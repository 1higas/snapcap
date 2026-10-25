using System;
using Microsoft.Win32;

namespace Snapcap
{
    enum IconMode { App, Watts }

    // The firmware cannot be asked for its current limit, so remember what was last applied.
    class Settings
    {
        const string KeyPath = @"Software\" + Program.Id;

        public bool Known;
        public int Target = Limits.MaxTarget;
        public int Delta = Limits.DefaultDelta;
        public int LastLimit = Limits.DefaultTarget;
        public IconMode Icon = IconMode.Watts;
        public int Interval = Limits.DefaultInterval;
        public int History = Limits.DefaultHistory;
        public bool CheckUpdates = true;
        public DateTime LastUpdateCheck;
        public string LatestVersion;
        public bool FirstRun = true;

        public bool Limited { get { return Target < Limits.MaxTarget; } }

        public static Settings Load()
        {
            Settings s = new Settings();
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath))
            {
                if (key == null)
                    return s;
                s.FirstRun = key.GetValue("Initialized") == null;
                s.Known = key.GetValue("Target") != null;
                s.Target = Limits.ClampTarget((int)key.GetValue("Target", Limits.MaxTarget));
                s.Delta = Limits.ClampDelta(s.Target, (int)key.GetValue("Delta", Limits.DefaultDelta));
                s.LastLimit = Limits.ClampTarget((int)key.GetValue("LastLimit", Limits.DefaultTarget));
                s.Icon = (int)key.GetValue("IconMode", (int)IconMode.Watts) == (int)IconMode.Watts ? IconMode.Watts : IconMode.App;
                s.Interval = System.Math.Max(1, (int)key.GetValue("Interval", Limits.DefaultInterval));
                s.CheckUpdates = (int)key.GetValue("CheckUpdates", 1) != 0;
                s.LastUpdateCheck = new DateTime((long)key.GetValue("LastUpdateCheck", 0L), DateTimeKind.Utc);
                s.LatestVersion = key.GetValue("LatestVersion") as string;
                s.History = System.Math.Max(1, System.Math.Min(Limits.MaxHistory, (int)key.GetValue("History", Limits.DefaultHistory)));
            }
            return s;
        }

        public void SaveApplied(int target, int delta)
        {
            Known = true;
            Target = target;
            Delta = delta;
            if (target < Limits.MaxTarget)
                LastLimit = target;
            Save("Target", target);
            Save("Delta", delta);
            Save("LastLimit", LastLimit);
        }

        // Set after the first start (or by the installer) so first-run defaults are applied only once.
        public static void MarkInitialized()
        {
            Save("Initialized", 1);
        }

        public void SaveUpdateCheck(DateTime when, string latest)
        {
            LastUpdateCheck = when;
            LatestVersion = latest;
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                key.SetValue("LastUpdateCheck", when.Ticks, RegistryValueKind.QWord);
                key.SetValue("LatestVersion", latest);
            }
        }

        public void SaveCheckUpdates(bool on)
        {
            CheckUpdates = on;
            Save("CheckUpdates", on ? 1 : 0);
        }

        public void SaveHistory(int minutes)
        {
            History = minutes;
            Save("History", minutes);
        }

        public void SaveInterval(int seconds)
        {
            Interval = seconds;
            Save("Interval", seconds);
        }

        public void SaveIcon(IconMode mode)
        {
            Icon = mode;
            Save("IconMode", (int)mode);
        }

        static void Save(string name, int value)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
                key.SetValue(name, value, RegistryValueKind.DWord);
        }

        public static void Delete()
        {
            Registry.CurrentUser.DeleteSubKeyTree(KeyPath, false);
        }
    }

    // Same bounds Linux's qcom_battmgr applies: stop threshold 55-100%, resume threshold at least 50%.
    // The UI offers 5% steps; the command line accepts any value in range.
    static class Limits
    {
        public const int MinTarget = 55, MaxTarget = 100, DefaultTarget = 80, Step = 5;
        public const int MinDelta = 1, MaxDelta = 20, DefaultDelta = 1, MinResume = 50;
        public static readonly int[] Deltas = { 1, 5, 10, 20 };
        // How often the battery readout (tray watts, flyout) refreshes, in seconds.
        public static readonly int[] Intervals = { 1, 2, 5, 10 };
        public const int DefaultInterval = 5;
        // Time span of the power chart, in minutes.
        public static readonly int[] Histories = { 5, 15, 60 };
        public const int DefaultHistory = 15, MaxHistory = 60;

        public static int MaxDeltaFor(int target)
        {
            return System.Math.Min(MaxDelta, target - MinResume);
        }

        public static bool IsValid(int target, int delta)
        {
            return target >= MinTarget && target <= MaxTarget && delta >= MinDelta && delta <= MaxDeltaFor(target);
        }

        public static int ClampTarget(int target)
        {
            return target < MinTarget ? MinTarget : target > MaxTarget ? MaxTarget : target;
        }

        public static int ClampDelta(int target, int delta)
        {
            int max = MaxDeltaFor(target);
            return delta < MinDelta ? MinDelta : delta > max ? max : delta;
        }
    }
}
