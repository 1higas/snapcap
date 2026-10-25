using System;
using System.Windows.Forms;

namespace Snapcap
{
    // The panel that opens above the notification area, like Windows 11's quick settings.
    // Two pages: the limit, and settings behind the gear button. Every change applies at once;
    // the panel closes when it loses focus.
    class Flyout : FluentForm
    {
        const string GearGlyph = "", BackGlyph = "";

        readonly TrayApp app;
        readonly Headline headline;
        readonly TitleBar title;
        readonly TextItem note;
        readonly Notice update;
        readonly TextItem hint;
        readonly Toggle limit, autostart;
        readonly Segmented resume;
        readonly Slider target;
        readonly Item[] main, settings;
        string error;
        int pending;

        public Flyout(TrayApp app) : base(true)
        {
            this.app = app;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            Settings s = app.Settings;

            // On top: the panel is anchored to the bottom, so it grows upward and nothing moves.
            update = Add(new Notice());
            update.Link = "Download";
            title = Add(new TitleBar(Program.Name, GearGlyph, false));
            title.Gap = 12;
            LoadTitleIcon();
            headline = Add(new Headline());
            Chart chart = Add(new Chart());
            chart.Samples = app.History;
            chart.Minutes = s.History;
            chart.MaxGapSeconds = GapFor(s.Interval);
            Divider divider = Add(new Divider());
            limit = Add(new Toggle("Limit charging", s.Limited));
            limit.Gap = 16;
            target = Add(new Slider("Stop charging at", Limits.MinTarget, Limits.MaxTarget - Limits.Step, Limits.Step, s.Limited ? s.Target : s.LastLimit));
            target.Gap = 20;
            note = Add(new TextItem("", Small));
            note.Gap = 16;
            hint = Add(new TextItem(Installer.PromptsForChanges ? "Tip: the installed version (Snapcap-Setup) changes the limit without asking for permission each time." : null, Small));
            hint.Tint = Theme.Disabled;
            main = new Item[] { update, title, headline, chart, divider, limit, target, note, hint };

            TitleBar back = Add(new TitleBar("Settings", BackGlyph, true));
            Segmented icon = Add(new Segmented("Tray icon", new[] { "Watts", "App icon" }, s.Icon == IconMode.Watts ? 0 : 1));
            icon.Gap = 16;
            Segmented refresh = Add(new Segmented("Refresh every",
                Array.ConvertAll(Limits.Intervals, i => i + " s"), Array.IndexOf(Limits.Intervals, s.Interval)));
            refresh.Gap = 16;
            Segmented history = Add(new Segmented("Chart shows the last",
                Array.ConvertAll(Limits.Histories, m => m >= 60 ? "1 h" : m + " min"), Array.IndexOf(Limits.Histories, s.History)));
            history.Gap = 16;
            resume = Add(new Segmented("Resume charging after a drop of",
                Array.ConvertAll(Limits.Deltas, d => d + "%"), Array.IndexOf(Limits.Deltas, s.Delta)));
            resume.Gap = 16;
            autostart = Add(new Toggle("Start with Windows", Installer.AutostartEnabled));
            autostart.Gap = 20;
            Toggle updates = Add(new Toggle("Check for updates", s.CheckUpdates));
            updates.Gap = 20;
            TextItem version = Add(new TextItem(Program.Name + " " + Program.Version, Small));
            version.Tint = Theme.Secondary;
            version.Gap = 20;
            settings = new Item[] { back, icon, refresh, history, resume, autostart, updates, version };

            title.Clicked += delegate { ShowPage(true); };
            back.Clicked += delegate { ShowPage(false); };
            limit.Changed += on => Apply();
            target.Changed += Apply;
            resume.Changed += i => Apply();
            icon.Changed += i => app.SetIconMode(i == 0 ? IconMode.Watts : IconMode.App);
            refresh.Changed += i =>
            {
                app.SetInterval(Limits.Intervals[i]);
                chart.MaxGapSeconds = GapFor(Limits.Intervals[i]);
            };
            history.Changed += i =>
            {
                app.SetHistory(Limits.Histories[i]);
                chart.Minutes = Limits.Histories[i];
            };
            updates.Changed += on => app.SetCheckUpdates(on);
            update.Clicked += delegate
            {
                app.OpenUpdate();
                Close();
            };
            autostart.Changed += on =>
            {
                error = app.SetAutostart(on);
                autostart.On = Installer.AutostartEnabled;
                Sync();
            };

            app.Updated += ShowBattery;
            app.Refresh();
            ShowPage(false);
        }

        void LoadTitleIcon()
        {
            if (title.Image != null)
                title.Image.Dispose();
            using (System.Drawing.Icon small = Native.AppIcon(Px(20)))
                title.Image = small == null ? null : small.ToBitmap();
        }

        protected override void Rescale()
        {
            LoadTitleIcon();
            Sync();
        }

        public void ShowNearTray()
        {
            Show();
            Reposition();
            Activate();
        }

        // Keeps the panel anchored to the bottom-right corner when its height changes.
        void Reposition()
        {
            System.Drawing.Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new System.Drawing.Point(area.Right - Width - Px(12), area.Bottom - Height - Px(12));
        }

        void ShowPage(bool showSettings)
        {
            foreach (Item item in main)
                item.Visible = !showSettings;
            foreach (Item item in settings)
                item.Visible = showSettings;
            Sync();
        }

        void Apply()
        {
            int delta = Limits.ClampDelta(target.Value, Limits.Deltas[Math.Max(0, resume.Selected)]);
            pending++;
            error = null;
            app.Apply(limit.On ? target.Value : Limits.MaxTarget, delta, message =>
            {
                if (IsDisposed)
                    return;
                pending--;
                error = message;
                limit.On = app.Settings.Limited;
                resume.Selected = Array.IndexOf(Limits.Deltas, app.Settings.Delta);
                Sync();
            });
            Sync();
        }

        void Sync()
        {
            target.Enabled = limit.On;
            title.Status = pending > 0 ? "Applying…" : null;
            note.Text = error ?? (app.Settings.Known ? null : "The firmware doesn't report its current limit; pick one to apply it.");
            note.Visible = note.Text != null && headline.Visible;
            update.Visible = app.UpdateVersion != null && headline.Visible;
            hint.Visible = hint.Text != null && headline.Visible;
            update.Text = Program.Name + " " + app.UpdateVersion + " is available";
            note.Tint = error != null ? Theme.Error : Theme.Secondary;
            Relayout();
            if (Visible)
                Reposition();
        }

        // A longer pause between readings than this (sleep, for example) breaks the chart line.
        static int GapFor(int interval)
        {
            return Math.Max(15, 3 * interval);
        }

        public void ShowError(string message)
        {
            error = message;
            Sync();
        }

        void ShowBattery()
        {
            headline.Value = app.BatteryKnown ? app.Battery.Percent + "%" : "—";
            headline.Detail = app.StateText();
            if (update.Visible != (app.UpdateVersion != null && headline.Visible))
                Sync();
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Escape)
                return;
            if (headline.Visible)
                Close();
            else
                ShowPage(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                app.Updated -= ShowBattery;
            }
            base.Dispose(disposing);
        }
    }
}
