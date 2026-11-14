// Zapret (C#) — задачи Планировщика Windows и запуск системных команд.
// Отдельно от окна (MainUi): эта часть не трогает интерфейс, её можно использовать откуда угодно.
using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;

namespace ZapretCS
{
    static class WinTasks
    {
        // Тестовая сборка (ZapretCS.exe) создаёт задачи с другими именами, чтобы не трогать задачи установленной версии
        public static bool IsTestBuild { get { return Process.GetCurrentProcess().ProcessName.Equals("ZapretCS", StringComparison.OrdinalIgnoreCase); } }
        public static string NoPromptName { get { return IsTestBuild ? "ZapretCS GUI" : "ZapretGUI"; } }
        public static string AutoRunName { get { return IsTestBuild ? "ZapretCS Autostart" : "ZapretGUI Autostart"; } }

        // запуск команды без окна; код выхода или -1, если запустить не удалось
        public static int Run(string exe, string args)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }))
                { p.StandardOutput.ReadToEnd(); p.WaitForExit(15000); return p.ExitCode; }
            }
            catch (Exception logEx) { Log.Err("run " + exe, logEx); return -1; }
        }

        public static bool Exists(string name) { return Run("schtasks.exe", "/query /tn \"" + name + "\"") == 0; }
        public static void Delete(string name) { if (Exists(name)) Run("schtasks.exe", "/delete /tn \"" + name + "\" /f"); }

        public static void Register(string name, string args, bool atLogon, string description)
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            string user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            string trigger = atLogon ? "<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + SecurityElement.Escape(user) + "</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>" : "";
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                "<RegistrationInfo><Description>" + SecurityElement.Escape(description) + "</Description></RegistrationInfo>" + trigger +
                "<Principals><Principal id=\"Author\"><UserId>" + SecurityElement.Escape(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
                "<Settings><MultipleInstancesPolicy>" + (args.Length == 0 ? "Parallel" : "IgnoreNew") + "</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
                "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>4</Priority></Settings>" +
                "<Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(exe) + "</Command>" + (args.Length > 0 ? "<Arguments>" + SecurityElement.Escape(args) + "</Arguments>" : "") +
                "<WorkingDirectory>" + SecurityElement.Escape(Path.GetDirectoryName(exe)) + "</WorkingDirectory></Exec></Actions></Task>";
            // файл задачи кладём в защищённую папку: из %TEMP% его могла бы подменить другая программа
            string dir = Sec.TempDir("task");
            string tmp = Path.Combine(dir, "task.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            try { if (Run("schtasks.exe", "/create /tn \"" + name + "\" /xml \"" + tmp + "\" /f") != 0) throw new Exception("не удалось создать задачу Windows"); }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // запуск приложения без запроса прав (пересоздаём всегда: у старых версий задача запускала PowerShell)
        public static void EnableNoPrompt() { Register(NoPromptName, "", false, "Запуск приложения Zapret без запроса прав администратора"); }
        public static void EnableAutoRun() { Register(AutoRunName, "-AutoStart -Tray", true, "Запуск Zapret вместе с Windows"); }
    }
}
