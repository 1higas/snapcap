using System;
using System.ComponentModel;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle(Snapcap.Program.Name)]
[assembly: AssemblyDescription("Battery charge limit for Snapdragon X laptops")]
[assembly: AssemblyProduct(Snapcap.Program.Name)]
[assembly: AssemblyCopyright("Copyright (c) 2026 Thiago Fonseca. MIT License.")]
[assembly: AssemblyVersion(Snapcap.Program.Version + ".0")]
[assembly: AssemblyFileVersion(Snapcap.Program.Version + ".0")]

namespace Snapcap
{
    static class Program
    {
        public const string Name = "Snapcap", Id = "Snapcap";
        public const string Version = "2.0.0";
        public const string Url = "https://github.com/1higas/snapcap";
        public const string LatestReleaseApi = "https://api.github.com/repos/1higas/snapcap/releases/latest";
        const int ExitInvalidArguments = 87; // ERROR_INVALID_PARAMETER

        // Snapcap.exe                       tray icon (+ flyout for the portable copy), or brings
        //                                   up the running one's flyout
        // Snapcap.exe --tray                tray icon only (used at sign-in)
        // Snapcap.exe --demo                made-up chart for screenshots (only in "build.cmd demo" builds)
        // Snapcap.exe --set 80 [--delta 5]  applies a limit without any UI
        // Snapcap.exe --off                 removes the limit
        // Snapcap.exe --uninstall           removes the installed copy (asks for admin rights)
        // Snapcap-Setup.exe                 installer (same binary, chosen by file name)
        // Snapcap-Portable.exe is that same binary too, run from anywhere.
        // Command-line modes exit with 0 or a Win32 error code.
        [STAThread]
        static int Main(string[] args)
        {
            bool trayOnly = args.Length == 1 && args[0] == "--tray";
            bool demo = false;
#if DEMO
            demo = args.Length == 1 && args[0] == "--demo";
#endif
            if (args.Length == 1 && args[0] == "--uninstall")
            {
                if (!Installer.IsAdmin)
                    return Installer.RunElevated("--uninstall", false); // it stops every Snapcap process, this one too
                Installer.Uninstall();
                return 0;
            }
            if (args.Length > 0 && !trayOnly && !demo)
                return RunCommandLine(args);
            if (Installer.IsSetup && !Installer.IsAdmin)
                return Installer.RunElevated("", false);

            Native.AllowDarkMenus();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (Installer.IsSetup)
            {
                Application.Run(new SetupForm());
                return 0;
            }

            bool first;
            using (Mutex running = new Mutex(true, Id + ".Tray", out first))
            using (EventWaitHandle show = new EventWaitHandle(false, EventResetMode.AutoReset, Id + ".Show"))
            {
                if (!first)
                {
                    if (!trayOnly)
                        show.Set();
                    return 0;
                }
                // The portable copy opens its flyout when started; the installed one waits in the tray.
                Application.Run(new TrayApp(!trayOnly && !Installer.IsInstalledCopy, show, demo));
            }
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

            bool prompted;
            int error = Installer.ApplyLimit(target, delta, out prompted);
            if (error == 0)
                Settings.Load().SaveApplied(target, delta);
            return error;
        }
    }
}
