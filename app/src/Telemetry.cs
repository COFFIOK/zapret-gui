// Zapret (C#) — анонимная статистика: спрашиваем один раз; если человек согласился — данные уходят ОДИН раз, больше ничего не отправляется.
// Отправляется: случайный номер установки, версии приложения/zapret/Windows, язык, тема, выбранная стратегия,
// включены ли Telegram-прокси/игровой фильтр/экономия/автообновление, сколько своих сайтов (только число).
// НЕ отправляется: имя, IP (сервер его не хранит), списки сайтов, файлы.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;

namespace ZapretCS
{
    partial class MainUi
    {
        void StartTelemetry()
        {
            Btn("CsYes").Click += (s, e) => Safe(() => { S.Set("statsConsent", "yes"); S.Save(); CloseOverlay("Cs"); SendStatsOnce(); Toast("Спасибо! Данные отправлены один раз"); });
            Btn("CsNo").Click += (s, e) => Safe(() => { S.Set("statsConsent", "no"); S.Save(); CloseOverlay("Cs"); });
            string c = S.Str("statsConsent", "");
            if (c == "yes") { SendStatsOnce(); return; }          // согласился, но в прошлый раз не получилось отправить
            if (c == "no") return;
            AskConsentWhenFree(3500);
        }

        // показываем вопрос, когда не открыто другое окно (что нового, перенос, настройки и т.п.)
        void AskConsentWhenFree(int delay)
        {
            After(delay, () =>
            {
                if (S.Str("statsConsent", "") != "") return;
                bool busy = !Win.IsVisible || Win.WindowState == WindowState.Minimized;
                foreach (var n in new[] { "AuOverlay", "MigOverlay", "SettingsOverlay", "TestOverlay", "ShareOverlay", "FbOverlay", "UninstOverlay", "CloseOverlay", "QrOverlay" })
                {
                    var el = Win.FindName(n) as UIElement;
                    if (el != null && el.Visibility == Visibility.Visible) { busy = true; break; }
                }
                if (busy) { AskConsentWhenFree(4000); return; }
                OpenOverlay("Cs");
            });
        }

        void SendStatsOnce()
        {
            if (S.Str("statsConsent", "") != "yes" || S.Bool("statsSent")) return;
            int sites = 0, groups = 0;
            try { var g = ReadUserGroups(); sites = g.Total; groups = g.Count; } catch { }
            string gm = "", gt, gu; try { ZapretCore.ReadGame(out gm, out gt, out gu); } catch { }
            var os = Environment.OSVersion.Version;
            var body = new Dictionary<string, object> {
                { "id", InstallId() }, { "v", AppVersion },
                { "z", ZapretCore.LocalVersion ?? "" },
                { "os", os.Major + "." + os.Minor + "." + os.Build },
                { "lang", Translate.En ? "en" : "ru" },
                { "theme", Theme.Light ? "light" : "dark" },
                { "eco", Anim.Eco },
                { "strat", S.Str("strategy", "") },
                { "on", State == "on" },
                { "tg", S.Bool("tgEnabled") || tgState == "on" },
                { "autoupd", SafeExists(UpdFile) },
                { "game", gm ?? "" },
                { "sites", sites }, { "groups", groups },
                { "home", InHome }
            };
            string json = new JavaScriptSerializer().Serialize(body);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool ok = false;
                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    var req = (HttpWebRequest)WebRequest.Create(ShareServer + "/t/ping");
                    req.Method = "POST"; req.UserAgent = "ZapretGUI/" + AppVersion; req.Timeout = 15000; req.ReadWriteTimeout = 15000;
                    var data = Encoding.UTF8.GetBytes(json);
                    req.ContentType = "application/json; charset=utf-8"; req.ContentLength = data.Length;
                    using (var st = req.GetRequestStream()) st.Write(data, 0, data.Length);
                    using (req.GetResponse()) { }
                    ok = true;
                }
                catch { }   // нет сети — попробуем при следующем запуске
                if (ok) Win.Dispatcher.BeginInvoke(new Action(() => { S.Set("statsSent", true); S.Save(); Log.Write("statistics sent once"); }));
            });
        }

        static bool SafeExists(string f) { try { return File.Exists(f); } catch { return false; } }
    }
}
