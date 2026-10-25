using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Snapcap
{
    struct Sample
    {
        public DateTime Time;
        public int Mw;
    }

    // Lives in the notification area. Left click opens the flyout; right click offers Exit.
    // The battery is read once per refresh interval (default 5 s) for the icon and the power
    // history; nothing else runs in the background. Updates are only checked when the flyout
    // opens, at most once a day.
    class TrayApp : ApplicationContext
    {
        public readonly Settings Settings = Settings.Load();
        public BatteryState Battery;
        public bool BatteryKnown;
        public event Action Updated;
        public readonly Queue<Sample> History = new Queue<Sample>();

        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly Control invoker = new Control();
        readonly RegisteredWaitHandle showRequests;
        Flyout flyout;
        Icon appIcon, wattsIcon;
        Sample lastSample;
        int lastRead, flyoutClosedAt = -1000;
        bool checkingForUpdate;

        public TrayApp(bool showFlyout, WaitHandle showRequest, bool demo)
        {
            GC.KeepAlive(invoker.Handle); // creates the window BeginInvoke posts to
            showRequests = ThreadPool.RegisterWaitForSingleObject(showRequest, delegate { invoker.BeginInvoke((Action)ShowFlyout); }, null, -1, false);

            // Everything lives in the flyout; the right-click menu only exits.
            tray.ContextMenu = new ContextMenu(new[] { new MenuItem("Exit", delegate { Exit(); }) });
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleFlyout(); };
            tray.MouseMove += delegate { if (Environment.TickCount - lastRead > 2000) Refresh(); };
            timer.Interval = Settings.Interval * 1000;
            timer.Tick += delegate { Refresh(); };
            SystemEvents.UserPreferenceChanged += OnSystemChange;
            SystemEvents.PowerModeChanged += OnSystemChange;
            SystemEvents.DisplaySettingsChanged += OnDisplayChange;

            if (Settings.FirstRun)
            {
                SetAutostart(true);
                Settings.MarkInitialized();
            }
            SetIconMode(Settings.Icon);
            timer.Start();
#if DEMO
            if (demo)
                SeedDemo();
#endif
            tray.Visible = true;
            if (showFlyout)
                ShowFlyout();
            Native.TrimMemory();
        }

        public void Refresh()
        {
            lastRead = Environment.TickCount;
            try
            {
                Battery = Native.ReadBattery();
                BatteryKnown = true;
                Record(Battery.RateMw);
            }
            catch (Win32Exception) { BatteryKnown = false; }

            string tip = BatteryKnown ? string.Format("Battery {0}% · {1}", Battery.Percent, StateText()) : "Could not read the battery";
            if (Settings.Limited)
                tip += string.Format(" · Limit {0}%", Settings.Target);
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            if (Settings.Icon == IconMode.Watts)
                ShowWatts();
            if (Updated != null)
                Updated();
        }

        public string StateText()
        {
            if (!BatteryKnown)
                return "Could not read the battery";
            // The rate is the battery's own power: what goes into or out of it, not what the
            // charger delivers (which also powers the laptop).
            string state = !Battery.Online ? "On battery" : Battery.Charging ? "Charging" : "Plugged in";
            return BatteryWatts == 0 ? state + " · battery idle" : state + " · battery " + Chart.Watts(Battery.RateMw);
        }

        int BatteryWatts
        {
            get { return Math.Min(99, (int)Math.Round(Math.Abs(Battery.RateMw) / 1000.0)); }
        }

        // Applies the limit in the background (going through the scheduled task takes about a
        // second), then reports an error message, or null when the firmware accepted it.
        public void Apply(int target, int delta, Action<string> done)
        {
            bool flyoutOpen = flyout != null;
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool prompted;
                int error = Installer.ApplyLimit(target, delta, out prompted);
                invoker.BeginInvoke((Action)delegate
                {
                    if (error == 0)
                        Settings.SaveApplied(target, delta);
                    Refresh();
                    string message = error == 0 ? null : "Could not apply the limit: " + Describe(error);
                    // A UAC prompt takes the focus, which closes the flyout: bring it back.
                    if (prompted && flyoutOpen && flyout == null)
                    {
                        ShowFlyout();
                        flyout.ShowError(message);
                    }
                    else
                        done(message);
                });
            });
        }

        public string SetAutostart(bool on)
        {
            try { Installer.SetAutostart(on, Application.ExecutablePath); }
            catch (Exception e) { return string.Format("Could not change Start with Windows: {0}", e.Message); }
            return null;
        }

        public void SetIconMode(IconMode mode)
        {
            if (mode != Settings.Icon)
                Settings.SaveIcon(mode);
            if (mode == IconMode.App)
                SetIcon(AppIcon(), null);
            Refresh();
        }

        // Sized for the taskbar's scale now, which can change while the app runs.
        Icon AppIcon()
        {
            int size = Native.TrayIconSize();
            if (appIcon == null || appIcon.Width != size)
            {
                if (appIcon != null)
                {
                    Native.DestroyIcon(appIcon.Handle);
                    appIcon.Dispose();
                }
                appIcon = Native.AppIcon(size);
            }
            return appIcon;
        }

        // Keeps up to MaxHistory minutes of readings for the chart; extra reads (hover, menu)
        // closer than half a second to the previous one are skipped.
        void Record(int mw)
        {
            DateTime now = DateTime.UtcNow;
            if (History.Count > 0 && (now - lastSample.Time).TotalMilliseconds < 500)
                return;
            lastSample.Time = now;
            // Readings under half a watt are noise while the battery rests (the icon shows idle too).
            lastSample.Mw = Math.Abs(mw) < 500 ? 0 : mw;
            History.Enqueue(lastSample);
            while (History.Peek().Time < now.AddMinutes(-Limits.MaxHistory))
                History.Dequeue();
        }

        public void SetCheckUpdates(bool on)
        {
            Settings.SaveCheckUpdates(on);
            CheckForUpdate();
            Refresh();
        }

        // The newer release seen on GitHub, if any (and if update checks are on).
        public string UpdateVersion
        {
            get
            {
                Version latest;
                return Settings.CheckUpdates && Version.TryParse(Settings.LatestVersion, out latest) && latest > new Version(Program.Version)
                    ? Settings.LatestVersion : null;
            }
        }

        // Asks GitHub for the latest release, at most once a day, on a background thread with
        // a short timeout. Failures are ignored (and retried the next time the flyout opens).
        void CheckForUpdate()
        {
            if (!Settings.CheckUpdates || checkingForUpdate || DateTime.UtcNow - Settings.LastUpdateCheck < TimeSpan.FromHours(24))
                return;
            checkingForUpdate = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string tag = null;
                try
                {
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2, which GitHub requires
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(Program.LatestReleaseApi);
                    request.UserAgent = Program.Name + "/" + Program.Version;
                    request.Timeout = request.ReadWriteTimeout = 10000;
                    using (WebResponse response = request.GetResponse())
                    using (System.IO.StreamReader reader = new System.IO.StreamReader(response.GetResponseStream()))
                    {
                        Match match = Regex.Match(reader.ReadToEnd(), "\"tag_name\"\\s*:\\s*\"v?([0-9.]+)\"");
                        if (match.Success)
                            tag = match.Groups[1].Value;
                    }
                }
                catch (Exception) { }
                try
                {
                    invoker.BeginInvoke((Action)delegate
                    {
                        checkingForUpdate = false;
                        if (tag != null)
                            Settings.SaveUpdateCheck(DateTime.UtcNow, tag);
                        Refresh();
                        Native.TrimMemory();
                    });
                }
                catch (Exception) { } // exiting
            });
        }

        public void OpenUpdate()
        {
            Process.Start(Program.Url + "/releases/latest");
        }

