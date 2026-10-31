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
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

        [STAThread]
        static int Main(string[] args)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            Log.FilePath = Path.Combine(dir, "gui-error.log");
            Tray = args.Any(a => a.Equals("-Tray", StringComparison.OrdinalIgnoreCase));
            AutoStart = args.Any(a => a.Equals("-AutoStart", StringComparison.OrdinalIgnoreCase));

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
