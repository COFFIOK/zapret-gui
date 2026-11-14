// Zapret (C#) — чат с разработчиком: пользователь пишет об ошибках и идеях, автор отвечает из приложения Zapret Stats.
// Переписка привязана к случайному номеру установки (без имени, почты и IP).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ZapretCS
{
    partial class MainUi
    {
        bool fbSending, fbLoading, fbOpen;
        long fbLastN;                       // номер последнего показанного сообщения
        DispatcherTimer fbFastTimer, fbSlowTimer;
        byte[] fbPendingImg;                // прикреплённая картинка (уже сжатая в JPEG)
        string fbBanUntil;                  // null — можно писать; 'forever' или ISO-время
        bool fbEditNick;
        static readonly Dictionary<string, byte[]> fbImgCache = new Dictionary<string, byte[]>();

        void WireFeedback()
        {
            Btn("FbBtn").Click += (s, e) => Safe(() => { CloseSettings(); After(200, OpenFeedback); });
            Btn("ChatBtn").Click += (s, e) => Safe(() => { if (El("SettingsOverlay").Visibility == Visibility.Visible) { CloseSettings(); After(200, OpenFeedback); } else OpenFeedback(); });
            Btn("FbClose").Click += (s, e) => CloseFeedback();
            El("FbDim").MouseLeftButtonUp += (s, e) => { if (!fbSending) CloseFeedback(); };
            var text = E<TextBox>("FbText");
            text.TextChanged += (s, e) => El("FbHint").Visibility = text.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            // Enter — отправить, Shift+Enter — новая строка
            text.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.Enter) return;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                {
                    int at = text.CaretIndex;
                    text.Text = text.Text.Insert(at, Environment.NewLine);
                    text.CaretIndex = at + Environment.NewLine.Length;
                }
                else Safe(SendFeedback);
                e.Handled = true;
            };
            // для идей и вопросов журнал обычно не нужен
            E<RadioButton>("FbKindBug").Checked += (s, e) => E<CheckBox>("FbLog").IsChecked = true;
            E<RadioButton>("FbKindIdea").Checked += (s, e) => E<CheckBox>("FbLog").IsChecked = false;
            E<RadioButton>("FbKindOther").Checked += (s, e) => E<CheckBox>("FbLog").IsChecked = false;
            Btn("FbSend").Click += (s, e) => Safe(SendFeedback);
            Btn("FbAttach").Click += (s, e) => Safe(PickChatImage);
            El("FbAttachDel").MouseLeftButtonUp += (s, e) => SetChatImage(null);
            // Ctrl+V с картинкой в буфере обмена — прикрепить скриншот
            text.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && Clipboard.ContainsImage())
                {
                    Safe(() => SetChatImage(PrepImage(Clipboard.GetImage())));
                    e.Handled = true;
                }
            };
            // ник
            var nick = E<TextBox>("FbNickBox");
            nick.TextChanged += (s, e) => El("FbNickHint").Visibility = nick.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            nick.KeyDown += (s, e) => { if (e.Key == Key.Enter) Safe(SaveNick); };
            Btn("FbNickSave").Click += (s, e) => Safe(SaveNick);
            El("FbNickEdit").MouseLeftButtonUp += (s, e) => { fbEditNick = true; nick.Text = S.Str("chatNick", ""); UpdateChatUi(); nick.Focus(); nick.SelectAll(); };
            Win.KeyDown += (s, e) => { if (e.Key == Key.Escape && El("FbOverlay").Visibility == Visibility.Visible && !fbSending) CloseFeedback(); };

            // пока чат открыт — проверяем ответы часто; в фоне — редко и только если человек уже писал
            fbFastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            fbFastTimer.Tick += (s, e) => LoadChat(true);
            fbSlowTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
            fbSlowTimer.Tick += (s, e) => CheckReplies();
            fbSlowTimer.Start();
            After(15000, CheckReplies);
        }

        void OpenFeedback()
        {
            fbOpen = true;
            FbStatus("", false);
            Btn("FbSend").IsEnabled = true;
            SetChatDot(false);
            fbEditNick = false;
            UpdateChatUi();
            OpenOverlay("Fb");
            LoadChat(true);
            fbFastTimer.Start();
            After(260, () => { if (string.IsNullOrEmpty(S.Str("chatNick", ""))) E<TextBox>("FbNickBox").Focus(); else E<TextBox>("FbText").Focus(); });
        }

        void CloseFeedback()
        {
            fbOpen = false;
            fbFastTimer.Stop();
            CloseOverlay("Fb");
        }

        // что показать внизу чата: ввод ника, блокировку или обычное поле
        void UpdateChatUi()
        {
            string nick = S.Str("chatNick", "");
            bool needNick = fbEditNick || nick.Length < 2;
            bool banned = fbBanUntil != null;
            El("FbNickPanel").Visibility = needNick && !banned ? Visibility.Visible : Visibility.Collapsed;
            El("FbBanPanel").Visibility = banned ? Visibility.Visible : Visibility.Collapsed;
            El("FbComposer").Visibility = !needNick && !banned ? Visibility.Visible : Visibility.Collapsed;
            El("FbNickLine").Visibility = nick.Length >= 2 && !needNick ? Visibility.Visible : Visibility.Collapsed;
            E<TextBlock>("FbNickName").Text = nick;
            if (banned)
            {
                string until = fbBanUntil == "forever" ? Translate.T("Автор закрыл тебе доступ к чату.") : Translate.T("Писать можно будет после") + " " + ChatTime(fbBanUntil, true) + ".";
                E<TextBlock>("FbBanText").Text = until;
            }
        }

        void SaveNick()
        {
            string n = Regex.Replace(E<TextBox>("FbNickBox").Text ?? "", @"\s+", " ").Trim();
            if (n.Length < 2) { FbStatus(Translate.T("Ник — хотя бы 2 символа"), true); return; }
            S.Set("chatNick", n); S.Save();
            fbEditNick = false;
            FbStatus("", false);
            UpdateChatUi();
            E<TextBox>("FbText").Focus();
        }

        void SetChatBan(object ban)
        {
            var d = ban as Dictionary<string, object>;
            object u = null;
            string until = d != null && d.TryGetValue("until", out u) ? Convert.ToString(u) : null;
            if (until == fbBanUntil) return;
            fbBanUntil = string.IsNullOrEmpty(until) ? null : until;
            UpdateChatUi();
        }

        void SetChatDot(bool on)
        {
            El("ChatDot").Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- сеть ----
        static Dictionary<string, object> ChatPost(string path, Dictionary<string, object> body)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(ShareServer + path);
            req.Method = "POST"; req.UserAgent = "ZapretGUI/" + AppVersion; req.Timeout = 15000; req.ReadWriteTimeout = 15000;
            req.ContentType = "application/json; charset=utf-8";
            var data = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(body)); req.ContentLength = data.Length;
            using (var st = req.GetRequestStream()) st.Write(data, 0, data.Length);
            using (var r = req.GetResponse())
            using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                return new JavaScriptSerializer().DeserializeObject(sr.ReadToEnd()) as Dictionary<string, object>;
        }

        // загрузить новые сообщения (seen — чат открыт, ответы считаются прочитанными)
        void LoadChat(bool seen)
        {
            if (fbLoading) return;
            if (!S.Bool("chatStarted")) { ShowChatEmpty(); return; }
            fbLoading = true;
            var body = new Dictionary<string, object> { { "id", InstallId() }, { "after", fbLastN }, { "seen", seen } };
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Dictionary<string, object> d = null;
                try { d = ChatPost("/chat/get", body); } catch (Exception logEx) { Log.Err("chat get", logEx); }
                Win.Dispatcher.BeginInvoke(new Action(() =>
                {
                    fbLoading = false;
                    if (d == null) return;
                    object m, ban; d.TryGetValue("msgs", out m);
                    d.TryGetValue("ban", out ban); SetChatBan(ban);
                    var list = m as IEnumerable;
                    bool anim = E<StackPanel>("FbList").Children.Count > 0;   // первая загрузка — без анимации, новые сообщения — «впрыгивают»
                    if (list != null) foreach (var x in list) { var msg = x as Dictionary<string, object>; if (msg != null) AddBubble(msg, anim); }
                    ShowChatEmpty();
                }));
            });
        }

        // фоновая проверка: ответил ли автор, пока чат закрыт
        void CheckReplies()
        {
            if (!S.Bool("chatStarted") || fbOpen) return;
            var body = new Dictionary<string, object> { { "id", InstallId() }, { "peek", true } };
            ThreadPool.QueueUserWorkItem(_ =>
            {
                long unread = 0;
                try { var d = ChatPost("/chat/get", body); object u; if (d != null && d.TryGetValue("unread", out u)) unread = Convert.ToInt64(u); } catch { return; }
                if (unread <= 0) return;
                Win.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (fbOpen) return;
                    SetChatDot(true);
                    Toast("Разработчик ответил в чате — значок сверху");
                }));
            });
        }

        void SendFeedback()
        {
            if (fbSending) return;
            var box = E<TextBox>("FbText");
            string text = box.Text.Trim();
            var img = fbPendingImg;
            if (text.Length == 0 && img == null) return;
            bool first = !S.Bool("chatStarted");
            string kind = E<RadioButton>("FbKindIdea").IsChecked == true ? "idea" : E<RadioButton>("FbKindOther").IsChecked == true ? "other" : "bug";
            var os = Environment.OSVersion.Version;
            var body = new Dictionary<string, object> {
                { "id", InstallId() }, { "v", AppVersion }, { "z", ZapretCore.LocalVersion ?? "" },
                { "os", os.Major + "." + os.Minor + "." + os.Build }, { "lang", Translate.En ? "en" : "ru" },
                { "kind", kind }, { "text", text }, { "nick", S.Str("chatNick", "") }, { "strat", S.Str("strategy", "") }, { "on", State == "on" }
            };
            if (E<CheckBox>("FbLog").IsChecked == true) body["log"] = LogTail(60, 6000);

            fbSending = true;
            Btn("FbSend").IsEnabled = false;
            FbStatus(Translate.T(img != null ? "Загружаю картинку…" : "Отправляю…"), false);
            string id = InstallId();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string err = null; object ban = null;
                try
                {
                    if (img != null) body["img"] = UploadChatImage(id, img);
                    ChatPost("/chat/send", body);
                }
                catch (WebException ex)
                {
                    var r = ex.Response as HttpWebResponse;
                    int code = r != null ? (int)r.StatusCode : 0;
                    if (code == 403)
                    {
                        try { using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8)) ban = new JavaScriptSerializer().DeserializeObject(sr.ReadToEnd()); } catch { }
                        err = "Ты не можешь писать в чат";
                    }
                    else err = code == 429 ? "Слишком много сообщений — попробуй позже" : code == 413 || code == 415 ? "Картинка не подошла — попробуй другую" : "Не получилось отправить — проверь интернет";
                }
                catch { err = "Не получилось отправить — проверь интернет"; }
                Win.Dispatcher.BeginInvoke(new Action(() =>
                {
                    fbSending = false;
                    Btn("FbSend").IsEnabled = true;
                    if (ban != null) { SetChatBan(ban); FbStatus("", false); return; }
                    if (err != null) { FbStatus(Translate.T(err), true); return; }
                    FbStatus("", false);
                    box.Text = "";
                    SetChatImage(null);
                    if (first) { S.Set("chatStarted", true); S.Save(); }
                    // журнал после первого сообщения об ошибке обычно уже не нужен
                    E<CheckBox>("FbLog").IsChecked = false;
                    LoadChat(true);
                    box.Focus();
                }));
            });
        }

        void ShowChatEmpty()
        {
            bool empty = E<StackPanel>("FbList").Children.Count == 0;
            El("FbEmpty").Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- пузыри сообщений ----
        void AddBubble(Dictionary<string, object> m, bool animate = false)
        {
            object o;
            long n = m.TryGetValue("n", out o) ? Convert.ToInt64(o) : 0;
            if (n <= fbLastN) return;
            fbLastN = n;
            bool dev = m.TryGetValue("dev", out o) && o is bool && (bool)o;
            string text = m.TryGetValue("text", out o) ? Convert.ToString(o) : "";
            string at = m.TryGetValue("at", out o) ? Convert.ToString(o) : "";
            string kind = m.TryGetValue("kind", out o) ? Convert.ToString(o) : "";

            var accent = ((SolidColorBrush)Win.FindResource("Accent")).Color;
            var muted = (Brush)Win.FindResource("Muted");
            var bubble = new Border
            {
                MaxWidth = 400, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(dev ? 0 : 60, 0, dev ? 60 : 0, 10),
                HorizontalAlignment = dev ? HorizontalAlignment.Left : HorizontalAlignment.Right, BorderThickness = new Thickness(2),
                Background = dev ? (Brush)Win.FindResource("Field") : new SolidColorBrush(Color.FromArgb(0x2E, accent.R, accent.G, accent.B)),
                BorderBrush = dev ? (Brush)Win.FindResource("Line") : new SolidColorBrush(Color.FromArgb(0x80, accent.R, accent.G, accent.B))
            };
            var sp = new StackPanel();
            if (dev)
            {
                var who = new TextBlock { FontSize = 11, Margin = new Thickness(0, 0, 0, 3) };
                who.Inlines.Add(new System.Windows.Documents.Run("COFFIOK") { Foreground = new SolidColorBrush(accent), FontWeight = FontWeights.Bold });
                who.Inlines.Add(new System.Windows.Documents.Run(" · " + Translate.T("разработчик")) { Foreground = muted });
                sp.Children.Add(who);
            }
            else if (kind == "bug" || kind == "idea" || kind == "other")
            {
                sp.Children.Add(new TextBlock { Text = Translate.T(kind == "bug" ? "Ошибка" : kind == "idea" ? "Идея" : "Вопрос"), FontSize = 11, Foreground = muted, Margin = new Thickness(0, 0, 0, 3) });
            }
            string img = m.TryGetValue("img", out o) ? Convert.ToString(o) : "";
            if (!string.IsNullOrEmpty(img)) sp.Children.Add(ChatImageView(img));
            // TextBox только для чтения — чтобы текст можно было выделить и скопировать
            if (text.Length > 0) sp.Children.Add(new TextBox
            {
                Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = Win.Foreground, FontSize = 13, Padding = new Thickness(0), Margin = new Thickness(-2, 0, 0, 0)
            });
            sp.Children.Add(new TextBlock { Text = ChatTime(at), FontSize = 10.5, Foreground = muted, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) });
            bubble.Child = sp;
            E<StackPanel>("FbList").Children.Add(bubble);
            if (animate && !Anim.Eco)
            {
                // пиксельное появление: тремя кадрами снизу вверх
                var tt = new TranslateTransform(0, 9);
                bubble.RenderTransform = tt;
                bubble.Opacity = 0;
                Px.Steps(bubble, UIElement.OpacityProperty, 0, 0, 0.5, 45, 0.85, 90, 1);
                Px.Steps(tt, TranslateTransform.YProperty, 0, 0, 9, 45, 4, 90, 0);
            }
            var sv = E<ScrollViewer>("FbScroll");
            sv.Dispatcher.BeginInvoke(new Action(sv.ScrollToEnd), DispatcherPriority.Loaded);
        }

        static string ChatTime(string iso, bool full = false)
        {
            DateTime t;
            if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) return "";
            t = t.ToLocalTime();
            if (t.Date == DateTime.Today && !full) return t.ToString("HH:mm");
            return t.ToString("d MMM, HH:mm", Translate.En ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("ru-RU"));
        }

        // ---- картинки ----
        void PickChatImage()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Translate.T("Картинки") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif", Title = Translate.T("Выбери фото или скриншот") };
            if (dlg.ShowDialog(Win) != true) return;
            try
            {
                var bi = new BitmapImage();
                using (var fs = File.OpenRead(dlg.FileName)) { bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = fs; bi.EndInit(); }
                SetChatImage(PrepImage(bi));
            }
            catch { FbStatus(Translate.T("Не получилось открыть картинку"), true); }
        }

        void SetChatImage(byte[] jpg)
        {
            fbPendingImg = jpg;
            El("FbAttachRow").Visibility = jpg != null ? Visibility.Visible : Visibility.Collapsed;
            E<Image>("FbAttachImg").Source = jpg != null ? DecodeImage(jpg, 240) : null;
            if (jpg != null) { E<CheckBox>("FbLog").IsChecked = E<RadioButton>("FbKindBug").IsChecked == true; E<TextBox>("FbText").Focus(); }
        }

        // уменьшаем до 1600 px по большей стороне и сжимаем в JPEG (не больше ~1.4 МБ)
        static byte[] PrepImage(BitmapSource src)
        {
            foreach (var t in new[] { new { Side = 1600, Q = 85 }, new { Side = 1280, Q = 75 }, new { Side = 1024, Q = 65 } })
            {
                BitmapSource b = src;
                double sc = Math.Min(1.0, t.Side / (double)Math.Max(src.PixelWidth, src.PixelHeight));
                if (sc < 1) b = new TransformedBitmap(src, new ScaleTransform(sc, sc));
                if (b.Format != PixelFormats.Bgr24 && b.Format != PixelFormats.Bgr32) b = new FormatConvertedBitmap(b, PixelFormats.Bgr24, null, 0);
                var enc = new JpegBitmapEncoder { QualityLevel = t.Q };
                enc.Frames.Add(BitmapFrame.Create(b));
                using (var ms = new MemoryStream()) { enc.Save(ms); if (ms.Length <= 1400 * 1024) return ms.ToArray(); }
            }
            throw new Exception("too big");
        }

        static BitmapImage DecodeImage(byte[] data, int width)
        {
            var bi = new BitmapImage();
            bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = new MemoryStream(data);
            if (width > 0) bi.DecodePixelWidth = width;
            bi.EndInit(); bi.Freeze();
            return bi;
        }

        static string UploadChatImage(string id, byte[] jpg)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(ShareServer + "/chat/img?id=" + id);
            req.Method = "POST"; req.ContentType = "image/jpeg"; req.UserAgent = "ZapretGUI/" + AppVersion; req.Timeout = 60000; req.ReadWriteTimeout = 60000;
            req.ContentLength = jpg.Length;
            using (var st = req.GetRequestStream()) st.Write(jpg, 0, jpg.Length);
            using (var r = req.GetResponse())
            using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
            {
                var d = new JavaScriptSerializer().DeserializeObject(sr.ReadToEnd()) as Dictionary<string, object>;
                object k;
                if (d == null || !d.TryGetValue("img", out k)) throw new WebException("no img");
                return Convert.ToString(k);
            }
        }

        UIElement ChatImageView(string img)
        {
            var im = new Image { MaxWidth = 300, MaxHeight = 240, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand };
            var wait = new TextBlock { Text = Translate.T("картинка…"), Foreground = (Brush)Win.FindResource("Muted"), FontSize = 12, Margin = new Thickness(8) };
            var box = new Border { BorderBrush = (Brush)Win.FindResource("Line"), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6), Child = wait };
            Action<byte[]> show = data =>
            {
                try { im.Source = DecodeImage(data, 600); box.Child = im; } catch { wait.Text = Translate.T("не удалось показать картинку"); }
            };
            im.MouseLeftButtonUp += (s, e) => OpenChatImage(img);
            byte[] cached;
            if (fbImgCache.TryGetValue(img, out cached)) { show(cached); return box; }
            string id = InstallId();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                byte[] data = null;
                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    var req = (HttpWebRequest)WebRequest.Create(ShareServer + "/chat/img/" + img + "?id=" + id);
                    req.UserAgent = "ZapretGUI/" + AppVersion; req.Timeout = 30000; req.ReadWriteTimeout = 30000;
                    using (var r = req.GetResponse())
                    using (var ms = new MemoryStream()) { r.GetResponseStream().CopyTo(ms); data = ms.ToArray(); }
                }
                catch (Exception logEx) { Log.Err("chat image", logEx); }
                Win.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (data == null) { wait.Text = Translate.T("картинка недоступна"); return; }
                    if (fbImgCache.Count > 40) fbImgCache.Clear();   // не держим в памяти слишком много картинок
                    fbImgCache[img] = data; show(data);
                    var sv = E<ScrollViewer>("FbScroll");
                    if (sv.VerticalOffset >= sv.ScrollableHeight - 300) sv.ScrollToEnd();
                }));
            });
            return box;
        }

        static void OpenChatImage(string img)
        {
            byte[] data;
            if (!fbImgCache.TryGetValue(img, out data)) return;
            try
            {
                string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ZapretChat");
                Directory.CreateDirectory(dir);
                string f = System.IO.Path.Combine(dir, img + (data.Length > 1 && data[0] == 0x89 ? ".png" : ".jpg"));
                if (!File.Exists(f)) File.WriteAllBytes(f, data);
                System.Diagnostics.Process.Start(f);
            }
            catch (Exception logEx) { Log.Err("chat image open", logEx); }
        }

        void FbStatus(string text, bool error)
        {
            var m = E<TextBlock>("FbMsg");
            m.Text = text;
            m.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            m.Foreground = error ? new SolidColorBrush(Color.FromRgb(0xFF, 0x5E, 0x62)) : (Brush)Win.FindResource("Muted");
        }

        // случайный номер установки — по нему сервер находит переписку
        string InstallId()
        {
            string id = S.Str("tid");
            if (string.IsNullOrEmpty(id) || !Regex.IsMatch(id, "^[0-9a-f]{32}$"))
            {
                id = Guid.NewGuid().ToString("N");
                S.Set("tid", id); S.Save();
            }
            return id;
        }

        // последние строки журнала без имени пользователя, путей и IP
        static string LogTail(int lines, int maxChars)
        {
            try
            {
                if (string.IsNullOrEmpty(Log.FilePath) || !File.Exists(Log.FilePath)) return "";
                string[] all;
                using (var fs = new FileStream(Log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8)) all = sr.ReadToEnd().Split('\n');
                string s = string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)).Select(l => l.TrimEnd('\r')));
                s = CleanPrivate(s);
                return s.Length > maxChars ? s.Substring(s.Length - maxChars) : s;
            }
            catch { return ""; }
        }

        static string CleanPrivate(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = Regex.Replace(s, @"[A-Za-z]:\\Users\\[^\\""'\s]+", "%USER%", RegexOptions.IgnoreCase);
            string user = Environment.UserName ?? "";
            if (user.Length >= 3) s = Regex.Replace(s, Regex.Escape(user), "%USER%", RegexOptions.IgnoreCase);
            string pc = Environment.MachineName ?? "";
            if (pc.Length >= 3) s = Regex.Replace(s, Regex.Escape(pc), "%PC%", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b\d{1,3}(\.\d{1,3}){3}\b", "x.x.x.x");
            return s;
        }
    }
}