#if DEMO
        // Chart for screenshots (build.cmd demo, then --demo), traced from a real session: idle at
        // the limit, unplugged for a while, plugged back in charging in steps, then tapering off.
        // Pairs of (fraction of the chart's time span, watts), interpolated linearly.
        static readonly double[] DemoTrace =
        {
            0, 0, 0.16, 0, 0.17, -5.5, 0.19, -6.8, 0.3, -7.4, 0.4, -7.9, 0.5, -8.3, 0.555, -8.4,
            0.56, 4.5, 0.58, 6.5, 0.6, 8, 0.61, 11.5, 0.625, 12, 0.635, 15.5, 0.65, 16, 0.66, 19.5,
            0.675, 20, 0.685, 22.5, 0.695, 23, 0.7, 15, 0.715, 11, 0.73, 8.5, 0.75, 6, 0.78, 3.8,
            0.82, 2.4, 0.86, 1.4, 0.9, 0.7, 0.94, 0.2, 0.96, 0, 1, 0,
        };

        void SeedDemo()
        {
            DateTime now = DateTime.UtcNow;
            History.Clear();
            double span = Settings.History * 60;
            for (double t = 0; t < span; t += Settings.Interval)
            {
                double f = t / span;
                int i = 2;
                while (DemoTrace[i] < f)
                    i += 2;
                double f0 = DemoTrace[i - 2], w0 = DemoTrace[i - 1], f1 = DemoTrace[i], w1 = DemoTrace[i + 1];
                lastSample.Time = now.AddSeconds(t - span);
                lastSample.Mw = (int)(1000 * (w0 + (w1 - w0) * (f - f0) / (f1 - f0)));
                History.Enqueue(lastSample);
            }
        }
