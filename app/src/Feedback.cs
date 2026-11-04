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
using System.Windows.Threading;

namespace ZapretCS
{
    partial class MainUi
    {
        bool fbSending, fbLoading, fbOpen;
        long fbLastN;                       // номер последнего показанного сообщения
        DispatcherTimer fbFastTimer, fbSlowTimer;

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
            OpenOverlay("Fb");
            LoadChat(true);
            fbFastTimer.Start();
            After(260, () => E<TextBox>("FbText").Focus());
        }

        void CloseFeedback()
        {
            fbOpen = false;
            fbFastTimer.Stop();
            CloseOverlay("Fb");
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
                try { d = ChatPost("/chat/get", body); } catch { }
                Win.Dispatcher.BeginInvoke(new Action(() =>
                {
                    fbLoading = false;
                    if (d == null) return;
                    object m; d.TryGetValue("msgs", out m);
                    var list = m as IEnumerable;
                    if (list != null) foreach (var x in list) { var msg = x as Dictionary<string, object>; if (msg != null) AddBubble(msg); }
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
            if (text.Length == 0) return;
            bool first = !S.Bool("chatStarted");
            string kind = E<RadioButton>("FbKindIdea").IsChecked == true ? "idea" : E<RadioButton>("FbKindOther").IsChecked == true ? "other" : "bug";
            var os = Environment.OSVersion.Version;
            var body = new Dictionary<string, object> {
                { "id", InstallId() }, { "v", AppVersion }, { "z", ZapretCore.LocalVersion ?? "" },
                { "os", os.Major + "." + os.Minor + "." + os.Build }, { "lang", Translate.En ? "en" : "ru" },
                { "kind", kind }, { "text", text }, { "strat", S.Str("strategy", "") }, { "on", State == "on" }
            };
            if (E<CheckBox>("FbLog").IsChecked == true) body["log"] = LogTail(60, 6000);

            fbSending = true;
            Btn("FbSend").IsEnabled = false;
            FbStatus(Translate.T("Отправляю…"), false);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string err = null;
                try { ChatPost("/chat/send", body); }
                catch (WebException ex)
                {
                    var r = ex.Response as HttpWebResponse;
                    err = r != null && (int)r.StatusCode == 429 ? "Слишком много сообщений — попробуй позже" : "Не получилось отправить — проверь интернет";
                }
                catch { err = "Не получилось отправить — проверь интернет"; }
                Win.Dispatcher.BeginInvoke(new Action(() =>
                {
                    fbSending = false;
                    Btn("FbSend").IsEnabled = true;
                    if (err != null) { FbStatus(Translate.T(err), true); return; }
                    FbStatus("", false);
                    box.Text = "";
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
        void AddBubble(Dictionary<string, object> m)
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
            // TextBox только для чтения — чтобы текст можно было выделить и скопировать
            sp.Children.Add(new TextBox
            {
                Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = Win.Foreground, FontSize = 13, Padding = new Thickness(0), Margin = new Thickness(-2, 0, 0, 0)
            });
            sp.Children.Add(new TextBlock { Text = ChatTime(at), FontSize = 10.5, Foreground = muted, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) });
            bubble.Child = sp;
            E<StackPanel>("FbList").Children.Add(bubble);
            var sv = E<ScrollViewer>("FbScroll");
            sv.Dispatcher.BeginInvoke(new Action(sv.ScrollToEnd), DispatcherPriority.Loaded);
        }

        static string ChatTime(string iso)
        {
            DateTime t;
            if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) return "";
            t = t.ToLocalTime();
            if (t.Date == DateTime.Today) return t.ToString("HH:mm");
            return t.ToString("d MMM, HH:mm", Translate.En ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("ru-RU"));
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
