// Zapret (C#) — «Поделиться» кодом: список сайтов хранится на сервере, друг вводит короткий код
using System;
using System.Collections;
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
using System.Windows.Input;

namespace ZapretCS
{
    partial class MainUi
    {
        const string ShareServer = "https://zapret-share.chodaniel07.workers.dev";
        static readonly Regex CodeRx = new Regex(@"^\s*([A-HJKMNP-Z2-9]{3})[\s-]?([A-HJKMNP-Z2-9]{3})\s*$", RegexOptions.IgnoreCase);
        bool shareBusy;

        static string NormCode(string s)
        {
            var m = CodeRx.Match(s ?? "");
            return m.Success ? (m.Groups[1].Value + m.Groups[2].Value).ToUpperInvariant() : null;
        }
        static string PrettyCode(string c) { return c.Length == 6 ? c.Substring(0, 3) + "-" + c.Substring(3) : c; }

        void ShareView(string v)
        {
            El("ShareListBox").Visibility = v == "list" ? Visibility.Visible : Visibility.Collapsed;
            El("ShareCountRow").Visibility = v == "list" ? Visibility.Visible : Visibility.Collapsed;
            El("ShareCodeView").Visibility = v == "code" ? Visibility.Visible : Visibility.Collapsed;
            El("ShareEnterView").Visibility = v == "enter" ? Visibility.Visible : Visibility.Collapsed;
            Btn("ShareAlt").Visibility = Visibility.Collapsed;
            Btn("ShareGo").IsEnabled = true;
        }

