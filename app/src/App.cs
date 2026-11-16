// Zapret (C#) — точка входа: одна копия приложения, обработка ошибок, запуск окна
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace ZapretCS
{
    static class Program
    {
        public static Mutex InstMutex;
        public static EventWaitHandle ShowEvent;
        public static bool Tray, AutoStart;
        public static string InstallZapret;   // -InstallZapret <версия> — поставить выбранную версию zapret (Zapret Dev)
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

        [STAThread]
        static int Main(string[] args)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            Log.FilePath = Path.Combine(dir, "gui-error.log");
            Tray = args.Any(a => a.Equals("-Tray", StringComparison.OrdinalIgnoreCase));
            AutoStart = args.Any(a => a.Equals("-AutoStart", StringComparison.OrdinalIgnoreCase));

            // самопроверка интерфейса (Zapret Dev и автосборка на GitHub): -selftest <папка для отчёта>
            int st = Array.FindIndex(args, a => a.Equals("-selftest", StringComparison.OrdinalIgnoreCase));
            if (st >= 0)
            {
                string outDir = Path.GetFullPath(st + 1 < args.Length ? args[st + 1] : Path.Combine(dir, "selftest"));
                Directory.CreateDirectory(outDir);
                Log.FilePath = Path.Combine(outDir, "selftest-error.log");
                new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                return MainUi.SelfTest(outDir);
            }

            int iz = Array.FindIndex(args, a => a.Equals("-InstallZapret", StringComparison.OrdinalIgnoreCase));
            if (iz >= 0 && iz + 1 < args.Length && System.Text.RegularExpressions.Regex.IsMatch(args[iz + 1], @"^[0-9A-Za-z._-]{1,40}$")) InstallZapret = args[iz + 1];

            // одна копия: повторный запуск просто показывает уже открытое окно
            bool created;
            string id = System.Diagnostics.Process.GetCurrentProcess().ProcessName.Equals("ZapretCS", StringComparison.OrdinalIgnoreCase) ? "ZapretCS" : "Zapret2";
            InstMutex = new Mutex(true, "Local\\" + id + ".Instance", out created);
            ShowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\" + id + ".Show");
            if (!created)
            {
                try { created = InstMutex.WaitOne(1500); } catch (AbandonedMutexException) { created = true; }
                if (!created)
                {
                    // разрешаем уже открытому окну выйти на передний план (иначе Windows только мигнёт значком)
                    try { AllowSetForegroundWindow(-1); } catch { }
                    // уже открытое окно поставит выбранную версию zapret (см. MainUi.CheckInstallRequest)
                    if (InstallZapret != null) try { File.WriteAllText(Path.Combine(dir, "install-zapret.request"), InstallZapret); } catch { }
                    ShowEvent.Set(); return 0;
                }
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("crash: " + e.ExceptionObject);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (s, e) =>
            {
                Log.Write("ui: " + e.Exception);
                try { if (MainUi.Current != null) MainUi.Current.Toast("Ошибка: " + e.Exception.Message, true); } catch { }
                e.Handled = true;
            };
            try
            {
                var ui = new MainUi(dir);
                ui.Run(app);
            }
            catch (Exception ex)
            {
                Log.Write("start: " + ex);
                MessageBox.Show("Ошибка запуска:\n" + ex.Message + "\n\nПодробности в gui-error.log", "Zapret", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            try { InstMutex.ReleaseMutex(); } catch { }
            return 0;
        }
    }
}
