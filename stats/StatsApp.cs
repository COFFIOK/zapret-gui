// Zapret Stats — личное приложение автора: анонимная статистика Zapret GUI · by COFFIOK
// Берёт данные с сервера (GET /stats.json?k=КЛЮЧ). Ключ хранится в %AppData%\ZapretStats, зашифрован Windows (DPAPI).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

[assembly: AssemblyTitle("Zapret Stats")]
[assembly: AssemblyProduct("Zapret Stats")]
[assembly: AssemblyCompany("COFFIOK")]
[assembly: AssemblyVersion("1.2.0.0")]

namespace ZapretStats
{
    static class C
    {
        public static readonly Brush Bg = B("#121419"), Card = B("#1A1D24"), Line = B("#2E3340"), Text = B("#E6E8EE"),
            Muted = B("#8A90A0"), Accent = B("#3DDC97"), AccentDim = B("#1F5C45"), Red = B("#FF5E62"), Shadow = B("#08090B"), Hover = B("#232733");
        public static FontFamily Pixel = new FontFamily("Consolas");
        public static readonly FontFamily Body = new FontFamily("Segoe UI"), Mono = new FontFamily("Consolas");
        static Brush B(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }

        public static string Dir
        {
            get
            {
                string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZapretStats");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static void LoadFont()
        {
            try
            {
                byte[] data;
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ZPixel.ttf"))
                using (var ms = new MemoryStream()) { s.CopyTo(ms); data = ms.ToArray(); }
                string dir = Path.Combine(Dir, "font");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "ZPixel.ttf");
                if (!File.Exists(file) || new FileInfo(file).Length != data.Length) File.WriteAllBytes(file, data);
                Pixel = new FontFamily(new Uri(dir.TrimEnd('\\') + "\\"), "./#ZPixel");
            }
            catch { }
        }
    }