        // ---- сеть (в фоновом потоке) ----
        static string Http(string method, string path, string body, out int status)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(ShareServer + path);
            req.Method = method; req.UserAgent = "ZapretGUI/" + AppVersion; req.Timeout = 12000; req.ReadWriteTimeout = 12000;
            if (body != null)
            {
                var data = Encoding.UTF8.GetBytes(body);
                req.ContentType = "application/json; charset=utf-8"; req.ContentLength = data.Length;
                using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            }
            HttpWebResponse resp;
            try { resp = (HttpWebResponse)req.GetResponse(); }
            catch (WebException ex)
            {
                resp = ex.Response as HttpWebResponse;
                if (resp == null) throw;
            }
            using (resp)
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                status = (int)resp.StatusCode;
                return r.ReadToEnd();
            }
        }

        void InBackground(Action work)
        {
            new Thread(() => { try { work(); } catch (Exception ex) { Log.Write("share: " + ex.Message); } }) { IsBackground = true }.Start();
        }
        void OnUi(Action a) { Win.Dispatcher.BeginInvoke(new Action(() => Safe(a))); }

        // ---- получить код ----
        void ShareMakeCode(Groups sel)
        {
            if (shareBusy) return;
            shareBusy = true;
            var go = Btn("ShareGo"); go.IsEnabled = false; go.Content = Translate.T("Создаю код…");
            Btn("ShareAlt").IsEnabled = false;
            var payload = new Dictionary<string, object>();
            var groups = new Dictionary<string, object>();
            foreach (var k in sel.Keys) groups[k] = sel[k].ToList();
            payload["groups"] = groups;
            string json = new JavaScriptSerializer().Serialize(payload);
            int n = sel.Total;
            InBackground(() =>
            {
                string code = null;
                try
                {
                    int st; string res = Http("POST", "/s", json, out st);
                    if (st == 200)
                    {
                        var o = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(res);
                        object c; if (o.TryGetValue("code", out c)) code = c as string;
                    }
                    else Log.Write("share post: " + st + " " + res);
                }
                catch (Exception ex) { Log.Write("share post: " + ex.Message); }
                OnUi(() =>
                {
                    shareBusy = false;
                    Btn("ShareAlt").IsEnabled = true;
                    if (code == null)
                    {
                        // сервер недоступен — всё равно даём поделиться: копируем список текстом
                        Clipboard.SetText(TextFromGroups(sel));
                        go.IsEnabled = true; go.Content = Translate.T("Получить код");
                        CloseOverlay("Share");
                        Toast("Сервер кодов не ответил — список скопирован текстом, вставь его в сообщение");
                        return;
                    }
                    ShareShowCode(code, n);
                });
            });
        }

        void ShareShowCode(string code, int n)
        {
            shareMode = "code";
            ShareView("code");
            string pretty = PrettyCode(code);
            Tb("ShareTitle").Text = Translate.T("Код готов");
            Tb("ShareSub").Text = Translate.T("Отправь его другу — он нажмёт «Ввести код» в своём Zapret.");
            Tb("ShareCodeText").Text = pretty;
            try { Clipboard.SetText(pretty); } catch (Exception logEx) { Log.Err("clipboard", logEx); }
            Tb("ShareCodeInfo").Text = Translate.T("Код уже скопирован · " + Plural(n) + " · работает 30 дней");
            Btn("ShareGo").Content = Translate.T("Готово");
            var card = El("ShareCodeCard");
            card.Opacity = 0; Anim.D(card, Anim.OP, 1, 260, 0);
            Anim.D(A("ShareCodeScale"), Anim.SX, 1, 520, 0.6, ease: Anim.Back());
            Anim.D(A("ShareCodeScale"), Anim.SY, 1, 520, 0.6, ease: Anim.Back());
        }

        // ---- ввести код ----
        void OpenEnterCode(string prefill)
        {
            shareMode = "enter";
            ShareView("enter");
            Tb("ShareTitle").Text = Translate.T("Сайты от друга");
            Tb("ShareSub").Text = Translate.T("Код из 6 символов, например K7M-4QX. Буквы можно вводить маленькими.");
            Tb("ShareEnterMsg").Text = "";
            var box = E<TextBox>("ShareCodeBox");
            box.Text = prefill != null ? PrettyCode(prefill) : "";
            Btn("ShareGo").Content = Translate.T("Найти");
            Btn("ShareGo").IsEnabled = NormCode(box.Text) != null;
            OpenOverlay("Share");
            After(120, () => { box.Focus(); box.CaretIndex = box.Text.Length; });
        }

        void ShareFindCode()
        {
            string code = NormCode(E<TextBox>("ShareCodeBox").Text);
            if (code == null || shareBusy) return;
            shareBusy = true;
            var go = Btn("ShareGo"); go.IsEnabled = false; go.Content = Translate.T("Ищу…");
            var msg = Tb("ShareEnterMsg"); msg.Foreground = Theme.B("#8A90A0"); msg.Text = "";
            InBackground(() =>
            {
                Groups got = null; string err = null;
                try
                {
                    int st; string res = Http("GET", "/s/" + code, null, out st);
                    if (st == 404) err = "Код не найден. Проверь его или попроси новый — коды работают 30 дней.";
                    else if (st != 200) err = "Сервер кодов ответил ошибкой (" + st + "). Попробуй чуть позже.";
                    else got = GroupsFromJson(res);
                }
                catch (Exception ex) { Log.Write("share get: " + ex.Message); err = "Не удалось связаться с сервером кодов. Проверь интернет и что zapret включён."; }
                OnUi(() =>
                {
                    shareBusy = false;
                    go.Content = Translate.T("Найти"); go.IsEnabled = true;
                    if (got == null || got.Total == 0)
                    {
                        msg.Foreground = Theme.B("#C9606F");
                        msg.Text = Translate.T(err ?? "По этому коду ничего нет.");
                        var wrap = E<Border>("ShareCodeBoxWrap");
                        var tt = new System.Windows.Media.TranslateTransform(); wrap.RenderTransform = tt;
                        Anim.D(tt, Anim.TX, 0, 420, 10, ease: new System.Windows.Media.Animation.ElasticEase { Oscillations = 3, Springiness = 4 });
                        return;
                    }
                    // убираем то, что у друга уже есть, и показываем список с галочками
                    var parsed = ParseSites(TextFromGroups(got));
                    if (parsed.Count == 0)
                    {
                        msg.Foreground = Theme.B("#8A90A0");
                        msg.Text = Translate.T("Все сайты из этого кода у тебя уже есть (" + parseSkipped + ").");
                        return;
                    }
                    OpenShare("import", parsed, false);
                });
            });
        }

        static Groups GroupsFromJson(string json)
        {
            var g = new Groups();
            if (json == null || json.Length > 64 * 1024) return g;
            var o = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            object gr; if (!o.TryGetValue("groups", out gr)) return g;
            var d = gr as IDictionary<string, object>;
            if (d == null) return g;
            foreach (var kv in d)
            {
                var list = kv.Value as IEnumerable;
                if (list == null || kv.Value is string) continue;
                string name = CleanGroupName(kv.Key) ?? "Прочее";
                foreach (var it in list) { var s = it as string; if (s != null && s.Length <= 253 && !g[name].Contains(s)) g[name].Add(s); }
            }
            foreach (var k in g.Keys.ToList()) if (g[k].Count == 0) g.Remove(k);
            return g;
        }

        void WireShareCode()
        {
            var box = E<TextBox>("ShareCodeBox");
            box.TextChanged += (s, e) =>
            {
                if (shareMode != "enter") return;
                Btn("ShareGo").IsEnabled = !shareBusy && NormCode(box.Text) != null;
                if (Tb("ShareEnterMsg").Text.Length > 0 && !shareBusy) Tb("ShareEnterMsg").Text = "";
            };
            box.KeyDown += (s, e) => { if (e.Key == Key.Return) Safe(ShareFindCode); };
            Tb("SharePasteList").MouseLeftButtonUp += (s, e) => Safe(() => PasteList(true));
            El("ShareCodeCard").MouseLeftButtonUp += (s, e) => Safe(() =>
            {
                try { Clipboard.SetText(Tb("ShareCodeText").Text); } catch (Exception logEx) { Log.Err("clipboard", logEx); }
                Anim.D(A("ShareCodeScale"), Anim.SX, 1, 380, 0.9, ease: Anim.Back());
                Anim.D(A("ShareCodeScale"), Anim.SY, 1, 380, 0.9, ease: Anim.Back());
                Toast("Код скопирован");
            });
            Btn("ShareAlt").Click += (s, e) => Safe(() =>
            {
                if (shareMode != "share") return;
                var sel = ShareSelection(); int n = sel.Total;
                Clipboard.SetText(TextFromGroups(sel));
                CloseOverlay("Share");
                Toast("Скопировано: " + Plural(n) + ". Вставь в сообщение другу (Ctrl+V)");
            });
        }
    }
}
