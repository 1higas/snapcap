using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Snapcap
{
    // Snapcap runs as a normal user. Only the firmware request needs admin rights. When Snapcap
    // is installed, it goes through an on-demand scheduled task, registered by the installer,
    // that runs "Program Files\Snapcap\Snapcap.exe --set <target> --delta <delta>" with the
    // highest privileges, so no prompt is needed. The task only ever points at the copy in
    // Program Files, which only admins can change: a task pointing at a user-writable exe would
    // let any program running as the user swap the file (or plant a DLL next to it) and get admin
    // rights without a prompt. Anything that can trigger the task can only pass two numbers that
    // the command line validates. Without an installation, each change asks for admin rights.
    //
    // Snapcap-Setup.exe and Snapcap-Portable.exe are the same binary; the word "setup" in the
    // file name makes it install itself to Program Files as Snapcap.exe.
    static class Installer
    {
        const string TaskName = Program.Name;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + Program.Id;
        const int TASK_STATE_RUNNING = 4, ERROR_FILE_NOT_FOUND = 2, ERROR_TIMEOUT = 1460;
        const int TaskFileMissing = unchecked((int)0x80070002);
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Program.Name);
        static readonly string Exe = Path.Combine(Dir, Program.Id + ".exe");
        static readonly string Shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Program.Name + ".lnk");

        public static bool IsSetup
        {
            get { return Path.GetFileName(Application.ExecutablePath).IndexOf("setup", StringComparison.OrdinalIgnoreCase) >= 0; }
        }

        public static bool IsInstalledCopy
        {
            get { return string.Equals(Path.GetFullPath(Application.ExecutablePath), Exe, StringComparison.OrdinalIgnoreCase); }
        }

        // True when changing the limit will show a UAC prompt (portable, nothing installed).
        public static bool PromptsForChanges
        {
            get { return !IsAdmin && FindApplyTask() == null; }
        }

        public static bool IsAdmin
        {
            get { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        }

        // Starts this exe elevated (one UAC prompt) and returns its exit code, or 0 right away
        // when not waiting; 1223 if the prompt is declined.
        public static int RunElevated(string args, bool wait)
        {
            ProcessStartInfo info = new ProcessStartInfo(Application.ExecutablePath, args);
            info.Verb = "runas";
            try
            {
                using (Process p = Process.Start(info))
                {
                    if (!wait)
                        return 0;
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (Win32Exception e) { return e.NativeErrorCode; }
        }

        // Sets the firmware limit; returns 0 or a Win32 error code. prompted tells whether
        // Windows showed a UAC prompt for it.
        public static int ApplyLimit(int target, int delta, out bool prompted)
        {
            prompted = false;
            if (IsAdmin)
            {
                try { Native.SetChargeLimit(target, delta); }
                catch (Win32Exception e) { return e.NativeErrorCode; }
                return 0;
            }
            int result = RunApplyTask(target, delta);
            if (result == ERROR_FILE_NOT_FOUND || result == TaskFileMissing)
            {
                prompted = true;
                result = RunElevated(string.Format("--set {0} --delta {1}", target, delta), true);
            }
            return result;
        }

        // The installed copy's task, or null. Only a task that runs the exe in Program Files is trusted.
        static object FindApplyTask()
        {
            try
            {
                object service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                Com(service, "Connect", BindingFlags.InvokeMethod);
                object folder = Com(service, "GetFolder", BindingFlags.InvokeMethod, "\\");
                object task = Com(folder, "GetTask", BindingFlags.InvokeMethod, TaskName);
                string definition = (string)Com(task, "Xml", BindingFlags.GetProperty);
                return definition.IndexOf("\"" + SecurityElement.Escape(Exe) + "\"", StringComparison.OrdinalIgnoreCase) < 0 ? null : task;
            }
            catch (TargetInvocationException) { return null; }
        }

        // Runs the task through the Task Scheduler API and waits for its exit code.
        static int RunApplyTask(int target, int delta)
        {
            object task = FindApplyTask();
            if (task == null)
                return ERROR_FILE_NOT_FOUND;

            object previous = Com(task, "LastRunTime", BindingFlags.GetProperty);
            Com(task, "Run", BindingFlags.InvokeMethod, new object[] { new[] { target.ToString(), delta.ToString() } });
            // Usually ~0.2 s, but the first run after installing can take much longer while
            // Windows Defender checks the new exe.
            bool seenRunning = false;
            for (int i = 0; i < 1200; i++)
            {
                Thread.Sleep(50);
                bool running = (int)Com(task, "State", BindingFlags.GetProperty) == TASK_STATE_RUNNING;
                seenRunning |= running;
                if (!running && (seenRunning || !Com(task, "LastRunTime", BindingFlags.GetProperty).Equals(previous)))
                    return (int)Com(task, "LastTaskResult", BindingFlags.GetProperty);
            }
            return ERROR_TIMEOUT;
        }

        // Elevated: (re)creates the on-demand task for the installed exe.
        static void RegisterApplyTask()
        {
            string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                "<Principals><Principal><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
                "<Settings><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
                "<ExecutionTimeLimit>PT1M</ExecutionTimeLimit><MultipleInstancesPolicy>Queue</MultipleInstancesPolicy></Settings>" +
                "<Actions><Exec><Command>\"" + SecurityElement.Escape(Exe) + "\"</Command><Arguments>--set $(Arg0) --delta $(Arg1)</Arguments></Exec></Actions></Task>";
            string file = Path.GetTempFileName();
            try
            {
                File.WriteAllText(file, xml, Encoding.Unicode);
                Schtasks("/create /tn \"" + TaskName + "\" /xml \"" + file + "\" /f");
            }
            finally { File.Delete(file); }
        }

        static void Schtasks(string args)
        {
            ProcessStartInfo info = new ProcessStartInfo("schtasks.exe", args);
            info.CreateNoWindow = true;
            info.UseShellExecute = false;
            info.RedirectStandardError = true;
            using (Process p = Process.Start(info))
            {
                string message = p.StandardError.ReadToEnd().Trim();
                p.WaitForExit();
                if (p.ExitCode != 0)
                    throw new Exception(message.Length > 0 ? message : "schtasks " + p.ExitCode);
            }
        }

        public static bool AutostartEnabled
        {
            get
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(Program.Name) != null;
            }
        }

        // A per-user Run entry: the tray app needs no admin rights, so no task is involved.
        public static void SetAutostart(bool on, string exe)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                if (on)
                    k.SetValue(Program.Name, "\"" + exe + "\" --tray");
                else
                    k.DeleteValue(Program.Name, false);
        }

        // Elevated (the setup asks for it).
        public static void Install(bool autostart)
        {
            StopOtherInstances();
            Directory.CreateDirectory(Dir);
            File.Copy(Application.ExecutablePath, Exe, true);

            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            object link = Com(shell, "CreateShortcut", BindingFlags.InvokeMethod, Shortcut);
            Com(link, "TargetPath", BindingFlags.SetProperty, Exe);
            Com(link, "Save", BindingFlags.InvokeMethod);

            using (RegistryKey k = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", Program.Name);
                k.SetValue("DisplayVersion", Program.Version);
                k.SetValue("Publisher", "Thiago Fonseca");
                k.SetValue("URLInfoAbout", Program.Url);
                k.SetValue("DisplayIcon", Exe);
                k.SetValue("InstallLocation", Dir);
                k.SetValue("UninstallString", "\"" + Exe + "\" --uninstall");
                k.SetValue("EstimatedSize", (int)(new FileInfo(Exe).Length / 1024 + 1), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            RegisterApplyTask();
            SetAutostart(autostart, Exe);
            Settings.MarkInitialized();
            // Through Explorer, so the tray app starts without the installer's admin rights.
            Process.Start("explorer.exe", "\"" + Exe + "\"");
            // Then ask it to open its flyout (it starts in the tray), letting it take the focus.
            Native.AllowSetForegroundWindow(-1);
            for (int i = 0; i < 50; i++)
            {
                EventWaitHandle show;
                if (EventWaitHandle.TryOpenExisting(Program.Id + ".Show", out show))
                {
                    using (show)
                        show.Set();
                    break;
                }
                Thread.Sleep(200);
            }
        }

        // Elevated (Main asks for it).
        public static void Uninstall()
        {
            StopOtherInstances();
            SetAutostart(false, null);
            try { Schtasks("/delete /tn \"" + TaskName + "\" /f"); }
            catch (Exception) { }
            File.Delete(Shortcut);
            Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false);
            Settings.Delete();
            // This exe may be the one being removed, so let cmd delete the folder after we exit.
            ProcessStartInfo info = new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + Dir + "\"");
            info.CreateNoWindow = true;
            info.UseShellExecute = false;
            Process.Start(info);
        }

        static object Com(object target, string member, BindingFlags kind, params object[] args)
        {
            return target.GetType().InvokeMember(member, kind, null, target, args);
        }

        static void StopOtherInstances()
        {
            int self = Process.GetCurrentProcess().Id;
            foreach (string name in new[] { Program.Id, Program.Id + "-Portable" })
                foreach (Process p in Process.GetProcessesByName(name))
                    using (p)
                        if (p.Id != self)
                            try { p.Kill(); p.WaitForExit(3000); }
                            catch (Win32Exception) { }
        }
    }

    class SetupForm : FluentForm
    {
        readonly Toggle autostart;
        readonly TextItem error;
        readonly Buttons buttons;

        public SetupForm() : base(false)
        {
            Text = "Install " + Program.Name;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ContentWidth = 420;

            TextItem title = Add(new TextItem(Program.Name, Display));
            title.Gap = 0;
            Add(new TextItem(string.Format("Installs version {0} to {1} and adds it to the Start menu. To uninstall, use Settings › Apps.", Program.Version, Installer.Dir), Body)).Tint = Theme.Secondary;
            autostart = Add(new Toggle("Open in the system tray when I sign in", true));
            autostart.Gap = 20;
            error = Add(new TextItem("", Body));
            error.Tint = Theme.Error;
            error.Visible = false;
            buttons = Add(new Buttons(new[] { "Install", "Cancel" }, new Action[] { Install, Close }));
            Relayout();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && buttons.Enabled)
                Install();
            else if (e.KeyCode == Keys.Escape)
                Close();
        }

        void Install()
        {
            buttons.Enabled = false;
            Refresh();
            try
            {
                Installer.Install(autostart.On);
                Close();
            }
            catch (Exception e)
            {
                error.Text = string.Format("Installation failed: {0}", e.Message);
                error.Visible = true;
                buttons.Enabled = true;
                Relayout();
            }
        }
    }
}
