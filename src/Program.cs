using System;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyTitle("Charge Limit")]
[assembly: AssemblyDescription("Battery charge limit for Snapdragon X laptops")]
[assembly: AssemblyProduct("Charge Limit")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Thiago Fonseca. MIT License.")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace ChargeLimit
{
    static class Program
    {
        const int ExitInvalidArguments = 87; // ERROR_INVALID_PARAMETER

        // ChargeLimit.exe                       opens the window
        // ChargeLimit.exe --set 80 [--delta 5]  applies a limit without a window
        // ChargeLimit.exe --off                 removes the limit
        // The exit code is 0 on success or a Win32 error code.
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0)
                return RunCommandLine(args);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        static int RunCommandLine(string[] args)
        {
            int target = -1, delta = Limits.DefaultDelta;
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].ToLowerInvariant();
                if (arg == "--off")
                    target = Limits.MaxTarget;
                else if (arg == "--set" && i + 1 < args.Length && int.TryParse(args[i + 1], out target))
                    i++;
                else if (arg == "--delta" && i + 1 < args.Length && int.TryParse(args[i + 1], out delta))
                    i++;
                else
                    return ExitInvalidArguments;
            }
            if (!Limits.IsValid(target, delta))
                return ExitInvalidArguments;

            try { Native.SetChargeLimit(target, delta); }
            catch (Win32Exception e) { return e.NativeErrorCode; }
            Settings.SaveApplied(target, delta);
            return 0;
        }
    }
}
