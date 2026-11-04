// Zapret (C#) — «Написать разработчику»: ошибка, идея или пожелание уходит автору (в приложение Zapret Stats)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;

namespace ZapretCS
{
    partial class MainUi
    {
        bool fbSending;

        void WireFeedback()
        {
            Btn("FbBtn").Click += (s, e) => Safe(() => { CloseSettings(); After(200, OpenFeedback); });
            Btn("FbClose").Click += (s, e) => CloseOverlay("Fb");
            El("FbDim").MouseLeftButtonUp += (s, e) => { if (!fbSending) CloseOverlay("Fb"); };
            var text = E<TextBox>("FbText");
            var contact = E<TextBox>("FbContact");
            text.TextChanged += (s, e) =>
            {
                El("FbHint").Visibility = text.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                E<TextBlock>("FbCount").Text = text.Text.Length + " / 2000";
            };
            contact.TextChanged += (s, e) => El("FbContactHint").Visibility = contact.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            // для идей журнал обычно не нужен
            E<RadioButton>("FbKindBug").Checked += (s, e) => E<CheckBox>("FbLog").IsChecked = true;
            E<RadioButton>("FbKindIdea").Checked += (s, e) => E<CheckBox>("FbLog").IsChecked = false;
            E<RadioButton>("FbKindOther").Checked += (s, e) => E<CheckBox>("FbLog").IsChecked = false;
            Btn("FbSend").Click += (s, e) => Safe(SendFeedback);
            Win.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape && El("FbOverlay").Visibility == Visibility.Visible && !fbSending) CloseOverlay("Fb"); };
        }

        void OpenFeedback()
        {
            E<TextBlock>("FbMsg").Text = "";
            E<TextBlock>("FbMsg").Foreground = (System.Windows.Media.Brush)Win.FindResource("Muted");
            Btn("FbSend").IsEnabled = true;
            OpenOverlay("Fb");
            After(260, () => E<TextBox>("FbText").Focus());
        }

        void SendFeedback()
        {
            if (fbSending) return;
            string text = E<TextBox>("FbText").Text.Trim();
            if (text.Length < 5) { FbStatus(Translate.T("Напиши хотя бы пару слов"), true); return; }

            string kind = E<RadioButton>("FbKindIdea").IsChecked == true ? "idea" : E<RadioButton>("FbKindOther").IsChecked == true ? "other" : "bug";
            var os = Environment.OSVersion.Version;
            var body = new Dictionary<string, object> {
                { "id", InstallId() }, { "v", AppVersion }, { "z", ZapretCore.LocalVersion ?? "" },
                { "os", os.Major + "." + os.Minor + "." + os.Build }, { "lang", Translate.En ? "en" : "ru" },
                { "kind", kind }, { "text", text }, { "contact", E<TextBox>("FbContact").Text.Trim() },
                { "strat", S.Str("strategy", "") }, { "on", State == "on" }
            };
            if (E<CheckBox>("FbLog").IsChecked == true) body["log"] = LogTail(60, 6000);

            fbSending = true;
            Btn("FbSend").IsEnabled = false;
            FbStatus(Translate.T("Отправляю…"), false);
            string json = new JavaScriptSerializer().Serialize(body);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string err = null;
                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    var req = (HttpWebRequest)WebRequest.Create(ShareServer + "/fb");
                    req.Method = "POST"; req.UserAgent = "ZapretGUI/" + AppVersion; req.Timeout = 15000; req.ReadWriteTimeout = 15000;
                    req.ContentType = "application/json; charset=utf-8";
                    var data = Encoding.UTF8.GetBytes(json); req.ContentLength = data.Length;
                    using (var st = req.GetRequestStream()) st.Write(data, 0, data.Length);
                    using (req.GetResponse()) { }
                }
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
                    E<TextBox>("FbText").Text = "";
                    CloseOverlay("Fb");
                    Toast("Спасибо! Сообщение отправлено автору");
                }));
            });
        }

        void FbStatus(string text, bool error)
        {
            var m = E<TextBlock>("FbMsg");
            m.Text = text;
            m.Foreground = error ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x5E, 0x62)) : (System.Windows.Media.Brush)Win.FindResource("Muted");
        }

        // случайный номер установки — чтобы автор видел, что несколько сообщений от одного человека
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
