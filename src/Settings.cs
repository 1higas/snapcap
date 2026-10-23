using Microsoft.Win32;

namespace ChargeLimit
{
    // The firmware cannot be asked for its current limit, so remember what was last applied.
    class Settings
    {
        const string KeyPath = @"Software\ChargeLimit";

        public bool Known;
        public int Target = Limits.MaxTarget;
        public int Delta = Limits.DefaultDelta;
        public int LastLimit = Limits.DefaultTarget;

        public bool Limited { get { return Target < Limits.MaxTarget; } }

        public static Settings Load()
        {
            Settings s = new Settings();
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath))
            {
                if (key == null)
                    return s;
                s.Known = true;
                s.Target = Limits.ClampTarget((int)key.GetValue("Target", Limits.MaxTarget));
                s.Delta = Limits.ClampDelta(s.Target, (int)key.GetValue("Delta", Limits.DefaultDelta));
                s.LastLimit = Limits.ClampTarget((int)key.GetValue("LastLimit", Limits.DefaultTarget));
            }
            return s;
        }

        public static void SaveApplied(int target, int delta)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                key.SetValue("Target", target, RegistryValueKind.DWord);
                key.SetValue("Delta", delta, RegistryValueKind.DWord);
                if (target < Limits.MaxTarget)
                    key.SetValue("LastLimit", target, RegistryValueKind.DWord);
            }
        }
    }

    // Same bounds Linux's qcom_battmgr applies: stop threshold 55-100%, resume threshold at least 50%.
    static class Limits
    {
        public const int MinTarget = 55, MaxTarget = 100, DefaultTarget = 80;
        public const int MinDelta = 1, MaxDelta = 20, DefaultDelta = 5, MinResume = 50;

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
