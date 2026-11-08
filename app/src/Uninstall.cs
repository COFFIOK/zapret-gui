// Zapret (C#) — удаление из настроек: полностью или с сохранением zapret от Flowseal на рабочем столе
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace ZapretCS
{
    partial class MainUi
    {
        bool unKeep = true, unBusy;

        void WireUninstall()
        {
            Btn("UninstBtn").Click += (s, e) => Safe(() =>
            {
                if (!InHome) { Toast("Удаление доступно только для установленного Zapret", true); return; }
                CloseSettings(); OpenUninstall();
            });
            El("UnKeep").MouseLeftButtonUp += (s, e) => { if (!unBusy) SetUnChoice(true); };
            El("UnAll").MouseLeftButtonUp += (s, e) => { if (!unBusy) SetUnChoice(false); };
            Btn("UninstClose").Click += (s, e) => { if (!unBusy) CloseOverlay("Uninst"); };
            Btn("UninstCancel").Click += (s, e) => { if (!unBusy) CloseOverlay("Uninst"); };
            El("UninstDim").MouseLeftButtonUp += (s, e) => { if (!unBusy) CloseOverlay("Uninst"); };
            Btn("UninstGo").Click += (s, e) => Safe(StartUninstall);
        }

        bool ZapretInsideHome
        {
            get
            {
                if (string.IsNullOrEmpty(ZapretCore.Root)) return false;
                string home = System.IO.Path.GetFullPath(HomeDir).TrimEnd('\\') + "\\";
                return System.IO.Path.GetFullPath(ZapretCore.Root).StartsWith(home, StringComparison.OrdinalIgnoreCase);
            }
        }

        void OpenUninstall()
        {
            unBusy = false;
            SetUnChoice(true);
            string name = string.IsNullOrEmpty(ZapretCore.Root) ? "zapret" : System.IO.Path.GetFileName(ZapretCore.Root);
            Tb("UnKeepSub").Text = Translate.T(ZapretInsideHome
                ? "Удалится только приложение. Папка «" + name + "» со всеми твоими сайтами и настройками переедет на рабочий стол."
                : "Удалится только приложение. Папка zapret останется на месте: " + ZapretCore.Root);
            Tb("UninstStatus").Text = "";
            El("UninstChoice").IsEnabled = true;
            Btn("UninstGo").IsEnabled = true; Btn("UninstGo").Content = Translate.T("Удалить");
            Btn("UninstCancel").IsEnabled = true;
            OpenOverlay("Uninst");
        }

        void SetUnChoice(bool keep)
        {
            unKeep = keep;
            foreach (var k in new[] { "UnKeep", "UnAll" })
            {
                bool on = (k == "UnKeep") == keep;
                var b = E<Border>(k);
                b.BorderBrush = Theme.B(on ? (k == "UnAll" ? "#E0566A" : "#3DDC97") : "#2E3340");
                b.Background = Theme.B(on ? (k == "UnAll" ? "#2A1E22" : "#1B3329") : "#1E2129");
                var dot = El(k + "Dot");
                ((System.Windows.Shapes.Shape)dot).Fill = Theme.B(k == "UnAll" ? "#E0566A" : "#3DDC97");
                Anim.D(dot, Anim.OP, on ? 1 : 0, 200);
            }
            Tb("UninstStatus").Text = keep ? "" : Translate.T("Твои списки сайтов тоже удалятся. Если хочешь их сохранить — сначала поделись ими кодом.");
            Tb("UninstStatus").Foreground = Theme.B(keep ? "#8A90A0" : "#E0566A");
        }

        void UnStatus(string text) { OnUi(() => { Tb("UninstStatus").Foreground = Theme.B("#8A90A0"); Tb("UninstStatus").Text = Translate.T(text); }); }

        void StartUninstall()
        {
            if (unBusy) return;
            unBusy = true;
            bool keep = unKeep;
            El("UninstChoice").IsEnabled = false;
            Btn("UninstGo").IsEnabled = false; Btn("UninstCancel").IsEnabled = false;
            Btn("UninstGo").Content = Translate.T("Удаляю…");
            string home = HomeDir, root = ZapretCore.Root, strategy = S.Str("strategy");
            bool rootInside = ZapretInsideHome;
            try { StopTg(); } catch { }
            try { LanEt.Stop(); LanEt.RemoveFirewall(); } catch { }
            string kept = null;
            Background(() =>
            {
                UnStatus("Останавливаю zapret…");
                bool svc; ZapretCore.Stop(out svc);
                foreach (var p in Process.GetProcessesByName("TgWsProxy_windows")) { try { p.Kill(); } catch { } p.Dispose(); }
                Thread.Sleep(600);

                if (keep && rootInside && Directory.Exists(root))
                {
                    UnStatus("Переношу zapret на рабочий стол…");
                    string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    string dst = System.IO.Path.Combine(desk, System.IO.Path.GetFileName(root));
                    for (int i = 2; Directory.Exists(dst) || File.Exists(dst); i++) dst = System.IO.Path.Combine(desk, System.IO.Path.GetFileName(root) + " (" + i + ")");
                    CopyDir(root, dst);
                    WriteHowTo(dst, strategy);
                    kept = dst;
                }
                else if (keep && !rootInside) kept = root;

                UnStatus("Убираю службы, задачи и ярлыки…");
                // служба zapret и драйвер WinDivert указывали на удаляемую папку — убираем
                foreach (var sv in new[] { "zapret", "WinDivert", "WinDivert14" }) { Run("sc.exe", "stop " + sv); Run("sc.exe", "delete " + sv); }
                Run("netsh.exe", "advfirewall firewall delete rule name=\"Zapret Telegram proxy\"");
                foreach (var tn in new[] { "ZapretGUI", "ZapretGUI Autostart", "ZapretCS GUI", "ZapretCS Autostart", "ZapretCS Dev" }) DeleteTask(tn);
                foreach (var l in new[] {
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Zapret.lnk"),
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Zapret.lnk") })
                    try { File.Delete(l); } catch { }
                try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\ZapretGUI", false); } catch { }

                // zapret снаружи папки Zapret при полном удалении тоже убираем
                if (!keep && !rootInside && !string.IsNullOrEmpty(root) && Directory.Exists(root) && File.Exists(System.IO.Path.Combine(root, "bin\\winws.exe")))
                    try { Directory.Delete(root, true); } catch { }
                UnStatus("Готово, закрываю Zapret…");
            }, err =>
            {
                if (err != null)
                {
                    unBusy = false;
                    El("UninstChoice").IsEnabled = true; Btn("UninstGo").IsEnabled = true; Btn("UninstCancel").IsEnabled = true;
                    Btn("UninstGo").Content = Translate.T("Удалить");
                    Tb("UninstStatus").Foreground = Theme.B("#E0566A");
                    Tb("UninstStatus").Text = Translate.T("Не получилось: " + err.Message);
                    return;
                }
                // саму папку (вместе с работающим Zapret.exe) удаляет cmd, когда приложение закроется
                Process.Start(new ProcessStartInfo("cmd.exe", "/c timeout /t 3 /nobreak >nul & rmdir /s /q \"" + home + "\"") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = System.IO.Path.GetTempPath() });
                string msg = kept != null
                    ? "Zapret удалён. Zapret от Flowseal со всеми твоими сайтами остался здесь:\n" + kept + "\n\nЧтобы включить его, запусти нужный .bat (подсказка — в файле «Как запустить.txt»)."
                    : "Zapret полностью удалён.";
                try { trayIcon.Visible = false; } catch { }
                Win.Hide();
                MessageBox.Show(Translate.T(msg), "Zapret", MessageBoxButton.OK, MessageBoxImage.Information);
                if (kept != null && Directory.Exists(kept)) try { Process.Start("explorer.exe", "\"" + kept + "\""); } catch { }
                Environment.Exit(0);
            });
        }

        static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var d in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(System.IO.Path.Combine(dst, d.Substring(src.Length).TrimStart('\\')));
            foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                File.Copy(f, System.IO.Path.Combine(dst, f.Substring(src.Length).TrimStart('\\')), true);
        }

        static void WriteHowTo(string dir, string strategy)
        {
            string bat = null;
            if (!string.IsNullOrEmpty(strategy))
                bat = Directory.GetFiles(dir, "*.bat").Select(System.IO.Path.GetFileName)
                    .FirstOrDefault(f => string.Equals(System.IO.Path.GetFileNameWithoutExtension(f), strategy, StringComparison.OrdinalIgnoreCase));
            var sb = new StringBuilder();
            sb.AppendLine("Zapret от Flowseal — твои сайты и настройки сохранены.");
            sb.AppendLine();
            sb.AppendLine("Как включить:");
            sb.AppendLine(bat != null
                ? "  запусти «" + bat + "» — эту стратегию ты выбирал в Zapret GUI."
                : "  запусти один из файлов general*.bat (например, «general.bat»).");
            sb.AppendLine("  Окно с логом не закрывай: пока оно открыто, zapret работает.");
            sb.AppendLine();
            sb.AppendLine("Чтобы zapret включался сам вместе с Windows — запусти service.bat и выбери установку службы.");
            sb.AppendLine("Твои сайты лежат в lists\\list-general-user.txt.");
            sb.AppendLine();
            sb.AppendLine("Вернуть удобное приложение: https://github.com/COFFIOK/zapret-gui/releases/latest");
            File.WriteAllText(System.IO.Path.Combine(dir, "Как запустить.txt"), sb.ToString(), new UTF8Encoding(true));
        }
    }
}