#endif

        public void SetHistory(int minutes)
        {
            Settings.SaveHistory(minutes);
        }

        public void SetInterval(int seconds)
        {
            Settings.SaveInterval(seconds);
            timer.Interval = seconds * 1000;
        }

        static string Describe(int error)
        {
            const int ERROR_FILE_NOT_FOUND = 2, ERROR_CANCELLED = 1223;
            if (error == ERROR_FILE_NOT_FOUND) return "Qualcomm PMIC GLink driver (QCOMBATTMGR) not found. Snapcap only works on Snapdragon X laptops.";
            if (error == ERROR_CANCELLED) return "Windows asks for administrator approval once, to let Snapcap talk to the battery firmware.";
            return new Win32Exception(error).Message;
        }

        void ShowWatts()
        {
            // Nothing flowing (typically held at the limit): the app icon reads better than a lone 0.
            if (!BatteryKnown || BatteryWatts == 0)
            {
                SetIcon(AppIcon(), null);
                return;
            }
            bool light = Theme.IsLight("SystemUsesLightTheme");
            Color color = Theme.Power(Battery.RateMw > 0, !light);
            SetIcon(AppIcon(), RenderNumber(BatteryWatts.ToString(), color, Native.TrayIconSize()));
        }

        // Draws the number as large as the icon allows, centered on its own outline.
        static Icon RenderNumber(string text, Color color, int size)
        {
            using (Bitmap bmp = new Bitmap(size, size))
            using (Graphics g = Graphics.FromImage(bmp))
            using (GraphicsPath path = new GraphicsPath())
            using (SolidBrush brush = new SolidBrush(color))
            {
                FontFamily family;
                try { family = new FontFamily("Segoe UI Variable Display Semib"); }
                catch (ArgumentException) { family = new FontFamily("Segoe UI Semibold"); }
                path.AddString(text, family, 0, 100, Point.Empty, StringFormat.GenericTypographic);
                family.Dispose();
                RectangleF b = path.GetBounds();
                float scale = Math.Min(size / b.Width, size * 0.8f / b.Height);
                using (Matrix m = new Matrix())
                {
                    m.Translate(size / 2f, size / 2f);
                    m.Scale(scale, scale);
                    m.Translate(-b.X - b.Width / 2, -b.Y - b.Height / 2);
                    path.Transform(m);
                }
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(brush, path);
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        void SetIcon(Icon fallback, Icon rendered)
        {
            tray.Icon = rendered ?? fallback;
            if (wattsIcon != null)
            {
                Native.DestroyIcon(wattsIcon.Handle);
                wattsIcon.Dispose();
            }
            wattsIcon = rendered;
        }

        void ToggleFlyout()
        {
            if (flyout != null)
                flyout.Close();
            else if (Environment.TickCount - flyoutClosedAt > 300) // the click that closed it by deactivation
                ShowFlyout();
        }

        void ShowFlyout()
        {
            if (flyout == null)
            {
                flyout = new Flyout(this);
                flyout.FormClosed += delegate
                {
                    flyout = null;
                    flyoutClosedAt = Environment.TickCount;
                    Native.TrimMemory();
                };
            }
            flyout.ShowNearTray();
            CheckForUpdate();
        }

        void OnSystemChange(object sender, EventArgs e)
        {
            invoker.BeginInvoke((Action)delegate { Native.AllowDarkMenus(); Refresh(); });
        }

        void OnDisplayChange(object sender, EventArgs e)
        {
            invoker.BeginInvoke((Action)delegate { SetIconMode(Settings.Icon); });
        }

        void Exit()
        {
            SystemEvents.UserPreferenceChanged -= OnSystemChange;
            SystemEvents.PowerModeChanged -= OnSystemChange;
            SystemEvents.DisplaySettingsChanged -= OnDisplayChange;
            showRequests.Unregister(null);
            timer.Dispose();
            if (flyout != null)
                flyout.Close();
            tray.Visible = false;
            tray.Dispose();
            ExitThread();
        }
    }
}