    // ---------- ключ ----------
    static class Key
    {
        static string FilePath { get { return Path.Combine(C.Dir, "key.bin"); } }
        static readonly byte[] Salt = Encoding.UTF8.GetBytes("ZapretStats-COFFIOK");
        public static string Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Salt, DataProtectionScope.CurrentUser));
            }
            catch { return null; }
        }
        public static void Save(string k) { File.WriteAllBytes(FilePath, ProtectedData.Protect(Encoding.UTF8.GetBytes(k), Salt, DataProtectionScope.CurrentUser)); }
        public static void Clear() { try { File.Delete(FilePath); } catch { } }
    }

    // ---------- сеть ----------
    static class Api
    {
        public const string Server = "https://zapret-share.chodaniel07.workers.dev";
        // null — ключ неверный; исключение — нет связи
        public static Dictionary<string, object> Fetch(string key) { return Call(key, "/stats.json", null, null); }
        public static Dictionary<string, object> Threads(string key, bool all) { return Call(key, "/chat/threads.json", all ? "&s=all" : "", null); }
        public static Dictionary<string, object> Thread(string key, string id) { return Call(key, "/chat/thread.json", "&id=" + id, null); }
        public static void Ban(string key, string id, double hours) { Call(key, "/chat/ban", "", Json(new Dictionary<string, object> { { "id", id }, { "hours", hours } })); }
        public static void Reply(string key, string id, string text, string img) { Call(key, "/chat/reply", "", Json(new Dictionary<string, object> { { "id", id }, { "text", text }, { "img", img ?? "" } })); }
        // картинка: загрузить (вернёт ключ) / скачать
        public static string UploadImg(string key, string id, byte[] jpg)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(Server + "/chat/img?id=" + id + "&k=" + Uri.EscapeDataString(key));
            req.Method = "POST"; req.ContentType = "image/jpeg"; req.UserAgent = "ZapretStats/1.2"; req.Timeout = 60000; req.ReadWriteTimeout = 60000;
            req.ContentLength = jpg.Length;
            using (var st = req.GetRequestStream()) st.Write(jpg, 0, jpg.Length);
            using (var r = req.GetResponse())
            using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
            {
                var d = new JavaScriptSerializer().DeserializeObject(sr.ReadToEnd()) as Dictionary<string, object>;
                return d != null && d.ContainsKey("img") ? Convert.ToString(d["img"]) : null;
            }
        }
        public static byte[] GetImg(string key, string img)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(Server + "/chat/img/" + img + "?k=" + Uri.EscapeDataString(key));
            req.UserAgent = "ZapretStats/1.2"; req.Timeout = 30000; req.ReadWriteTimeout = 30000;
            using (var r = req.GetResponse())
            using (var ms = new MemoryStream()) { r.GetResponseStream().CopyTo(ms); return ms.ToArray(); }
        }
        public static void SetThread(string key, string id, string status) { Call(key, "/chat/set", "", Json(new Dictionary<string, object> { { "id", id }, { "status", status } })); }
        static string Json(object o) { return new JavaScriptSerializer().Serialize(o); }
        static Dictionary<string, object> Call(string key, string path, string query, string postJson)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(Server + path + "?k=" + Uri.EscapeDataString(key) + (query ?? ""));
            req.UserAgent = "ZapretStats/1.2"; req.Timeout = 20000; req.ReadWriteTimeout = 20000;
            if (postJson != null)
            {
                req.Method = "POST"; req.ContentType = "application/json; charset=utf-8";
                var data = Encoding.UTF8.GetBytes(postJson); req.ContentLength = data.Length;
                using (var st = req.GetRequestStream()) st.Write(data, 0, data.Length);
            }
            try
            {
                using (var r = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                {
                    var js = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
                    return js.DeserializeObject(sr.ReadToEnd()) as Dictionary<string, object>;
                }
            }
            catch (WebException ex)
            {
                var hr = ex.Response as HttpWebResponse;
                if (hr != null && hr.StatusCode == HttpStatusCode.NotFound) return null;
                throw;
            }
        }
    }

    // ---------- разбор ответа ----------
    class Row { public string K; public long N; }
    static class J
    {
        public static long N(object o) { try { return o == null ? 0 : Convert.ToInt64(o, CultureInfo.InvariantCulture); } catch { return 0; } }
        public static double D(object o) { try { return o == null ? 0 : Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch { return 0; } }
        public static string S(object o) { return o == null ? "" : Convert.ToString(o, CultureInfo.InvariantCulture); }
        public static object Get(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v : null; }
        public static IEnumerable<Dictionary<string, object>> List(object o)
        {
            var e = o as IEnumerable;
            if (e == null || o is string) yield break;
            foreach (var x in e) { var d = x as Dictionary<string, object>; if (d != null) yield return d; }
        }
        public static List<Row> Rows(Dictionary<string, object> d, string k)
        {
            return List(Get(d, k)).Select(x => new Row { K = S(Get(x, "k")), N = N(Get(x, "n")) }).ToList();
        }
    }

    class App : Application
    {
        [STAThread]
        static void Main(string[] args)
        {
            var app = new App { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (s, e) => { MessageBox.Show(e.Exception.Message, "Zapret Stats"); e.Handled = true; };
            C.LoadFont();
            var w = new MainWin();
            // для разработки: ZapretStats.exe --shot sample.json out.png — нарисовать пример и сохранить картинку
            if (args.Length >= 3 && args[0] == "--shot") { w.SampleFile = args[1]; w.ShotFile = args[2]; }
            app.Run(w);
        }
    }

    partial class MainWin : Window
    {
        string key;
        readonly StackPanel content = new StackPanel { Margin = new Thickness(24, 8, 24, 28) };
        readonly TextBlock status = new TextBlock();
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        bool loading;
        Dictionary<string, object> last;
        public string SampleFile, ShotFile;
        string tab = "stats";          // stats | fb
        long lastFresh = -1;
        readonly TextBlock tabStats = new TextBlock(), tabFb = new TextBlock();
        readonly Border tabStatsLine = new Border(), tabFbLine = new Border();

        public MainWin()
        {
            Title = "Zapret Stats";
            Width = 1180; Height = 820; MinWidth = 860; MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = C.Bg; Foreground = C.Text; FontFamily = C.Body; FontSize = 13.5;
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            try { Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location).Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions()); } catch { }

            var root = new DockPanel();
            root.Children.Add(TopBar());
            DockPanel.SetDock((UIElement)root.Children[0], Dock.Top);
            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content, Focusable = false };
            var body = new Grid();
            body.Children.Add(sv);
            BuildChatView();
            chatView.Visibility = Visibility.Collapsed;
            body.Children.Add(chatView);
            root.Children.Add(body);
            Content = root;

            timer.Tick += (s, e) => Refresh(false);
            Loaded += (s, e) =>
            {
                if (SampleFile != null) { ShowSample(); return; }
                key = Key.Load();
                if (string.IsNullOrEmpty(key) && !AskKey()) { Close(); return; }
                Refresh(true);
                timer.Start();
            };
            KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.F5) Refresh(true); };
        }

        // ---------- верхняя панель ----------
        UIElement TopBar()
        {
            var bar = new Border { Background = C.Card, BorderBrush = C.Line, BorderThickness = new Thickness(0, 0, 0, 2), Padding = new Thickness(24, 14, 24, 14) };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var logo = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            logo.Children.Add(PixelZ());
            var tt = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            tt.Children.Add(new TextBlock { Text = "ZAPRET STATS", FontFamily = C.Pixel, FontSize = 22, Foreground = C.Text });
            status.FontSize = 12; status.Foreground = C.Muted; status.Text = "загрузка…";
            tt.Children.Add(status);
            logo.Children.Add(tt);
            g.Children.Add(logo);

            var tabs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(36, 0, 0, 0) };
            tabs.Children.Add(Tab(tabStats, tabStatsLine, "СТАТИСТИКА", "stats"));
            tabs.Children.Add(Tab(tabFb, tabFbLine, "ЧАТЫ", "fb"));
            Grid.SetColumn(tabs, 1);
            g.Children.Add(tabs);
            UpdateTabs();

            var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            btns.Children.Add(PxButton("ОБНОВИТЬ", true, () => Refresh(true)));
            btns.Children.Add(PxButton("В БРАУЗЕРЕ", false, () =>
            {
                try { System.Diagnostics.Process.Start(Api.Server + "/stats?k=" + Uri.EscapeDataString(key ?? "")); } catch { }
            }));
            btns.Children.Add(PxButton("КЛЮЧ", false, () => { if (AskKey()) Refresh(true); }));
            Grid.SetColumn(btns, 2);
            g.Children.Add(btns);
            bar.Child = g;
            return bar;
        }

        UIElement Tab(TextBlock label, Border line, string text, string id)
        {
            label.Text = text; label.FontFamily = C.Pixel; label.FontSize = 15;
            line.Height = 4; line.Margin = new Thickness(0, 6, 0, 0);
            var sp = new StackPanel { Margin = new Thickness(0, 0, 26, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            sp.Children.Add(label); sp.Children.Add(line);
            sp.MouseLeftButtonUp += (s, e) => { tab = id; UpdateTabs(); ShowTab(); };
            return sp;
        }

        void UpdateTabs()
        {
            long fresh = lastThreads != null ? J.N(J.Get(lastThreads, "unread")) : (last != null ? J.N(J.Get(last, "chatUnread")) : 0);
            tabFb.Text = fresh > 0 ? "ЧАТЫ · " + fresh : "ЧАТЫ";
            tabStats.Foreground = tab == "stats" ? C.Text : C.Muted;
            tabFb.Foreground = tab == "fb" ? C.Text : (fresh > 0 ? C.Accent : C.Muted);
            tabStatsLine.Background = tab == "stats" ? C.Accent : Brushes.Transparent;
            tabFbLine.Background = tab == "fb" ? C.Accent : Brushes.Transparent;
            Title = fresh > 0 ? "(" + fresh + ") Zapret Stats" : "Zapret Stats";
        }

        void ShowTab()
        {
            var sv = content.Parent as ScrollViewer;
            sv.Visibility = tab == "fb" ? Visibility.Collapsed : Visibility.Visible;
            chatView.Visibility = tab == "fb" ? Visibility.Visible : Visibility.Collapsed;
            if (tab == "fb") { RenderThreads(); if (selId != null) LoadThread(selId, false); }
            else { if (last != null) Render(last); sv.ScrollToTop(); }
        }

        static UIElement PixelZ()
        {
            // буква Z из клеток 5x5
            string[] m = { "#####", "...#.", "..#..", ".#...", "#####" };
            var c = new Canvas { Width = 30, Height = 30 };
            for (int y = 0; y < 5; y++)
                for (int x = 0; x < 5; x++)
                    if (m[y][x] == '#')
                    {
                        var r = new Border { Width = 6, Height = 6, Background = C.Accent };
                        Canvas.SetLeft(r, x * 6); Canvas.SetTop(r, y * 6); c.Children.Add(r);
                    }
            return c;
        }

        static Button PxButton(string text, bool primary, Action click)
        {
            var b = new Button { Content = text, Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand, Focusable = false };
            var fg = primary ? C.Bg : C.Text;
            var bg = primary ? C.Accent : C.Card;
            var t = new ControlTemplate(typeof(Button));
            var outer = new FrameworkElementFactory(typeof(Grid));
            var shadow = new FrameworkElementFactory(typeof(Border));
            shadow.SetValue(Border.BackgroundProperty, C.Shadow);
            shadow.SetValue(FrameworkElement.MarginProperty, new Thickness(3, 3, -3, -3));
            var face = new FrameworkElementFactory(typeof(Border));
            face.Name = "face";
            face.SetValue(Border.BackgroundProperty, bg);
            face.SetValue(Border.BorderBrushProperty, primary ? C.Accent : C.Line);
            face.SetValue(Border.BorderThicknessProperty, new Thickness(2));
            face.SetValue(Border.PaddingProperty, new Thickness(14, 7, 14, 7));
            var cp = new FrameworkElementFactory(typeof(TextBlock));
            cp.SetValue(TextBlock.TextProperty, text);
            cp.SetValue(TextBlock.FontFamilyProperty, C.Pixel);
            cp.SetValue(TextBlock.FontSizeProperty, 13.0);
            cp.SetValue(TextBlock.ForegroundProperty, fg);
            face.AppendChild(cp);
            outer.AppendChild(shadow);
            outer.AppendChild(face);
            t.VisualTree = outer;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, C.Accent, "face"));
            if (!primary) hover.Setters.Add(new Setter(Border.BackgroundProperty, C.Hover, "face"));
            t.Triggers.Add(hover);
            var press = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            press.Setters.Add(new Setter(FrameworkElement.RenderTransformProperty, new TranslateTransform(2, 2), "face"));
            t.Triggers.Add(press);
            b.Template = t;
            b.Click += (s, e) => click();
            return b;
        }

        // ---------- ключ ----------
        bool AskKey()
        {
            var w = new Window
            {
                Title = "Zapret Stats — ключ", Owner = IsLoaded ? this : null, Width = 440, SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize, WindowStartupLocation = IsLoaded ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                Background = C.Bg, Foreground = C.Text, FontFamily = C.Body, ShowInTaskbar = !IsLoaded
            };
            var sp = new StackPanel { Margin = new Thickness(22) };
            sp.Children.Add(new TextBlock { Text = "КЛЮЧ СТАТИСТИКИ", FontFamily = C.Pixel, FontSize = 18 });
            sp.Children.Add(new TextBlock { Text = "Секрет STATS_KEY с сервера Cloudflare. Хранится только на этом компьютере в зашифрованном виде.", TextWrapping = TextWrapping.Wrap, Foreground = C.Muted, Margin = new Thickness(0, 6, 0, 14) });
            var pb = new PasswordBox { FontSize = 15, Padding = new Thickness(8, 6, 8, 6), Background = C.Card, Foreground = C.Text, BorderBrush = C.Line, BorderThickness = new Thickness(2), CaretBrush = C.Accent };
            sp.Children.Add(pb);
            var err = new TextBlock { Foreground = C.Red, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            sp.Children.Add(err);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            bool ok = false;
            Action save = () =>
            {
                string k = pb.Password.Trim();
                if (k.Length < 8) { err.Text = "Ключ слишком короткий"; err.Visibility = Visibility.Visible; return; }
                err.Text = "Проверяю…"; err.Foreground = C.Muted; err.Visibility = Visibility.Visible;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    string msg = null;
                    try { if (Api.Fetch(k) == null) msg = "Неверный ключ"; }
                    catch (Exception ex) { msg = "Нет связи с сервером: " + ex.Message; }
                    w.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (msg != null) { err.Text = msg; err.Foreground = C.Red; return; }
                        Key.Save(k); key = k; ok = true; w.Close();
                    }));
                });
            };
            row.Children.Add(PxButton("ОТМЕНА", false, () => w.Close()));
            row.Children.Add(PxButton("СОХРАНИТЬ", true, save));
            sp.Children.Add(row);
            pb.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) save(); };
            w.Content = sp;
            w.Loaded += (s, e) => pb.Focus();
            w.ShowDialog();
            return ok;
        }

        // ---------- загрузка ----------
        void Refresh(bool manual)
        {
            if (loading || string.IsNullOrEmpty(key)) return;
            loading = true;
            if (manual) status.Text = "обновляю…";
            string k = key;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Dictionary<string, object> d = null, fb = null; string error = null; bool badKey = false;
                try
                {
                    d = Api.Fetch(k); badKey = d == null;
                    if (!badKey) fb = Api.Threads(k, showClosed);
                }
                catch (Exception ex) { error = ex.Message; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    loading = false;
                    if (badKey)
                    {
                        status.Text = "ключ не подошёл";
                        Key.Clear();
                        if (AskKey()) Refresh(true);
                        return;
                    }
                    if (error != null)
                    {
                        status.Text = "нет связи: " + error + (last != null ? " · показаны прошлые данные" : "");
                        if (last == null) ShowMessage("Не удалось получить данные", error);
                        return;
                    }
                    last = d;
                    if (fb != null) lastThreads = fb;
                    long fresh = lastThreads != null ? J.N(J.Get(lastThreads, "unread")) : 0;
                    if (lastFresh >= 0 && fresh > lastFresh) Flash();   // пришло новое сообщение — мигаем на панели задач
                    lastFresh = fresh;
                    UpdateTabs();
                    try { if (tab == "fb") RenderThreads(); else Render(d); }
                    catch (Exception ex) { ShowMessage("Ошибка отображения", ex.Message); }
                    status.Text = "обновлено " + DateTime.Now.ToString("HH:mm:ss") + " · автообновление каждую минуту · F5";
                }));
            });
        }

        void ShowSample()
        {
            var d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(SampleFile, Encoding.UTF8)) as Dictionary<string, object>;
            if (d.ContainsKey("threads"))
            {
                tab = "fb"; lastThreads = d; UpdateTabs(); ShowTab();
                var th = J.Get(d, "thread") as Dictionary<string, object>;
                if (th != null) { selId = J.S(J.Get(J.Get(th, "thread") as Dictionary<string, object>, "id")); RenderThreads(); RenderThread(th); }
            }
            else Render(d);
            status.Text = "обновлено " + DateTime.Now.ToString("HH:mm:ss") + " · автообновление каждую минуту · F5";
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // снимок: верхняя панель + всё содержимое целиком (не только видимая часть)
                if (tab == "fb") { ShotWindow(); return; }
                var top = (FrameworkElement)((DockPanel)Content).Children[0];
                double w = top.ActualWidth, h1 = top.ActualHeight, h2 = content.ActualHeight + content.Margin.Top + content.Margin.Bottom;
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(C.Bg, null, new Rect(0, 0, w, h1 + h2));
                    dc.DrawRectangle(new VisualBrush(top), null, new Rect(0, 0, w, h1));
                    dc.DrawRectangle(new VisualBrush(content), null, new Rect(content.Margin.Left, h1 + content.Margin.Top, content.ActualWidth, content.ActualHeight));
                }
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)w, (int)(h1 + h2), 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using (var f = File.Create(ShotFile)) enc.Save(f);
                Close();
            }), DispatcherPriority.ApplicationIdle);
        }

        void ShowMessage(string title, string text)
        {
            content.Children.Clear();
            content.Children.Add(H(title));
            content.Children.Add(new TextBlock { Text = text, Foreground = C.Muted, TextWrapping = TextWrapping.Wrap });
        }

        // ---------- отрисовка ----------
        void Render(Dictionary<string, object> d)
        {
            double offset = 0;
            var sv = content.Parent as ScrollViewer;
            if (sv != null) offset = sv.VerticalOffset;
            content.Children.Clear();

            // каждая установка присылает данные один раз (если человек согласился) — показываем срез по установкам
            var daily = J.Rows(d, "daily");
            long tot = J.N(J.Get(d, "tot"));

            content.Children.Add(H("Прислали данные"));
            var cards = new UniformGrid { Columns = 4 };
            cards.Children.Add(StatCard("всего", tot, null));
            cards.Children.Add(StatCard("сегодня", J.N(J.Get(d, "new1")), null));
            cards.Children.Add(StatCard("за 7 дней", J.N(J.Get(d, "new7")), null));
            cards.Children.Add(StatCard("за 30 дней", J.N(J.Get(d, "new30")), null));
            content.Children.Add(cards);
            content.Children.Add(new TextBlock { Text = "каждая установка присылает данные один раз — если человек согласился", Foreground = C.Muted, FontSize = 12, Margin = new Thickness(0, -4, 0, 0) });

            content.Children.Add(H("По дням · 30 дней"));
            content.Children.Add(DauChart(daily));

            content.Children.Add(H("Версии и система"));
            var g1 = Columns(2);
            g1.Children.Add(Box("Версия приложения", Bars(J.Rows(d, "vers"))));
            g1.Children.Add(Box("Версия zapret", Bars(J.Rows(d, "zv"))));
            g1.Children.Add(Box("Windows", Bars(J.Rows(d, "os"))));
            g1.Children.Add(Box("Язык и тема", Stack(Bars(J.Rows(d, "langs"), r => r.K == "ru" ? "русский" : r.K == "en" ? "английский" : r.K),
                Sub("тема"), Bars(J.Rows(d, "themes"), r => r.K == "dark" ? "тёмная" : r.K == "light" ? "светлая" : r.K))));
            content.Children.Add(g1);

            content.Children.Add(H("Как пользуются"));
            var g2 = Columns(2);
            g2.Children.Add(Box("Выбранная стратегия", Bars(J.Rows(d, "strat"))));
            g2.Children.Add(Box("Telegram-прокси и игровой фильтр", Stack(Bars(J.Rows(d, "tg")), Sub("игровой фильтр"), Bars(J.Rows(d, "game")))));
            g2.Children.Add(Box("Экономия и автообновление", Stack(Bars(J.Rows(d, "eco")), Sub("автопроверка обновлений zapret"), Bars(J.Rows(d, "autoupd")))));
            g2.Children.Add(Box("Где стоит zapret", Bars(J.Rows(d, "home"))));
            var sites = J.Get(d, "sites") as Dictionary<string, object>;
            var st = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = C.Muted };
            st.Inlines.Add("в среднем "); st.Inlines.Add(Num(J.D(J.Get(sites, "avg")).ToString("0.#", CultureInfo.InvariantCulture)));
            st.Inlines.Add(" · максимум "); st.Inlines.Add(Num(J.N(J.Get(sites, "max")).ToString()));
            st.Inlines.Add(" · свои сайты есть у "); st.Inlines.Add(Num(J.N(J.Get(sites, "withSites")).ToString()));
            st.Inlines.Add(" из " + tot);
            g2.Children.Add(Box("Свои сайты", st));
            content.Children.Add(g2);

            if (sv != null) sv.Dispatcher.BeginInvoke(new Action(() => sv.ScrollToVerticalOffset(offset)), DispatcherPriority.Loaded);
        }

        // ---------- сообщения от пользователей ----------
        static readonly Brush KindBug = C.Red, KindIdea = C.Accent, KindOther = MakeBrush("#7AA2FF"), NewBg = MakeBrush("#1B3329");
        static Brush MakeBrush(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }

        static UIElement Tag(string text, Brush color, bool filled)
        {
            return new Border
            {
                Background = filled ? color : Brushes.Transparent, BorderBrush = color, BorderThickness = new Thickness(2), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 8, 0),
                Child = new TextBlock { Text = text, FontFamily = C.Pixel, FontSize = 12, Foreground = filled ? C.Bg : color }
            };
        }

        static string Or(string v, string def) { return string.IsNullOrEmpty(v) ? def : v; }

        static string When(string iso)
        {
            DateTime t;
            if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) return iso;
            t = t.ToLocalTime();
            var ru = CultureInfo.GetCultureInfo("ru-RU");
            if (t.Date == DateTime.Today) return "сегодня, " + t.ToString("HH:mm");
            if (t.Date == DateTime.Today.AddDays(-1)) return "вчера, " + t.ToString("HH:mm");
            return t.ToString(t.Year == DateTime.Today.Year ? "d MMMM, HH:mm" : "d MMMM yyyy, HH:mm", ru);
        }

        static string WinName(string os)
        {
            var m = System.Text.RegularExpressions.Regex.Match(os ?? "", @"^(\d+)\.(\d+)\.(\d+)");
            if (!m.Success) return string.IsNullOrEmpty(os) ? "Windows ?" : "Windows " + os;
            if (m.Groups[1].Value == "10") return int.Parse(m.Groups[3].Value) >= 22000 ? "Windows 11" : "Windows 10";
            if (m.Groups[1].Value == "6" && m.Groups[2].Value == "1") return "Windows 7";
            if (m.Groups[1].Value == "6") return "Windows 8";
            return "Windows " + os;
        }

        // мигание кнопки на панели задач, если окно не в фокусе
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
        void Flash()
        {
            try
            {
                if (IsActive) return;
                System.Media.SystemSounds.Asterisk.Play();
                var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                var f = new FLASHWINFO { hwnd = h, dwFlags = 3 | 12, uCount = 0, dwTimeout = 0 };   // окно + кнопка, пока не откроют
                f.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(f);
                FlashWindowEx(ref f);
            }
            catch { }
        }

        static Run Num(string s) { return new Run(s) { Foreground = C.Text, FontWeight = FontWeights.SemiBold }; }

        static TextBlock H(string text)
        {
            return new TextBlock { Text = text.ToUpperInvariant(), FontFamily = C.Pixel, FontSize = 14, Foreground = C.Muted, Margin = new Thickness(0, 22, 0, 10) };
        }
        static TextBlock Sub(string text)
        {
            return new TextBlock { Text = text, Foreground = C.Muted, FontSize = 12.5, Margin = new Thickness(0, 12, 0, 6) };
        }
        static StackPanel Stack(params UIElement[] items) { var s = new StackPanel(); foreach (var i in items) s.Children.Add(i); return s; }
        static UniformGrid Columns(int n) { return new UniformGrid { Columns = n }; }

        // карточка с жёсткой пиксельной тенью
        static UIElement Panel(UIElement child, Thickness pad, Brush edge)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 12, 12) };
            g.Children.Add(new Border { Background = C.Shadow, Margin = new Thickness(4, 4, -4, -4) });
            g.Children.Add(new Border { Background = C.Card, BorderBrush = edge ?? C.Line, BorderThickness = new Thickness(2), Padding = pad, Child = child });
            return g;
        }

        static UIElement StatCard(string title, long value, string sub, bool warn = false)
        {
            var s = new StackPanel();
            s.Children.Add(new TextBlock { Text = value.ToString("N0", CultureInfo.GetCultureInfo("ru-RU")), FontFamily = C.Pixel, FontSize = 32, Foreground = warn ? C.Red : C.Accent });
            s.Children.Add(new TextBlock { Text = title, Foreground = C.Text, Margin = new Thickness(0, 2, 0, 0) });
            if (sub != null) s.Children.Add(new TextBlock { Text = sub, Foreground = C.Muted, FontSize = 12 });
            return Panel(s, new Thickness(14, 10, 14, 12), null);
        }

        static UIElement Box(string title, UIElement body)
        {
            var s = new StackPanel();
            s.Children.Add(new TextBlock { Text = title, Foreground = C.Muted, FontWeight = FontWeights.SemiBold, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 8) });
            s.Children.Add(body);
            return Panel(s, new Thickness(14, 12, 14, 12), null);
        }

        static UIElement Bars(List<Row> rows, Func<Row, string> label = null)
        {
            if (rows.Count == 0) return new TextBlock { Text = "пока нет данных", Foreground = C.Muted };
            long max = Math.Max(1, rows.Max(r => r.N)), sum = rows.Sum(r => r.N);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
                string l = label != null ? label(r) : r.K;
                if (string.IsNullOrEmpty(l)) l = "—";
                var lt = new TextBlock { Text = l, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = l };
                Grid.SetRow(lt, i); g.Children.Add(lt);

                // полоса: доля от максимума, ступенчатая (пиксельные клетки по 6px)
                var track = new Grid { Height = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 10, 0) };
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(r.N, GridUnitType.Star) });
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(max - r.N, GridUnitType.Star) });
                track.Children.Add(new Border { Background = C.AccentDim, Opacity = 0.35 });
                Grid.SetColumnSpan((UIElement)track.Children[0], 2);
                var fill = new Border { Background = C.Accent, MinWidth = r.N > 0 ? 6 : 0 };
                track.Children.Add(fill);
                Grid.SetRow(track, i); Grid.SetColumn(track, 1); g.Children.Add(track);

                var nt = new TextBlock { Text = r.N.ToString(), FontFamily = C.Mono, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetRow(nt, i); Grid.SetColumn(nt, 2); g.Children.Add(nt);
                var pt = new TextBlock { Text = sum > 0 ? Math.Round(r.N * 100.0 / sum) + "%" : "", Foreground = C.Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetRow(pt, i); Grid.SetColumn(pt, 3); g.Children.Add(pt);
            }
            return g;
        }

        static UIElement DauChart(List<Row> dau)
        {
            var byDay = new Dictionary<string, long>();
            foreach (var r in dau) byDay[r.K] = r.N;
            var days = Enumerable.Range(0, 30).Select(i => DateTime.UtcNow.Date.AddDays(i - 29)).ToList();
            long max = Math.Max(1, days.Select(x => { long n; return byDay.TryGetValue(x.ToString("yyyy-MM-dd"), out n) ? n : 0; }).Max());

            var outer = new StackPanel();
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            top.Children.Add(new TextBlock { Text = "максимум " + max, Foreground = C.Muted, FontSize = 12 });
            outer.Children.Add(top);

            var g = new Grid { Height = 140 };
            for (int i = 0; i < days.Count; i++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition());
                long n; byDay.TryGetValue(days[i].ToString("yyyy-MM-dd"), out n);
                double h = n == 0 ? 2 : Math.Max(6, Math.Round(n * 138.0 / max / 6) * 6);   // высота кратна 6px — «клетки»
                bool today = i == days.Count - 1;
                var col = new Border
                {
                    Height = h, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(2, 0, 2, 0),
                    Background = n == 0 ? C.Line : (today ? C.Text : C.Accent),
                    ToolTip = days[i].ToString("d MMMM", CultureInfo.GetCultureInfo("ru-RU")) + ": " + n
                };
                var hit = new Border { Background = Brushes.Transparent, Child = col, ToolTip = col.ToolTip };
                Grid.SetColumn(hit, i); g.Children.Add(hit);
            }
            outer.Children.Add(g);

            var labels = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            labels.ColumnDefinitions.Add(new ColumnDefinition()); labels.ColumnDefinitions.Add(new ColumnDefinition());
            labels.Children.Add(new TextBlock { Text = days[0].ToString("d MMM", CultureInfo.GetCultureInfo("ru-RU")), Foreground = C.Muted, FontSize = 12 });
            var lr = new TextBlock { Text = "сегодня", Foreground = C.Muted, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(lr, 1); labels.Children.Add(lr);
            outer.Children.Add(labels);
            return Panel(outer, new Thickness(14, 12, 14, 12), null);
        }

        static UIElement LinkBtn(string text, Action<TextBlock> click)
        {
            var t = new TextBlock { Text = text, Foreground = C.Accent, FontSize = 12, Margin = new Thickness(0, 0, 16, 0), Cursor = Cursors.Hand };
            t.MouseLeftButtonUp += (s, e) => click(t);
            return t;
        }
    }
}
