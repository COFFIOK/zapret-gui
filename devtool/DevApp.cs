// Zapret Dev — пульт разработчика Zapret GUI · by COFFIOK
// Сборка, тестовая копия, самопроверка, релиз с подписью ключом с флешки, статистика и чаты (Zapret Stats),
// состояние автосборки на GitHub. Лежит в корне папки ZapretDev, собирается devtool\build-devtool.cmd.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using C = ZapretStats.C;

[assembly: AssemblyTitle("Zapret Dev")]
[assembly: AssemblyProduct("Zapret Dev")]
[assembly: AssemblyCompany("COFFIOK")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace ZapretDevTool
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (s, e) => { MessageBox.Show(e.Exception.Message, "Zapret Dev"); e.Handled = true; };
            C.LoadFont();
            string root = FindRoot();
            if (root == null) return 0;
            ZapretStats.MainWin.DevRoot = root;
            app.Run(new ZapretStats.MainWin());
            return 0;
        }

        // где лежит папка ZapretDev: рядом с программой, в ZapretDev рядом с ней, запомненная — или спрашиваем один раз
        static string FindRoot()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string saved = Path.Combine(C.Dir, "devroot.txt");
            var cands = new List<string> { exeDir, Path.Combine(exeDir, "ZapretDev") };
            try { if (File.Exists(saved)) cands.Insert(0, File.ReadAllText(saved).Trim()); } catch { }
            foreach (var c in cands)
                if (!string.IsNullOrEmpty(c) && File.Exists(Path.Combine(c, "compile.cmd")) && Directory.Exists(Path.Combine(c, "src"))) return Remember(saved, c);
            MessageBox.Show("Покажи, где папка ZapretDev: выбери в ней файл compile.cmd.\nЭто нужно один раз — дальше Zapret Dev запомнит.", "Zapret Dev");
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Выбери compile.cmd в папке ZapretDev", Filter = "compile.cmd|compile.cmd", InitialDirectory = exeDir };
            if (dlg.ShowDialog() != true) return null;
            return Remember(saved, Path.GetDirectoryName(dlg.FileName));
        }

        static string Remember(string file, string root)
        {
            try { File.WriteAllText(file, root); } catch { }
            return root;
        }
    }

    // вкладка «Разработка» в окне Zapret Dev (рядом со «Статистикой» и «Чатами»)
    class DevView : DockPanel
    {
        const string Repo = "COFFIOK/zapret-gui";
        readonly Window owner;
        readonly string root;
        string P(string rel) { return Path.Combine(root, rel); }

        readonly TextBox log = new TextBox();
        readonly TextBlock keyState = new TextBlock(), keyHint = new TextBlock(), appVer = new TextBlock(), ciState = new TextBlock();
        readonly TextBox verBox = new TextBox();
        readonly WrapPanel keyBtns = new WrapPanel(), relBtns = new WrapPanel();
        readonly DispatcherTimer usbTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        RSACryptoServiceProvider signer;     // ключ открыт, пока вставлена флешка
        string signerFile;
        volatile bool busy;
        string lastBuiltVersion;
        bool started;

        public DevView(Window owner, string root)
        {
            this.owner = owner;
            this.root = root;

            log.IsReadOnly = true; log.FontFamily = C.Mono; log.FontSize = 12; log.Background = C.Card; log.Foreground = C.Text;
            log.BorderBrush = C.Line; log.BorderThickness = new Thickness(0, 2, 0, 0); log.Padding = new Thickness(16, 10, 16, 10);
            log.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; log.TextWrapping = TextWrapping.Wrap; log.Height = 220;
            DockPanel.SetDock(log, Dock.Bottom); Children.Add(log);

            var body = new StackPanel { Margin = new Thickness(24, 16, 24, 16) };
            var info = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            info.ColumnDefinitions.Add(new ColumnDefinition());
            info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            appVer.Foreground = C.Muted; appVer.FontSize = 12.5; appVer.VerticalAlignment = VerticalAlignment.Center;
            info.Children.Add(appVer);
            ciState.FontSize = 12.5; ciState.Foreground = C.Muted; ciState.Cursor = Cursors.Hand; ciState.VerticalAlignment = VerticalAlignment.Center;
            ciState.ToolTip = "Открыть автосборку на GitHub";
            ciState.MouseLeftButtonUp += (s, e) => Open("https://github.com/" + Repo + "/actions");
            Grid.SetColumn(ciState, 1); info.Children.Add(ciState);
            body.Children.Add(info);
            body.Children.Add(Card("Ключ подписи", KeyPanel()));
            body.Children.Add(Card("Разработка", DevPanel()));
            body.Children.Add(Card("Релиз", ReleasePanel()));
            Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });

            Loaded += (s, e) =>
            {
                if (started) return;
                started = true;
                if (!File.Exists(P("compile.cmd"))) Log("Не нашёл compile.cmd рядом с ZapretDev.exe — программа должна лежать в корне папки ZapretDev.");
                ReadAppVersion();
                UpdateKey();
                usbTimer.Tick += (s2, e2) => UpdateKey();
                usbTimer.Start();
                CheckCi();
                var ci = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
                ci.Tick += (s2, e2) => CheckCi();
                ci.Start();
                Log("Готов. Папка: " + root);
            };
            owner.Closed += (s, e) => LockKey();
        }

        // ---------- оформление ----------
        static UIElement Card(string title, UIElement content)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontFamily = C.Body, FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = C.Muted, Margin = new Thickness(0, 0, 0, 10) });
            sp.Children.Add(content);
            return new Border { Background = C.Card, BorderBrush = C.Line, BorderThickness = new Thickness(2), Padding = new Thickness(18, 14, 18, 16), Margin = new Thickness(0, 0, 0, 14), Child = sp };
        }

        static WrapPanel Row(params UIElement[] items)
        {
            var w = new WrapPanel { Margin = new Thickness(-8, 0, 0, 0) };
            foreach (var i in items) w.Children.Add(i);
            return w;
        }

        Button Btn(string text, bool primary, Action click)
        {
            return ZapretStats.MainWin.PxButton(text, primary, () =>
            {
                if (busy) { Log("Подожди — ещё идёт предыдущее действие."); return; }
                try { click(); } catch (Exception ex) { Log("Ошибка: " + ex.Message); }
            });
        }

        void Log(string s)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                log.AppendText((log.Text.Length > 0 ? "\r\n" : "") + "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s);
                log.ScrollToEnd();
            }));
        }

        static void Open(string target) { try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { } }

        // ---------- версия приложения ----------
        string CurrentVersion()
        {
            var m = Regex.Match(File.ReadAllText(P("src\\AssemblyInfo.cs")), "AssemblyVersion\\(\"(\\d+)\\.(\\d+)\\.(\\d+)");
            return m.Success ? m.Groups[1].Value + "." + m.Groups[2].Value + "." + m.Groups[3].Value : "?";
        }

        void ReadAppVersion()
        {
            try
            {
                string v = CurrentVersion();
                appVer.Text = "Zapret GUI " + v + " в коде · " + root;
                var p = v.Split('.');
                int z;
                if (p.Length == 3 && int.TryParse(p[2], out z) && string.IsNullOrEmpty(verBox.Text)) verBox.Text = p[0] + "." + p[1] + "." + (z + 1);
            }
            catch (Exception ex) { appVer.Text = "не прочитал версию: " + ex.Message; }
        }

        // ---------- ключ ----------
        UIElement KeyPanel()
        {
            var sp = new StackPanel();
            keyState.FontSize = 14; keyState.TextWrapping = TextWrapping.Wrap;
            keyHint.FontSize = 12.5; keyHint.Foreground = C.Muted; keyHint.TextWrapping = TextWrapping.Wrap; keyHint.Margin = new Thickness(0, 4, 0, 10);
            sp.Children.Add(keyState); sp.Children.Add(keyHint);
            keyBtns.Margin = new Thickness(-8, 0, 0, 0);
            sp.Children.Add(keyBtns);
            return sp;
        }

        string lastKeyView, autoTried;
        void UpdateKey()
        {
            string file = UsbKey.Find();
            if (signer != null && (file == null || !string.Equals(file, signerFile, StringComparison.OrdinalIgnoreCase)))
            {
                LockKey();
                Log("Флешку-ключ вынули — ключ закрыт.");
            }
            // флешка без пароля открывается сама, как только её вставили
            if (signer == null && file != null && autoTried != file)
            {
                autoTried = file;
                try { OpenWith(file, ""); Log("Флешка-ключ " + Path.GetPathRoot(file) + " принята."); } catch { }
            }
            if (file == null) autoTried = null;
            string pub = KeyInfo.ReadModulus(P("src\\ReleaseKey.cs"));
            string view = (file ?? "") + "|" + (signer != null) + "|" + pub;
            if (view == lastKeyView) return;
            lastKeyView = view;
            keyBtns.Children.Clear();

            if (signer != null)
            {
                string mine = Convert.ToBase64String(signer.ExportParameters(false).Modulus);
                bool match = mine == pub;
                keyState.Text = match ? "✓ Флешка-ключ вставлена — можно собирать релиз" : "⚠ Это флешка с другим ключом";
                keyState.Foreground = match ? C.Accent : C.Red;
                keyHint.Text = "Флешка " + Path.GetPathRoot(signerFile) + (match ? ". Вынешь — ключ закроется." :
                    ". Ключ на ней не совпадает с ключом в программе — подпись не пройдёт проверку. Если это твоя новая флешка-ключ, нажми «Использовать эту флешку».");
                if (!match) keyBtns.Children.Add(Btn("ИСПОЛЬЗОВАТЬ ЭТУ ФЛЕШКУ", true, () => ReplaceProgramKey(signer.ExportParameters(false))));
                keyBtns.Children.Add(Btn("КОПИЯ НА ВТОРУЮ ФЛЕШКУ", false, BackupKey));
            }
            else if (file != null)
            {
                keyState.Text = "Флешка-ключ " + Path.GetPathRoot(file) + " защищена паролем";
                keyState.Foreground = C.Text;
                keyHint.Text = "Введи пароль, чтобы открыть ключ.";
                keyBtns.Children.Add(Btn("ВВЕСТИ ПАРОЛЬ", true, UnlockKey));
            }
            else
            {
                keyState.Text = "Флешка-ключ не вставлена";
                keyState.Foreground = C.Muted;
                keyHint.Text = "Вставь флешку-ключ. Ещё нет такой? Вставь любую флешку и нажми «Сделать флешку ключом».";
                keyBtns.Children.Add(Btn("СДЕЛАТЬ ФЛЕШКУ КЛЮЧОМ", true, MakeKeyDrive));
                keyBtns.Children.Add(Btn("ИЗ РЕЗЕРВНОЙ КОПИИ", false, ImportKey));
            }
        }

        void OpenWith(string file, string pw)
        {
            string xml = UsbKey.Open(file, pw);
            var rsa = new RSACryptoServiceProvider { PersistKeyInCsp = false };
            rsa.FromXmlString(xml);
            LockKey(); signer = rsa; signerFile = file; lastKeyView = null;
        }

        void LockKey()
        {
            if (signer != null) { signer.Dispose(); signer = null; signerFile = null; }
        }

        void UnlockKey()
        {
            string file = UsbKey.Find();
            if (file == null) { Log("Флешка с ключом не найдена."); return; }
            string pw = PasswordDialog.Ask(owner, "Пароль ключа", "Пароль от ключа на флешке:");
            if (pw == null) return;
            busy = true;
            Log("Открываю ключ…");
            new Thread(() =>
            {
                try
                {
                    string xml = UsbKey.Open(file, pw);
                    Dispatcher.BeginInvoke(new Action(() => { OpenWith(file, pw); UpdateKey(); Log("Ключ открыт."); }));
                }
                catch (Exception ex) { Log("Не открылся: " + ex.Message); }
                finally { busy = false; }
            }) { IsBackground = true }.Start();
        }

        // пароль по желанию: пустой — флешка открывается сама; null — отмена
        string AskNewPassword()
        {
            string a = PasswordDialog.Ask(owner, "Пароль флешки-ключа",
                "Пароль — по желанию.\nОставь пустым — флешка будет приниматься сама, как только её вставишь.\nС паролем — даже потерянной флешкой никто не подпишет обновление.", true);
            if (a == null) return null;
            if (a.Length == 0) return "";
            if (a.Length < 6) { MessageBox.Show(owner, "Пароль короче 6 символов — придумай подлиннее или оставь пустым.", "Zapret Dev"); return null; }
            string b = PasswordDialog.Ask(owner, "Пароль флешки-ключа", "Повтори пароль:", false);
            if (b == null) return null;
            if (a != b) { MessageBox.Show(owner, "Пароли не совпали.", "Zapret Dev"); return null; }
            return a;
        }

        void SaveToUsb(string xml, string sourceNote, Action onSaved = null)
        {
            string drive = DrivePicker.Pick(owner);
            if (drive == null) return;
            string pw = AskNewPassword();
            if (pw == null) return;
            string file = Path.Combine(drive, UsbKey.Folder, UsbKey.FileName);
            if (File.Exists(file) && MessageBox.Show(owner, "На флешке " + drive + " уже есть ключ. Заменить его?", "Zapret Dev", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            busy = true;
            Log("Записываю ключ на флешку " + drive + "…");
            new Thread(() =>
            {
                try
                {
                    UsbKey.Save(file, xml, pw);
                    UsbKey.Open(file, pw);   // сразу проверяем, что открывается
                    autoTried = null;
                    if (onSaved != null) Dispatcher.Invoke(onSaved);
                    Log("Флешка " + drive + " теперь ключ (" + sourceNote + ")." + (pw.Length == 0 ? " Пароля нет — она будет приниматься сама." : "") + " Советую сделать копию на вторую флешку.");
                }
                catch (Exception ex) { Log("Не записался: " + ex.Message); }
                finally { busy = false; Dispatcher.BeginInvoke(new Action(() => { lastKeyView = null; UpdateKey(); })); }
            }) { IsBackground = true }.Start();
        }

        void ImportKey()
        {
            // 1) старое место (sign-keygen.cmd)
            string dpapi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZapretGUI-sign", "release-key.dpapi");
            string xml = null, note = null, plainFile = null;
            if (File.Exists(dpapi))
            {
                try
                {
                    xml = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(dpapi), null, DataProtectionScope.CurrentUser));
                    note = "из " + dpapi;
                }
                catch (Exception ex) { Log("Старый ключ не прочитался: " + ex.Message); }
            }
            // 2) резервная копия release-key-BACKUP.xml
            if (xml == null)
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Где лежит release-key-BACKUP.xml?", Filter = "Ключ (*.xml)|*.xml|Все файлы|*.*" };
                if (dlg.ShowDialog(owner) != true) return;
                xml = File.ReadAllText(dlg.FileName);
                note = "из " + Path.GetFileName(dlg.FileName);
                plainFile = dlg.FileName;
            }
            using (var rsa = new RSACryptoServiceProvider { PersistKeyInCsp = false })
            {
                rsa.FromXmlString(xml);
                if (rsa.PublicOnly) { Log("В этом файле нет секретной части ключа."); return; }
                string mod = Convert.ToBase64String(rsa.ExportParameters(false).Modulus);
                string pub = KeyInfo.ReadModulus(P("src\\ReleaseKey.cs"));
                if (!string.IsNullOrEmpty(pub) && mod != pub &&
                    MessageBox.Show(owner, "Этот ключ (" + KeyInfo.Fingerprint(mod) + ") не совпадает с ключом в программе (" + KeyInfo.Fingerprint(pub) + ").\nВсё равно перенести?", "Zapret Dev", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            }
            SaveToUsb(xml, note);
            if (plainFile != null)
                Log("Файл " + plainFile + " хранит ключ открытым текстом. Когда убедишься, что ключ на флешке открывается, удали этот файл.");
        }

        void MakeKeyDrive()
        {
            string pub = KeyInfo.ReadModulus(P("src\\ReleaseKey.cs"));
            // ключ, созданный раньше sign-keygen.cmd, если он ещё лежит на компьютере и совпадает с программой
            string dpapi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZapretGUI-sign", "release-key.dpapi");
            if (File.Exists(dpapi))
            {
                try
                {
                    string old = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(dpapi), null, DataProtectionScope.CurrentUser));
                    using (var r = new RSACryptoServiceProvider { PersistKeyInCsp = false })
                    {
                        r.FromXmlString(old);
                        if (Convert.ToBase64String(r.ExportParameters(false).Modulus) == pub) { SaveToUsb(old, "ключ с этого компьютера"); return; }
                    }
                }
                catch { }
            }
            // новый ключ; открытый — в программу (до первого подписанного релиза это безопасно)
            using (var rsa = new RSACryptoServiceProvider(3072) { PersistKeyInCsp = false })
            {
                string xml = rsa.ToXmlString(true);
                var pubKey = rsa.ExportParameters(false);
                // открытый ключ — в программу только после того, как флешка записана
                SaveToUsb(xml, "новый ключ", () =>
                {
                    KeyInfo.WriteReleaseKey(P("src\\ReleaseKey.cs"), pubKey);
                    lastKeyView = null;
                    Log("Открытый ключ " + KeyInfo.Fingerprint(Convert.ToBase64String(pubKey.Modulus)) + " записан в src\\ReleaseKey.cs — нажми «Собрать», чтобы он попал в приложение.");
                });
            }
        }

        void ReplaceProgramKey(RSAParameters pub)
        {
            if (MessageBox.Show(owner, "Записать открытый ключ с флешки в src\\ReleaseKey.cs?\n\nДелай это, только если релизов со старым ключом ещё не было.", "Zapret Dev", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            KeyInfo.WriteReleaseKey(P("src\\ReleaseKey.cs"), pub);
            Log("src\\ReleaseKey.cs обновлён. Пересобери приложение.");
            lastKeyView = null; UpdateKey();
        }

        void BackupKey()
        {
            if (signer == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Резервная копия ключа (зашифрована паролем)", FileName = "release-key.zkey", Filter = "Ключ Zapret (*.zkey)|*.zkey" };
            if (dlg.ShowDialog(owner) != true) return;
            if (dlg.FileName.IndexOf("OneDrive", StringComparison.OrdinalIgnoreCase) >= 0 &&
                MessageBox.Show(owner, "Это папка OneDrive — копия уйдёт в облако. Она зашифрована паролем, но надёжнее вторая флешка или менеджер паролей. Сохранить всё равно?", "Zapret Dev", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            string pw = AskNewPassword();
            if (pw == null) return;
            UsbKey.Save(dlg.FileName, signer.ToXmlString(true), pw);
            Log("Резервная копия сохранена: " + dlg.FileName);
        }

        // ---------- разработка ----------
        UIElement DevPanel()
        {
            return Row(
                Btn("СОБРАТЬ", true, () => Run("Сборка", () => Compile())),
                Btn("ЗАПУСТИТЬ ТЕСТОВУЮ КОПИЮ", false, () => { if (File.Exists(P("out\\ZapretCS.exe"))) Open(P("out\\ZapretCS.exe")); else Log("Сначала собери."); }),
                Btn("САМОПРОВЕРКА", false, () => Run("Самопроверка", () => { if (!Compile()) return; SelfTest(); })),
                Btn("ОТЧЁТ И СКРИНШОТЫ", false, () => { if (Directory.Exists(P("selftest"))) Open(P("selftest")); else Log("Самопроверку ещё не запускали."); }),
                Btn("ЖУРНАЛ ОШИБОК", false, () => { if (File.Exists(P("out\\gui-error.log"))) Open(P("out\\gui-error.log")); else Log("Журнала пока нет."); }),
                Btn("ПАПКА", false, () => Open(root)));
        }

        void Run(string what, Action work)
        {
            if (busy) { Log("Подожди — ещё идёт предыдущее действие."); return; }
            busy = true;
            Log("== " + what);
            new Thread(() =>
            {
                try { work(); }
                catch (Exception ex) { Log("ОШИБКА: " + ex.Message); }
                finally { busy = false; Dispatcher.BeginInvoke(new Action(() => { ReadAppVersion(); lastKeyView = null; UpdateKey(); })); }
            }) { IsBackground = true }.Start();
        }

        bool Compile()
        {
            var sw = Stopwatch.StartNew();
            var psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + P("compile.cmd") + "\"\"") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true };
            using (var p = Process.Start(psi)) p.WaitForExit();
            string bl = File.Exists(P("build.log")) ? File.ReadAllText(P("build.log"), Encoding.Default) : "";
            foreach (var line in bl.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0 && !l.Contains("build start"))) Log("  " + line);
            bool ok = bl.Contains("BUILD OK");
            Log(ok ? "Собрано за " + sw.Elapsed.TotalSeconds.ToString("0.0") + " с" : "НЕ СОБРАЛОСЬ — ошибки выше");
            return ok;
        }

        int SelfTest()
        {
            string dir = P("selftest");
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            var psi = new ProcessStartInfo(P("out\\ZapretSelfTest.exe"), "-selftest \"" + dir + "\"") { UseShellExecute = false, CreateNoWindow = true };
            int code;
            using (var p = Process.Start(psi)) { p.WaitForExit(); code = p.ExitCode; }
            string rep = Path.Combine(dir, "report.txt");
            if (File.Exists(rep)) foreach (var l in File.ReadAllLines(rep, Encoding.UTF8).Skip(1).Where(x => x.Trim().Length > 0).Take(40)) Log("  " + l);
            Log(code == 0 ? "Самопроверка: проблем нет" : "Самопроверка: проблем — " + code + " (кнопка «Отчёт и скриншоты»)");
            return code;
        }

        // ---------- релиз ----------
        UIElement ReleasePanel()
        {
            var sp = new StackPanel();
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            line.Children.Add(new TextBlock { Text = "Версия", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            verBox.Width = 110; verBox.FontFamily = C.Mono; verBox.FontSize = 14; verBox.Padding = new Thickness(6, 4, 6, 4);
            verBox.Background = C.Bg; verBox.Foreground = C.Text; verBox.BorderBrush = C.Line; verBox.BorderThickness = new Thickness(2); verBox.CaretBrush = C.Text;
            line.Children.Add(verBox);
            line.Children.Add(new TextBlock { Text = "сборка → самопроверка → установщик → подпись", Foreground = C.Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) });
            sp.Children.Add(line);
            relBtns.Margin = new Thickness(-8, 0, 0, 0);
            relBtns.Children.Add(Btn("СОБРАТЬ РЕЛИЗ", true, BuildRelease));
            relBtns.Children.Add(Btn("ЗАМЕТКИ К РЕЛИЗУ", false, () => Open(NotesFile(verBox.Text.Trim(), true))));
            relBtns.Children.Add(Btn("ПАПКА RELEASE", false, () => Open(P("release"))));
            relBtns.Children.Add(Btn("НОВЫЙ РЕЛИЗ НА GITHUB", false, OpenGitHubRelease));
            sp.Children.Add(relBtns);
            return sp;
        }

        string NotesFile(string v, bool create)
        {
            string f = P("release\\release-notes-v" + v + ".md");
            if (create && !File.Exists(f) && Regex.IsMatch(v, @"^\d+\.\d+\.\d+$"))
            {
                Directory.CreateDirectory(P("release"));
                File.WriteAllText(f, "# Zapret GUI v" + v + "\n\n### Новое\n- \n\n### Установка\nНажми «Обновить» в Zapret или скачай `ZapretSetup.exe` ниже. Сайты и настройки сохранятся.\n", new UTF8Encoding(false));
                Log("Создал заготовку " + Path.GetFileName(f) + " — впиши, что нового.");
            }
            return f;
        }

        void BuildRelease()
        {
            string v = verBox.Text.Trim().TrimStart('v', 'V');
            if (!Regex.IsMatch(v, @"^\d+\.\d+\.\d+$")) { Log("Версия должна быть вида 2.3.3"); return; }
            if (signer == null) { Log("Сначала открой ключ подписи (вставь флешку → «Открыть ключ»)."); return; }
            string pub = KeyInfo.ReadModulus(P("src\\ReleaseKey.cs"));
            if (Convert.ToBase64String(signer.ExportParameters(false).Modulus) != pub) { Log("Ключ на флешке не совпадает с ключом в src\\ReleaseKey.cs — подпись не пройдёт проверку в приложении."); return; }
            var rsa = signer;
            Run("Релиз " + v, () =>
            {
                // 1. версия — только в AssemblyInfo.cs
                string asm = P("src\\AssemblyInfo.cs");
                string text = File.ReadAllText(asm);
                text = Regex.Replace(text, "Assembly(File)?Version\\(\"[^\"]*\"\\)", m => "Assembly" + m.Groups[1].Value + "Version(\"" + v + ".0\")");
                File.WriteAllText(asm, text, new UTF8Encoding(true));
                Log("Версия " + v + " → src\\AssemblyInfo.cs");
                // 2. сборка
                if (!Compile()) return;
                // 3. самопроверка
                int problems = SelfTest();
                if (problems != 0)
                {
                    bool go = false;
                    Dispatcher.Invoke(new Action(() => go = MessageBox.Show(owner, "Самопроверка нашла проблем: " + problems + ".\nСобрать релиз всё равно?", "Zapret Dev", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes));
                    if (!go) { Log("Остановлено после самопроверки."); return; }
                }
                // 4. установщик
                string outExe = P("release\\ZapretSetup.exe");
                Log("Собираю установщик…");
                string args = "-NoProfile -ExecutionPolicy Bypass -File \"" + P("installer\\pack.ps1") + "\" -Version " + v +
                    " -Exe \"" + P("out\\Zapret.exe") + "\" -Ico \"" + P("zapret.ico") + "\" -Font \"" + P("ui\\ZPixel.ttf") + "\" -Stub \"" + P("installer\\stub.exe") + "\" -Out \"" + outExe + "\"";
                var psi = new ProcessStartInfo("powershell.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
                if (File.Exists(outExe)) File.Delete(outExe);
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd(), e = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    foreach (var l in (o + "\n" + e).Split('\n').Select(x => x.TrimEnd()).Where(x => x.Length > 0)) Log("  " + l);
                    if (p.ExitCode != 0 || !File.Exists(outExe)) { Log("Установщик не собрался."); return; }
                }
                // 5. подпись и проверка тем же способом, что в приложении (Signature.Verify)
                byte[] hash;
                using (var sha = SHA256.Create()) hash = sha.ComputeHash(File.ReadAllBytes(outExe));
                byte[] sig = rsa.SignHash(hash, CryptoConfig.MapNameToOID("SHA256"));
                using (var check = new RSACryptoServiceProvider { PersistKeyInCsp = false })
                {
                    check.ImportParameters(new RSAParameters { Modulus = Convert.FromBase64String(pub), Exponent = rsa.ExportParameters(false).Exponent });
                    if (!check.VerifyHash(hash, CryptoConfig.MapNameToOID("SHA256"), sig)) { Log("Подпись не проверилась — релиз не готов."); return; }
                }
                File.WriteAllText(outExe + ".sig", Convert.ToBase64String(sig), new UTF8Encoding(false));
                Log("Подписано: release\\ZapretSetup.exe.sig");
                Log("release\\ZapretSetup.exe · " + (new FileInfo(outExe).Length / 1048576.0).ToString("0.00") + " МБ · SHA-256 " + string.Concat(hash.Select(b => b.ToString("x2"))));
                NotesFile(v, true);
                lastBuiltVersion = v;
                Log("ГОТОВО: Zapret GUI v" + v + ". Дальше — «Новый релиз на GitHub»: приложи ОБА файла (ZapretSetup.exe и ZapretSetup.exe.sig).");
            });
        }

        void OpenGitHubRelease()
        {
            string v = lastBuiltVersion ?? verBox.Text.Trim();
            string body = "";
            string nf = NotesFile(v, false);
            if (File.Exists(nf))
            {
                var lines = File.ReadAllLines(nf, Encoding.UTF8).ToList();
                if (lines.Count > 0 && lines[0].StartsWith("# ")) lines.RemoveAt(0);
                body = string.Join("\n", lines).Trim();
            }
            Open("https://github.com/" + Repo + "/releases/new?tag=v" + Uri.EscapeDataString(v) + "&target=main&title=" + Uri.EscapeDataString("Zapret GUI v" + v) + "&body=" + Uri.EscapeDataString(body));
            if (File.Exists(P("release\\ZapretSetup.exe"))) Process.Start("explorer.exe", "/select,\"" + P("release\\ZapretSetup.exe") + "\"");
            Log("Открыл страницу релиза v" + v + ". Перетащи в неё ZapretSetup.exe и ZapretSetup.exe.sig из открывшейся папки.");
        }

        // ---------- автосборка на GitHub ----------
        void CheckCi()
        {
            new Thread(() =>
            {
                var parts = new List<string>();
                foreach (var br in new[] { "main", "dev" })
                {
                    try
                    {
                        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                        using (var wc = new WebClient())
                        {
                            wc.Headers[HttpRequestHeader.UserAgent] = "ZapretDev";
                            wc.Encoding = Encoding.UTF8;
                            var j = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(wc.DownloadString("https://api.github.com/repos/" + Repo + "/commits/" + br + "/check-runs"));
                            var runs = j["check_runs"] as object[];
                            if (runs == null || runs.Length == 0) { parts.Add(br + ": —"); continue; }
                            var r = (Dictionary<string, object>)runs[0];
                            string st = Convert.ToString(r["status"]), con = Convert.ToString(r["conclusion"]);
                            parts.Add(br + ": " + (st != "completed" ? "… идёт" : con == "success" ? "✓" : "✗ ошибка"));
                        }
                    }
                    catch (WebException ex)
                    {
                        var resp = ex.Response as HttpWebResponse;
                        parts.Add(br + ": " + (resp != null && (int)resp.StatusCode == 404 ? "нет ветки" : "нет связи"));
                    }
                    catch { parts.Add(br + ": ?"); }
                }
                Dispatcher.BeginInvoke(new Action(() => ciState.Text = "Автосборка на GitHub   " + string.Join("   ", parts)));
            }) { IsBackground = true }.Start();
        }
    }

    // окно ввода пароля; allowEmpty — пустой пароль допустим (вернёт ""), отмена — null
    static class PasswordDialog
    {
        public static string Ask(Window owner, string title, string text) { return Ask(owner, title, text, false); }

        public static string Ask(Window owner, string title, string text, bool allowEmpty)
        {
            var w = new Window
            {
                Title = title, Owner = owner, Width = 440, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = C.Card, Foreground = C.Text, FontFamily = C.Body, FontSize = 13.5, ShowInTaskbar = false
            };
            var sp = new StackPanel { Margin = new Thickness(20) };
            sp.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
            var pb = new PasswordBox { FontSize = 15, Padding = new Thickness(6, 4, 6, 4), Background = C.Bg, Foreground = C.Text, BorderBrush = C.Line, BorderThickness = new Thickness(2), CaretBrush = C.Text };
            sp.Children.Add(pb);
            string result = null;
            Action ok = () => { if (allowEmpty || pb.Password.Length > 0) { result = pb.Password; w.Close(); } };
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            row.Children.Add(ZapretStats.MainWin.PxButton("ОТМЕНА", false, () => w.Close()));
            row.Children.Add(ZapretStats.MainWin.PxButton(allowEmpty ? "ДАЛЬШЕ" : "OK", true, ok));
            sp.Children.Add(row);
            pb.KeyDown += (s, e) => { if (e.Key == Key.Enter) ok(); else if (e.Key == Key.Escape) w.Close(); };
            w.Content = sp;
            w.Loaded += (s, e) => pb.Focus();
            w.ShowDialog();
            return result;
        }
    }

    // выбор флешки, которая станет ключом
    static class DrivePicker
    {
        public static string Pick(Window owner)
        {
            var drives = new List<DriveInfo>();
            foreach (var d in DriveInfo.GetDrives())
                try { if (d.DriveType == DriveType.Removable && d.IsReady) drives.Add(d); } catch { }
            if (drives.Count == 0) { MessageBox.Show(owner, "Не вижу ни одной флешки. Вставь флешку и нажми ещё раз.", "Zapret Dev"); return null; }
            var w = new Window
            {
                Title = "Какую флешку сделать ключом?", Owner = owner, Width = 440, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = C.Card, Foreground = C.Text, FontFamily = C.Body, FontSize = 13.5, ShowInTaskbar = false
            };
            var sp = new StackPanel { Margin = new Thickness(20) };
            sp.Children.Add(new TextBlock { Text = "На флешку запишется папка ZapretKey с ключом. Остальные файлы на ней не тронутся.", TextWrapping = TextWrapping.Wrap, Foreground = C.Muted, Margin = new Thickness(0, 0, 0, 12) });
            string result = null;
            foreach (var d in drives)
            {
                var drive = d;
                string label = "";
                try { label = drive.VolumeLabel; } catch { }
                string text = drive.Name.TrimEnd('\\') + "  " + (string.IsNullOrEmpty(label) ? "Флешка" : label) + "  ·  " + (drive.TotalSize / 1073741824.0).ToString("0.#") + " ГБ";
                var b = ZapretStats.MainWin.PxButton(text, false, () => { result = drive.RootDirectory.FullName; w.Close(); });
                b.Margin = new Thickness(0, 0, 0, 8); b.HorizontalAlignment = HorizontalAlignment.Stretch;
                sp.Children.Add(b);
            }
            sp.Children.Add(ZapretStats.MainWin.PxButton("ОТМЕНА", false, () => w.Close()));
            w.Content = sp;
            w.ShowDialog();
            return result;
        }
    }
}
