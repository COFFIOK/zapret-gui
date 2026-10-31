// Zapret (C#) — панель настроек: вид, игровой фильтр, IPSet, запуск, задачи Windows
using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ZapretCS
{
    partial class MainUi
    {
        static readonly string[] AccentChoices = { "#3DDC97", "#5ED3F3", "#4DA3FF", "#B57BFF", "#FF7AB6", "#FF9A4D", "#FFD166" };
        int setPage;
        string UpdFile { get { return System.IO.Path.Combine(ZapretCore.Utils, "check_updates.enabled"); } }
        string IpsetFile { get { return System.IO.Path.Combine(ZapretCore.Lists, "ipset-all.txt"); } }

        // Пока C#-версия тестовая, её задачи Windows называются иначе, чтобы не трогать задачи старой версии
        static bool IsTestBuild { get { return Process.GetCurrentProcess().ProcessName.Equals("ZapretCS", StringComparison.OrdinalIgnoreCase); } }
        static string TaskName { get { return IsTestBuild ? "ZapretCS GUI" : "ZapretGUI"; } }
        static string AutoRunTaskName { get { return IsTestBuild ? "ZapretCS Autostart" : "ZapretGUI Autostart"; } }

        void WireSettings()
        {
            var cfgV = ZapretCore.LocalVersion;
            Tb("InfoPath").Text = ZapretCore.Root;
            Tb("InfoVer").Text = "zapret " + cfgV;
            Tb("InfoApp").Text = "Zapret GUI v" + AppVersion + " · by COFFIOK";

            loading = true;
            SelectByTag(E<ComboBox>("ThemeBox"), Theme.Light ? "light" : "dark");
            SelectByTag(E<ComboBox>("LangBox"), Translate.En ? "en" : "ru");
            string gm, gt, gu; ZapretCore.ReadGame(out gm, out gt, out gu);
            SelectByTag(E<ComboBox>("GameBox"), gm);
            if (E<ComboBox>("GameBox").SelectedItem == null) E<ComboBox>("GameBox").SelectedIndex = 0;
            SelectByTag(E<ComboBox>("IpsetBox"), GetIpset());
            E<CheckBox>("UpdToggle").IsChecked = File.Exists(UpdFile);
            E<CheckBox>("WinToggle").IsChecked = S.Bool("showWindow");
            // задачи Windows проверяем в фоне: schtasks небыстрый, окно ждать не должно
            bool hasAuto = false;
            Background(() =>
            {
                // запуск без запроса прав включён всегда
                try { EnableNoPrompt(); } catch (Exception ex) { Log.Write("noprompt: " + ex.Message); }
                hasAuto = TaskExists(AutoRunTaskName);
                if (hasAuto) { try { EnableAutoRun(); } catch { } }   // путь мог поменяться
            }, err => { loading = true; E<CheckBox>("AutoRunToggle").IsChecked = hasAuto; loading = false; });
            FillAccents();
            loading = false;

            E<ComboBox>("ThemeBox").SelectionChanged += (s, e) => Safe(() => { if (loading) return; var v = Tag(E<ComboBox>("ThemeBox")); if (v != (Theme.Light ? "light" : "dark")) { S.Set("theme", v); RestartApp(); } });
            E<ComboBox>("LangBox").SelectionChanged += (s, e) => Safe(() => { if (loading) return; var v = Tag(E<ComboBox>("LangBox")); if (v != (Translate.En ? "en" : "ru")) { S.Set("lang", v); RestartApp(); } });
            E<ComboBox>("GameBox").SelectionChanged += (s, e) => Safe(() =>
            {
                if (loading) return;
                string m, t, u; ZapretCore.ReadGame(out m, out t, out u);
                File.WriteAllText(ZapretCore.GameFile, "mode=" + Tag(E<ComboBox>("GameBox")) + "\r\ntcp=" + t + "\r\nudp=" + u + "\r\n", Encoding.ASCII);
                Toast("Игровой фильтр: " + ((ComboBoxItem)E<ComboBox>("GameBox").SelectedItem).Content);
                MarkChanged();
            });
            E<ComboBox>("IpsetBox").SelectionChanged += (s, e) => Safe(() =>
            {
                if (loading) return;
                if (SetIpset(Tag(E<ComboBox>("IpsetBox")))) { Toast("IPSet: " + ((ComboBoxItem)E<ComboBox>("IpsetBox").SelectedItem).Content); MarkChanged(); }
                else { loading = true; SelectByTag(E<ComboBox>("IpsetBox"), GetIpset()); loading = false; }
            });
            E<CheckBox>("UpdToggle").Click += (s, e) => Safe(() =>
            {
                if (E<CheckBox>("UpdToggle").IsChecked == true) File.WriteAllText(UpdFile, "ENABLED \r\n", Encoding.ASCII);
                else if (File.Exists(UpdFile)) File.Delete(UpdFile);
            });
            // режим экономии: без анимаций и эффектов, применяется сразу
            var eco = E<CheckBox>("EcoToggle");
            eco.IsChecked = Anim.Eco;
            eco.Click += (s, e) => Safe(() =>
            {
                bool v = eco.IsChecked == true;
                S.Set("eco", v); S.Save();
                Anim.Eco = v;
                PauseGlow();
                if (v)
                {
                    // останавливаем всё, что крутится и мигает
                    Anim.Stop(A("SpinRot"), Anim.Ang);
                    Anim.Stop(E<Shape>("ZDraw"), Shape.StrokeDashOffsetProperty);
                    foreach (var n in new[] { "TgPlaneT" }) { Anim.Stop(A(n), Anim.TX); Anim.Stop(A(n), Anim.TY); }
                    Anim.Stop(Btn("TgConnect"), Anim.OP);
                    Toast("Режим экономии включён — анимации выключены");
                }
                else
                {
                    if (State == "on") ResumeGlow();
                    else if (State == "starting" || State == "stopping") Anim.D(A("SpinRot"), Anim.Ang, 360, 900, 0, forever: true, noEase: true);
                    Toast("Режим экономии выключен");
                }
            });
            E<CheckBox>("WinToggle").Click += (s, e) => Safe(() => { S.Set("showWindow", E<CheckBox>("WinToggle").IsChecked == true); S.Save(); MarkChanged(); });
            E<CheckBox>("AutoRunToggle").Click += (s, e) => Safe(() =>
            {
                if (E<CheckBox>("AutoRunToggle").IsChecked == true) { EnableAutoRun(); Toast("Готово: Zapret будет запускаться вместе с Windows"); }
                else { DeleteTask(AutoRunTaskName); Toast("Автозапуск выключен"); }
            });

            // открыть/закрыть панель
            var gear = Btn("GearBtn");
            gear.Click += (s, e) => Safe(() => { if (El("SettingsOverlay").Visibility == Visibility.Visible) CloseSettings(); else OpenSettings(); });
            gear.MouseEnter += (s, e) => { if (El("SettingsOverlay").Visibility != Visibility.Visible) Anim.D(A("GearRot"), Anim.Ang, 60, 400); };
            gear.MouseLeave += (s, e) => { if (El("SettingsOverlay").Visibility != Visibility.Visible) Anim.D(A("GearRot"), Anim.Ang, 0, 400); };
            Btn("SettingsClose").Click += (s, e) => CloseSettings();
            El("Dim").MouseLeftButtonUp += (s, e) => CloseSettings();
            Win.KeyDown += (s, e) => { if (e.Key == Key.Escape && El("SettingsOverlay").Visibility == Visibility.Visible) CloseSettings(); };
            for (int k = 0; k < 5; k++)
            {
                var nv = E<ToggleButton>("Nav" + k);
                if (nv == null) continue;
                nv.Checked += (s, e) => Safe(() => { if (El("SettingsOverlay").Visibility == Visibility.Visible) ShowSetPage(int.Parse((string)((FrameworkElement)s).Tag), false); });
            }
            Btn("RepoBtn").Click += (s, e) => Safe(() => Process.Start("https://github.com/" + UpdateRepo));
            Btn("ServiceBtn").Click += (s, e) => Safe(() => Process.Start(new ProcessStartInfo(System.IO.Path.Combine(ZapretCore.Root, "service.bat")) { WorkingDirectory = ZapretCore.Root, UseShellExecute = true }));
            Btn("FolderBtn").Click += (s, e) => Safe(() => Process.Start("explorer.exe", "\"" + (InHome ? HomeDir : ZapretCore.Root) + "\""));
            Btn("MigrateBtn").Click += (s, e) => Safe(() => { CloseSettings(); OpenMigrate(); });
            Btn("AppCheckBtn").Click += (s, e) => Safe(() => { CloseSettings(); Toast("Проверяю обновления приложения…"); StartAppUpdateCheck(true); });
        }
        static string Tag(ComboBox c) { var it = c.SelectedItem as ComboBoxItem; return it == null ? null : it.Tag as string; }

        void FillAccents()
        {
            var panel = E<Panel>("AccentPanel"); panel.Children.Clear();
            var tpl = (ControlTemplate)XamlReader.Parse("<ControlTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"Button\"><Grid><Ellipse Stroke=\"{TemplateBinding BorderBrush}\" StrokeThickness=\"2\"/><Ellipse Margin=\"4\" Fill=\"{TemplateBinding Background}\"/></Grid></ControlTemplate>");
            foreach (var c in AccentChoices)
            {
                var st = new ScaleTransform();
                var b = new Button { Width = 28, Height = 28, Margin = new Thickness(4, 0, 4, 4), Cursor = Cursors.Hand, Tag = c, ToolTip = c, Template = tpl, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = st };
                b.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c));
                b.BorderBrush = string.Equals(c, Theme.Accent, StringComparison.OrdinalIgnoreCase) ? Theme.B("#E6E8EE") : Brushes.Transparent;
                b.MouseEnter += (s, e) => { Anim.D(st, Anim.SX, 1.18, 160); Anim.D(st, Anim.SY, 1.18, 160); };
                b.MouseLeave += (s, e) => { Anim.D(st, Anim.SX, 1, 160); Anim.D(st, Anim.SY, 1, 160); };
                string col = c;
                b.Click += (s, e) => Safe(() => { if (!string.Equals(col, Theme.Accent, StringComparison.OrdinalIgnoreCase)) { S.Set("accent", col); RestartApp(); } });
                panel.Children.Add(b);
            }
        }

        void ShowSetPage(int i, bool noAnim)
        {
            for (int k = 0; k < 5; k++) { var p = El("SetPage" + k); if (p != null) p.Visibility = Visibility.Collapsed; }
            var pg = E<Panel>("SetPage" + i); pg.Visibility = Visibility.Visible;
            setPage = i;
            if (noAnim) return;
            int j = 0;
            foreach (UIElement ch in pg.Children)
            {
                ch.Opacity = 0; var tt = new TranslateTransform(); ch.RenderTransform = tt;
                Anim.D(ch, Anim.OP, 1, 260, 0, begin: j * 45);
                Anim.D(tt, Anim.TX, 0, 320, 18, begin: j * 45);
                j++;
            }
        }
        void OpenSettings()
        {
            El("SettingsOverlay").Visibility = Visibility.Visible;
            Anim.D(A("Dim"), Anim.OP, 1, 260, 0);
            Anim.D(A("SettingsShift"), Anim.TX, 0, 400, 620);
            Anim.D(A("GearRot"), Anim.Ang, 180, 500);
            int i = 0;
            foreach (UIElement nv in E<Panel>("SetNav").Children)
            {
                nv.Opacity = 0; var tt = new TranslateTransform(); nv.RenderTransform = tt;
                Anim.D(nv, Anim.OP, 1, 260, 0, begin: 120 + i * 40);
                Anim.D(tt, Anim.TX, 0, 320, -14, begin: 120 + i * 40);
                i++;
            }
            ShowSetPage(setPage, false);
            Translate.Tree(El("SettingsOverlay"));
        }
        public void CloseSettings()
        {
            Anim.D(A("Dim"), Anim.OP, 0, 220);
            Anim.D(A("SettingsShift"), Anim.TX, 620, 300);
            Anim.D(A("GearRot"), Anim.Ang, 0, 450);
            After(290, () => El("SettingsOverlay").Visibility = Visibility.Collapsed);
        }

        // перезапуск приложения (смена темы, языка, акцента, обновление zapret)
        public void RestartApp()
        {
            S.Save();
            Toast("Перезапускаю приложение…");
            try { Program.InstMutex.ReleaseMutex(); } catch { }
            Process.Start(Process.GetCurrentProcess().MainModule.FileName);
            After(700, () => Win.Close());
        }

        // ---- IPSet ----
        string GetIpset()
        {
            if (!File.Exists(IpsetFile)) return "any";
            var lines = File.ReadAllLines(IpsetFile);
            if (lines.Length == 0) return "any";
            foreach (var l in lines) if (l.Trim() == "203.0.113.113/32") return "none";
            return "loaded";
        }
        bool SetIpset(string target)
        {
            string cur = GetIpset(), backup = IpsetFile + ".backup";
            if (cur == target) return true;
            if (target == "loaded")
            {
                if (!File.Exists(backup)) { Toast("Нет сохранённого списка IP. Обнови его: «Сервис и диагностика» → пункт 8.", true); return false; }
                if (File.Exists(IpsetFile)) File.Delete(IpsetFile);
                File.Move(backup, IpsetFile);
                return true;
            }
            if (cur == "loaded") File.Copy(IpsetFile, backup, true);
            File.WriteAllText(IpsetFile, target == "none" ? "203.0.113.113/32\r\n" : "", Encoding.ASCII);
            return true;
        }

        // ---- задачи Планировщика Windows (запуск без запроса прав и автозапуск) ----
        static int Run(string exe, string args)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }))
                { p.StandardOutput.ReadToEnd(); p.WaitForExit(15000); return p.ExitCode; }
            }
            catch { return -1; }
        }
        static bool TaskExists(string name) { return Run("schtasks.exe", "/query /tn \"" + name + "\"") == 0; }
        static void DeleteTask(string name) { if (TaskExists(name)) Run("schtasks.exe", "/delete /tn \"" + name + "\" /f"); }
        static void RegisterTask(string name, string args, bool atLogon, string description)
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
                "<WorkingDirectory>" + SecurityElement.Escape(System.IO.Path.GetDirectoryName(exe)) + "</WorkingDirectory></Exec></Actions></Task>";
            // файл задачи кладём в защищённую папку: из %TEMP% его могла бы подменить другая программа
            string dir = Sec.TempDir("task");
            string tmp = System.IO.Path.Combine(dir, "task.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            try { if (Run("schtasks.exe", "/create /tn \"" + name + "\" /xml \"" + tmp + "\" /f") != 0) throw new Exception("не удалось создать задачу Windows"); }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
        void EnableNoPrompt()
        {
            // пересоздаём всегда (в фоне): у старых версий задача запускала PowerShell
            RegisterTask(TaskName, "", false, "Запуск приложения Zapret без запроса прав администратора");
        }
        void EnableAutoRun() { RegisterTask(AutoRunTaskName, "-AutoStart -Tray", true, "Запуск Zapret вместе с Windows"); }
    }
}
