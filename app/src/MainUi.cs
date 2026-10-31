// Zapret (C#) — главное окно: разметка из XAML, кнопка zapret, стратегии, анимации, закрытие
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ZapretCS
{
    partial class MainUi
    {
        public static MainUi Current;
        public const string AppVersion = "2.0.0";

        public readonly string GuiDir, HomeDir;
        public readonly bool InHome;
        public Settings S;
        public Window Win;
        public Monitor Mon = new Monitor();
        public string State = "unknown";   // off | on | starting | stopping
        long visAt;
        DateTime deadline;
        bool hiddenStart, loading = true;
        DispatcherTimer timer, toastTimer, showTimer;
        BitmapDecoder icoDec;
        int folderCount;

        public MainUi(string dir)
        {
            Current = this;
            GuiDir = dir.TrimEnd('\\');
            InHome = string.Equals(System.IO.Path.GetFileName(GuiDir), "app", StringComparison.OrdinalIgnoreCase);
            HomeDir = InHome ? System.IO.Path.GetDirectoryName(GuiDir) : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Zapret");
            S = new Settings(System.IO.Path.Combine(GuiDir, "gui-settings.json"));
            Theme.Init(S.Str("theme", "dark"), S.Str("accent", "#3DDC97"));
        }

        // ---- доступ к элементам разметки по имени ----
        public T E<T>(string name) where T : class { return Win.FindName(name) as T; }
        public FrameworkElement El(string name) { return Win.FindName(name) as FrameworkElement; }
        public IAnimatable A(string name) { return Win.FindName(name) as IAnimatable; }
        public TextBlock Tb(string name) { return Win.FindName(name) as TextBlock; }
        public Button Btn(string name) { return Win.FindName(name) as Button; }

        static string Res(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            using (var r = new StreamReader(s)) return r.ReadToEnd();
        }

        public void Run(Application app)
        {
            // папка zapret
            var all = ZapretCore.FindFolders(GuiDir);
            if (all.Count == 0)
            {
                MessageBox.Show("Не нашёл папку zapret (на рабочем столе, в Загрузках или на диске C:).\nПоложи папку zapret-discord-youtube рядом и запусти снова.", "Zapret", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var own = InHome ? all.Where(f => string.Equals(System.IO.Path.GetDirectoryName(f.Path), HomeDir, StringComparison.OrdinalIgnoreCase)).ToList() : new List<ZapretFolder>();
            ZapretCore.UseFolder((own.Count > 0 ? own : all).Last().Path);
            folderCount = all.Count;

            Log.Write("start " + AppVersion);
            Win = (Window)XamlReader.Parse(Theme.Apply(Res("Main.xaml")));
            LoadIcon();
            Step("fill", Fill);
            Step("wire", Wire);
            Step("sites", WireSites);
            Step("telegram", WireTelegram);
            Step("settings", WireSettings);
            Step("test", WireTest);
            Step("migrate", WireMigrate);
            Step("updates", WireUpdates);
            Step("tray", BuildTray);
            Step("translate", () => Translate.Init(this));

            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            timer.Tick += (s, e) => { try { RefreshState(); } catch (Exception ex) { Log.Write("refresh: " + ex); } };
            showTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            showTimer.Tick += (s, e) => { try { if (Program.ShowEvent.WaitOne(0)) ShowFromTray(); } catch { } };
            showTimer.Start();

            Win.Loaded += (s, e) =>
            {
                Log.Write("loaded");
                Anim.D(A("Shell"), Anim.OP, 1, 380, 0);
                Anim.D(A("ShellShift"), Anim.TY, 0, 450, 18);
                // первый замер — сразу, дальше всё делает фоновый монитор
                try { Mon.SampleNow(); } catch { }
                RefreshState();
                Mon.Start();
                timer.Start();
                if (Program.AutoStart && State == "off") StartZapret();
                if (S.Bool("autoStartOnce")) { S.Set("autoStartOnce", false); S.Save(); if (State == "off") StartZapret(); }
                if (!Program.Tray) After(450, () => Safe(ShowUpdatedIfNeeded));
                if (folderCount > 1 && !File.Exists(UpdFlag)) Toast("Использую " + System.IO.Path.GetFileName(ZapretCore.Root) + ". Найдено папок zapret: " + folderCount + ".");
                if (Program.Tray)
                {
                    var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                    t.Tick += (s2, e2) => { t.Stop(); PauseGlow(); Win.Hide(); Win.ShowInTaskbar = true; Win.WindowState = WindowState.Normal; };
                    t.Start();
                }
            };
            Win.Activated += (s, e) => SetIdle(false);
            Win.Deactivated += (s, e) => SetIdle(true);
            Win.StateChanged += (s, e) => SetIdle(Win.WindowState == WindowState.Minimized || !Win.IsActive);
            Win.Closed += (s, e) => app.Shutdown();

            if (Program.Tray) { Win.ShowInTaskbar = false; Win.WindowState = WindowState.Minimized; }
            Win.Show();
            app.Run();
            Mon.Stop();
            try { trayIcon.Visible = false; trayIcon.Dispose(); } catch { }
        }

        // один шаг запуска: ошибка в одной части не должна ломать всё окно
        void Step(string name, Action a)
        {
            try { a(); } catch (Exception ex) { Log.Write("init " + name + ": " + ex); }
        }
        void LoadIcon()
        {
            var ico = System.IO.Path.Combine(GuiDir, "zapret.ico");
            try
            {
                // значок читаем в память, чтобы файл не оставался занятым
                var ms = File.Exists(ico) ? new MemoryStream(File.ReadAllBytes(ico)) : null;
                if (ms != null)
                {
                    icoDec = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    Win.Icon = icoDec.Frames.OrderByDescending(f => f.PixelWidth).First();
                    E<Image>("TitleLogo").Source = Win.Icon;
                }
            }
            catch (Exception ex) { Log.Write("icon: " + ex.Message); }
        }

        // ---- заполнение начальных значений ----
        void Fill()
        {
            Tb("TitleVer").Text = "v" + ZapretCore.LocalVersion + (IsTestBuild ? "  ·  тест" : "");
            var box = E<ComboBox>("StrategyBox");
            foreach (var b in ZapretCore.Strategies())
                box.Items.Add(new ComboBoxItem { Content = System.IO.Path.GetFileNameWithoutExtension(b), Tag = b });
            SelectByTag(box, S.Str("strategy", "general") + ".bat");
            if (box.SelectedItem == null && box.Items.Count > 0) box.SelectedIndex = 0;
            string ca = S.Str("closeAction", "ask");
            SelectByTag(E<ComboBox>("CloseBox"), ca);
            loading = false;
        }
        public static void SelectByTag(ComboBox c, string tag)
        {
            if (c == null) return;
            foreach (ComboBoxItem it in c.Items) if (string.Equals(it.Tag as string, tag, StringComparison.OrdinalIgnoreCase)) { c.SelectedItem = it; return; }
        }

        // ---- события ----
        void Wire()
        {
            El("TitleBar").MouseLeftButtonDown += (s, e) => { try { Win.DragMove(); } catch { } };
            Btn("MinBtn").Click += (s, e) => Win.WindowState = WindowState.Minimized;
            Btn("CloseBtn").Click += (s, e) => Safe(() =>
            {
                switch (S.Str("closeAction", "ask")) { case "tray": HideToTray(); break; case "exit": ExitApp(); break; default: OpenOverlay("Close"); break; }
            });
            Btn("CloseAskX").Click += (s, e) => CloseOverlay("Close");
            El("CloseDim").MouseLeftButtonUp += (s, e) => CloseOverlay("Close");
            Btn("CloseToTray").Click += (s, e) => Safe(() => { SetCloseAction("tray"); El("CloseOverlay").Visibility = Visibility.Collapsed; HideToTray(); });
            Btn("CloseToExit").Click += (s, e) => Safe(() => { SetCloseAction("exit"); ExitApp(); });

            var pb = Btn("PowerBtn");
            pb.Click += (s, e) => Safe(() => { if (State == "on") { StopZapret(); HideApply(); } else if (State == "off") StartZapret(); });
            pb.MouseEnter += (s, e) => { Scale("PowerScale", 1.04, 200); if (zShown && State == "on") Anim.D(A("ZTilt"), Anim.Ang, -10, 380, null, ease: Anim.Back()); };
            pb.MouseLeave += (s, e) => { Scale("PowerScale", 1, 200); Anim.D(A("ZTilt"), Anim.Ang, 0, 380, null, ease: Anim.Back()); };
            pb.PreviewMouseLeftButtonDown += (s, e) => Scale("PowerScale", 0.95, 90);
            pb.PreviewMouseLeftButtonUp += (s, e) => Scale("PowerScale", 1.04, 160);

            E<ComboBox>("StrategyBox").SelectionChanged += (s, e) => Safe(() =>
            {
                if (loading) return;
                var it = E<ComboBox>("StrategyBox").SelectedItem as ComboBoxItem;
                if (it == null) return;
                S.Set("strategy", it.Content as string); S.Save();
                if (State == "on" && ZapretCore.ServiceState() != "Running") { HideApply(); RestartZapret(); }
            });
            var cb = E<ComboBox>("CloseBox");
            if (cb != null) cb.SelectionChanged += (s, e) => { if (loading || cb.SelectedItem == null) return; S.Set("closeAction", (string)((ComboBoxItem)cb.SelectedItem).Tag); S.Save(); };

        }

        void Scale(string name, double v, int ms) { Anim.D(A(name), Anim.SX, v, ms); Anim.D(A(name), Anim.SY, v, ms); }

        public void Safe(Action a)
        {
            try { a(); } catch (Exception ex) { Log.Write(ex.ToString()); Toast("Ошибка: " + ex.Message, true); }
        }

        // ---- всплывающее сообщение внизу ----
        public void Toast(string text, bool err = false)
        {
            Tb("ToastText").Text = Translate.T(text);
            Tb("ToastText").Foreground = Theme.B(err ? "#FF5E62" : "#E6E8EE");
            Anim.D(A("Toast"), Anim.OP, 1, 250);
            Anim.D(A("ToastShift"), Anim.TY, 0, 300);
            if (toastTimer == null)
            {
                toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
                toastTimer.Tick += (s, e) => { toastTimer.Stop(); Anim.D(A("Toast"), Anim.OP, 0, 300); Anim.D(A("ToastShift"), Anim.TY, 30, 300); };
            }
            toastTimer.Stop(); toastTimer.Start();
        }

        // ---- оверлеи (окно поверх главного: тусклый фон + панель) ----
        public void OpenOverlay(string p)
        {
            El(p + "Overlay").Visibility = Visibility.Visible;
            Anim.D(A(p + "Dim"), Anim.OP, 1, 220, 0);
            Anim.D(A(p + "Panel"), Anim.OP, 1, 250, 0);
            Anim.D(A(p + "Scale"), Anim.SX, 1, 380, 0.94, ease: Anim.Back());
            Anim.D(A(p + "Scale"), Anim.SY, 1, 380, 0.94, ease: Anim.Back());
        }
        public void CloseOverlay(string p)
        {
            Anim.D(A(p + "Dim"), Anim.OP, 0, 180);
            Anim.D(A(p + "Panel"), Anim.OP, 0, 180);
            After(190, () => El(p + "Overlay").Visibility = Visibility.Collapsed);
        }
        public static void After(int ms, Action a)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (s, e) => { t.Stop(); a(); };
            t.Start();
        }

        void SetCloseAction(string a)
        {
            S.Set("closeAction", a); S.Save();
            loading = true; SelectByTag(E<ComboBox>("CloseBox"), a); loading = false;
        }
        void ExitApp()
        {
            Anim.D(A("Shell"), Anim.OP, 0, 180);
            After(190, () => Win.Close());
        }

        // окно свёрнуто или в фоне — почти ничего не делаем
        void SetIdle(bool idle)
        {
            Mon.Idle = idle;
            if (Win.WindowState == WindowState.Minimized) { timer.Stop(); if (tgTimer != null && !tgDownloading) tgTimer.Stop(); PauseGlow(); }
            else
            {
                if (!timer.IsEnabled && Mon.At > 0) timer.Start();
                if (tgTimer != null && !tgTimer.IsEnabled) tgTimer.Start();
                if (!idle) { Mon.Wake(); if (Win.IsVisible) ResumeGlow(); }
            }
        }

        // ---- состояние zapret ----
        void RefreshState()
        {
            if (testing) return;
            if (State == "starting" || State == "stopping")
            {
                Mon.Fast = true;
                string name; bool r = ZapretCore.RunningInfo(out name);
                if (State == "starting" && r)
                {
                    string n = name ?? S.Str("strategy", "general");
                    SetVisual("on", "Стратегия: " + n);
                    Toast("zapret включён · " + n);
                }
                else if (State == "stopping" && !r) SetVisual("off", null);
                else if (DateTime.Now > deadline)
                {
                    if (State == "starting")
                    {
                        SetVisual("off", "Не удалось запустить");
                        Toast("winws.exe не запустился. Попробуй другую стратегию" + (hiddenStart ? " или включи «Показывать окно winws», чтобы увидеть ошибку." : "."), true);
                    }
                    else { State = "x"; RefreshState(); }
                }
                return;
            }
            Mon.Fast = false;
            if (System.Threading.Interlocked.Read(ref Mon.At) <= visAt) return;   // ждём свежие данные от монитора
            if (Mon.Svc == "Running") SetVisual("on", "Работает как служба" + (Mon.SvcName != null ? " · " + Mon.SvcName : ""));
            else if (Mon.Winws) SetVisual("on", "Стратегия: " + (Mon.WinwsName ?? S.Str("strategy", "general")));
            else SetVisual("off", null);
        }

        public void StartZapret()
        {
            var it = E<ComboBox>("StrategyBox").SelectedItem as ComboBoxItem;
            if (it == null) { Toast("Выбери стратегию", true); return; }
            SetVisual("starting", it.Content as string);
            deadline = DateTime.Now.AddSeconds(12);
            ZapretCore.Start((string)it.Tag, S.Bool("showWindow"), out hiddenStart);
        }
        public void StopZapret(bool quiet = false)
        {
            SetVisual("stopping", "");
            deadline = DateTime.Now.AddSeconds(8);
            bool svc; ZapretCore.Stop(out svc);
            if (svc && !quiet) Toast("Служба остановлена. Она снова запустится после перезагрузки ПК.");
        }
        void RestartZapret()
        {
            StopZapret(true);
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            t.Tick += (s, e) => { string n; if (!ZapretCore.RunningInfo(out n)) { t.Stop(); State = "off"; StartZapret(); } };
            t.Start();
        }
        void HideApply()
        {
            pendingApply = false;
            var b = El("ApplyBar"); if (b == null) return;
            Anim.D(b, Anim.OP, 0, 200); b.Visibility = Visibility.Collapsed;
        }

        public void SetVisual(string mode, string detail)
        {
            if (State == mode && mode != "on") return;
            string prev = State;
            State = mode;
            if (prev != mode)
            {
                visAt = DateTime.UtcNow.Ticks; Mon.Wake();
                UpdateTrayIcon();
                if (trayMenu != null && trayMenu.IsOpen) UpdateTrayMenu(false);
            }
            var status = Tb("StatusText"); var sub = Tb("SubText");
            var powerFill = (Brush)Win.FindName("PowerFill");
            switch (mode)
            {
                case "on":
                    status.Text = Translate.T("Работает");
                    sub.Text = Translate.T(detail);
                    if (prev != "on")
                    {
                        SetZColor("#0F1115");
                        ShowZ(false);
                        Anim.Color(powerFill, "#3DDC97");
                        Anim.Color((Brush)Win.FindName("IconBrush"), "#0F1115"); Anim.Color((Brush)Win.FindName("IconBrush2"), "#0F1115");
                        E<Shape>("TitleDot").Fill = (Brush)Win.FindResource("Accent");
                        Anim.D(A("PowerShadow"), DropShadowEffect.BlurRadiusProperty, 40, 500);
                        Anim.Stop(A("Spinner"), Anim.OP); El("Spinner").Opacity = 0;
                        glowPaused = true; ResumeGlow();
                    }
                    break;
                case "off":
                    status.Text = Translate.T("Выключен");
                    HideZ();
                    sub.Text = Translate.T(string.IsNullOrEmpty(detail) ? "Нажми, чтобы включить" : detail);
                    Anim.Color(powerFill, "#262A33");
                    Anim.Color((Brush)Win.FindName("IconBrush"), "#8A90A0"); Anim.Color((Brush)Win.FindName("IconBrush2"), "#8A90A0");
                    E<Shape>("TitleDot").Fill = Theme.B("#5A6070");
                    Anim.D(A("PowerShadow"), DropShadowEffect.BlurRadiusProperty, 0, 400);
                    foreach (var g in new[] { "Glow1", "Glow2" }) { Anim.Stop(A(g), Anim.OP); El(g).Opacity = 0; }
                    Anim.Stop(A("Spinner"), Anim.OP); El("Spinner").Opacity = 0; Anim.Stop(A("SpinRot"), Anim.Ang);
                    break;
                default:   // starting / stopping
                    status.Text = Translate.T(mode == "starting" ? "Запуск…" : "Остановка…");
                    if (mode == "starting") SetZColor("#3DDC97");
                    ShowZ(true);
                    sub.Text = Translate.T(detail ?? "");
                    Anim.D(A("Spinner"), Anim.OP, 1, 200);
                    Anim.D(A("SpinRot"), Anim.Ang, 360, 900, 0, forever: true, noEase: true);
                    break;
            }
        }

        // ---- пульсирующие кольца вокруг кнопки ----
        bool glowPaused;
        public void PauseGlow()
        {
            foreach (var g in new[] { "Glow1", "Glow2" }) { Anim.Stop(A(g), Anim.OP); El(g).Opacity = 0; }
            foreach (var g in new[] { "Glow1S", "Glow2S" }) { Anim.Stop(A(g), Anim.SX); Anim.Stop(A(g), Anim.SY); }
            StopZShine();
            glowPaused = true;
        }
        public void ResumeGlow()
        {
            if (!glowPaused) return;
            glowPaused = false;
            if (State != "on") return;
            StartZShine();
            var gl = new[] { new { E = "Glow1", S = "Glow1S", B = 0 }, new { E = "Glow2", S = "Glow2S", B = 1100 } };
            foreach (var g in gl)
            {
                Anim.D(A(g.E), Anim.OP, 0, 2200, 0.7, forever: true, begin: g.B, noEase: true, low: true);
                Anim.D(A(g.S), Anim.SX, 1.38, 2200, 1, forever: true, begin: g.B, low: true);
                Anim.D(A(g.S), Anim.SY, 1.38, 2200, 1, forever: true, begin: g.B, low: true);
            }
        }
    }
}
