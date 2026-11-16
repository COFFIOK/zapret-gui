// Zapret (C#) — обновления: самого приложения (без окна установщика) и zapret в один клик
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using System.Windows.Threading;

namespace ZapretCS
{
    partial class MainUi
    {
        public const string UpdateRepo = "COFFIOK/zapret-gui";
        // что нового в этой версии (показывается после обновления)
        static readonly string[] WhatsNew = {
            "Бета «Игровая сеть»: проверка связи с другом — пинг, потери и скорость"
        };
        string UpdFlag { get { return System.IO.Path.Combine(GuiDir, "updated.flag"); } }
        bool updDoneShown;
        string latestZapret;
        string appUpdTag, appUpdUrl;
        static string appUpdDigest;

        static Version ParseVer(string s)
        {
            var m = Regex.Match(s ?? "", @"(\d+(\.\d+)+)");
            Version v; return m.Success && Version.TryParse(m.Groups[1].Value, out v) ? v : new Version(0, 0);
        }
        // работа в фоне с результатом в окне
        void Background(Action work, Action<Exception> done)
        {
            new Thread(() =>
            {
                Exception err = null;
                try { work(); } catch (Exception ex) { err = ex; }
                Win.Dispatcher.BeginInvoke(new Action(() => Safe(() => done(err))));
            }) { IsBackground = true }.Start();
        }
        static string LatestTag(string repo, out string exeUrl)
        {
            exeUrl = null; appUpdDigest = null;
            string tag = LatestTagRaw(repo, out exeUrl);
            // проверяем, что обновление берётся только из релизов этого репозитория на GitHub
            if (!Sec.SafeTag(tag)) throw new Exception("странное имя версии: " + tag);
            if (exeUrl != null && !exeUrl.StartsWith("https://github.com/" + repo + "/releases/download/", StringComparison.OrdinalIgnoreCase)) { Log.Write("update: чужой адрес " + exeUrl); exeUrl = null; }
            return tag;
        }
        static string LatestTagRaw(string repo, out string exeUrl)
        {
            exeUrl = null;
            try
            {
                // обычный путь: API GitHub — заодно получаем SHA-256 установщика
                var rel = GitHub.Latest(repo);
                var a = rel.Find(n => n.Equals("ZapretSetup.exe", StringComparison.OrdinalIgnoreCase));
                if (a != null) { exeUrl = a.Url; appUpdDigest = a.Sha256; }
                return rel.Tag;
            }
            catch (Exception apiErr)
            {
                // API не ответил (лимит запросов и т.п.): узнаём версию по переадресации страницы, чтобы хотя бы показать,
                // что вышло обновление. Скачать его без проверки всё равно не дадим — см. StartAppDownload.
                Log.Err("update api", apiErr);
                var r = (HttpWebRequest)WebRequest.Create("https://github.com/" + repo + "/releases/latest");
                r.UserAgent = "ZapretGUI"; r.AllowAutoRedirect = false;
                using (var resp = r.GetResponse())
                {
                    var m = Regex.Match(resp.Headers["Location"] ?? "", "/tag/([^/?#]+)");
                    if (!m.Success) throw new Exception("релизов пока нет");
                    exeUrl = "https://github.com/" + repo + "/releases/download/" + m.Groups[1].Value + "/ZapretSetup.exe";
                    return m.Groups[1].Value;
                }
            }
        }

        void WireUpdates()
        {
            Btn("AppUpdBtn").Click += (s, e) => Safe(StartAppDownload);
            Btn("AppUpdLater").Click += (s, e) => HideAppUpdate();
            Btn("UpdateBtn").Click += (s, e) => Safe(() =>
            {
                if (latestZapret != null && latestZapret != ZapretCore.LocalVersion) StartZapretUpdate(latestZapret);
                else StartUpdateCheck();
            });
            El("AuDim").MouseLeftButtonUp += (s, e) => { if (updDoneShown) CloseAu(); };
            Btn("AuOkBtn").Click += (s, e) => CloseAu();
            Btn("AuNewsBtn").Click += (s, e) => Safe(() =>
            {
                El("AuNewsBtn").Visibility = Visibility.Collapsed;
                El("AuNews").Visibility = Visibility.Visible; Anim.D(A("AuNews"), Anim.OP, 1, 300, 0);
            });
            if (File.Exists(UpdFile)) { StartAppUpdateCheck(false); StartUpdateCheck(); }
            else Tb("UpdText").Text = Translate.T("Обновления");
        }

        // ---------- приложение ----------
        public void StartAppUpdateCheck(bool manual)
        {
            string tag = null, url = null;
            Background(() => { tag = LatestTag(UpdateRepo, out url); }, err =>
            {
                if (err != null || tag == null) { if (manual) Toast("Не удалось проверить обновления приложения: " + (err != null ? err.Message : ""), true); return; }
                if (ParseVer(tag) > ParseVer(AppVersion)) ShowAppUpdate(tag, url);
                else if (manual) Toast("У тебя последняя версия приложения (v" + AppVersion + ")");
            });
        }
        void ShowAppUpdate(string tag, string url)
        {
            appUpdTag = tag; appUpdUrl = url;
            Tb("AppUpdText").Text = Translate.T("Доступна новая версия приложения " + tag);
            Btn("AppUpdBtn").Content = Translate.T("Обновить"); Btn("AppUpdBtn").IsEnabled = true; El("AppUpdTrack").Visibility = Visibility.Collapsed;
            El("AppUpdBar").Visibility = Visibility.Visible;
            Anim.D(A("AppUpdBar"), Anim.OP, 1, 300, 0);
            Anim.D(A("AppUpdShift"), Anim.TY, 10, 520, -30, ease: Anim.Back());
        }
        void HideAppUpdate()
        {
            Anim.D(A("AppUpdBar"), Anim.OP, 0, 200);
            Anim.D(A("AppUpdShift"), Anim.TY, -30, 250);
            After(260, () => El("AppUpdBar").Visibility = Visibility.Collapsed);
        }
        void StartAppDownload()
        {
            if (appUpdUrl == null) return;
            Btn("AppUpdBtn").IsEnabled = false;
            HideAppUpdate();
            OpenAu(false);
            Tb("AuTitle").Text = Translate.T("Обновление до " + appUpdTag);
            Tb("AuSub").Text = Translate.T("Скачиваю обновление…"); Tb("AuPct").Text = "";
            string file = null;
            try { file = System.IO.Path.Combine(Sec.TempDir("upd"), "ZapretSetup-" + appUpdTag + ".exe"); }
            catch (Exception ex) { CloseAu(); Toast("Не удалось подготовить обновление: " + ex.Message, true); ShowAppUpdate(appUpdTag, appUpdUrl); return; }
            string digest = appUpdDigest;
            long got = 0, total = 0;
            var tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            tick.Tick += (s, e) => ShowProgress(Interlocked.Read(ref got), Interlocked.Read(ref total), 0.9);
            tick.Start();
            string url = appUpdUrl, tag = appUpdTag;
            Background(() =>
            {
                // без способа проверить файл не скачиваем: нужна подпись автора или хотя бы сумма из API GitHub
                if (!Signature.Configured && string.IsNullOrEmpty(digest))
                {
                    var rel = GitHub.Latest(UpdateRepo);
                    var a = rel.Need(n => n.Equals("ZapretSetup.exe", StringComparison.OrdinalIgnoreCase), "установщика");
                    if (rel.Tag != tag || string.IsNullOrEmpty(a.Sha256)) throw new Exception("не удалось проверить обновление — попробуй чуть позже");
                    digest = a.Sha256;
                }
                Net.Download(url, file, (g, t) => { Interlocked.Exchange(ref got, g); Interlocked.Exchange(ref total, t); });
                // проверка: это программа, она совпадает с релизом на GitHub и подписана автором
                if (!Sec.LooksLikeExe(file)) throw new Exception("скачанный файл повреждён");
                if (!string.IsNullOrEmpty(digest) && Sec.Sha256(file) != digest) throw new Exception("файл не совпадает с релизом на GitHub (контрольная сумма)");
                if (Signature.Configured && !Signature.VerifyEmbedded(file))
                {
                    // релиз старого вида: подпись отдельным файлом рядом с установщиком
                    byte[] sig;
                    try { sig = GitHub.FetchSmall(url + ".sig", 4096); }
                    catch (Exception ex) { throw new Exception("у обновления нет подписи автора — не устанавливаю", ex); }
                    if (!Signature.Verify(file, sig)) throw new Exception("подпись обновления не совпадает — не устанавливаю");
                }
            }, err =>
            {
                tick.Stop();
                if (err != null)
                {
                    try { File.Delete(file); } catch { }
                    CloseAu(); Toast("Не удалось скачать обновление: " + err.Message, true);
                    ShowAppUpdate(appUpdTag, appUpdUrl); Btn("AppUpdBtn").Content = Translate.T("Повторить");
                    return;
                }
                Tb("AuSub").Text = Translate.T("Устанавливаю… Zapret откроется снова через пару секунд"); Tb("AuPct").Text = "";
                Anim.D(A("AuFillS"), Anim.SX, 1, 900);
                // установщик работает тихо, без своего окна
                Process.Start(file, "-Auto");
                After(1300, () => { Anim.D(A("Shell"), Anim.OP, 0, 260); After(280, () => Win.Close()); });
            });
        }
        void ShowProgress(long got, long total, double part)
        {
            if (total <= 0) return;
            double f = Math.Min(1, (double)got / total);
            Anim.D(A("AuFillS"), Anim.SX, f * part, 380);
            Tb("AuPct").Text = Translate.T(string.Format("{0}% · {1:N1} из {2:N1} МБ", (int)(100 * f), got / 1048576.0, total / 1048576.0));
        }
        void OpenAu(bool done)
        {
            if (Win.Icon != null) E<Image>("AuLogo").Source = Win.Icon;
            OpenOverlay("Au");
            var ok = El("AuOk");
            if (done)
            {
                El("AuProgress").Visibility = Visibility.Collapsed; El("AuArc").Opacity = 0; El("AuLogo").Opacity = 0;
                // галочка «выпрыгивает» и от неё расходится кольцо
                Anim.D(ok, Anim.OP, 1, 200, 0, begin: 250);
                Anim.D(A("AuOkS"), Anim.SX, 1, 520, 0.4, begin: 250, ease: Anim.Back()); Anim.D(A("AuOkS"), Anim.SY, 1, 520, 0.4, begin: 250, ease: Anim.Back());
                Anim.D(A("AuBurst"), Anim.OP, 0, 900, 0.9, begin: 450);
                Anim.D(A("AuBurstS"), Anim.SX, 1.7, 900, 1, begin: 450); Anim.D(A("AuBurstS"), Anim.SY, 1.7, 900, 1, begin: 450);
            }
            else
            {
                El("AuProgress").Visibility = Visibility.Visible; ((ScaleTransform)Win.FindName("AuFillS")).ScaleX = 0;
                El("AuBtns").Visibility = Visibility.Collapsed; El("AuNews").Visibility = Visibility.Collapsed; updDoneShown = false;
                El("AuArc").Opacity = 1; El("AuLogo").Opacity = 1; ok.Opacity = 0;
                StartAuSpinner();
                Anim.D(A("AuLogoS"), Anim.SX, 0.92, 900, 1, forever: true, autoReverse: true, low: true);
                Anim.D(A("AuLogoS"), Anim.SY, 0.92, 900, 1, forever: true, autoReverse: true, low: true);
            }
        }
        void CloseAu()
        {
            CloseOverlay("Au");
            After(210, () => { StopAuSpinner(); Anim.Stop(A("AuLogoS"), Anim.SX); Anim.Stop(A("AuLogoS"), Anim.SY); });
        }
        // после обновления установщик оставляет отметку — показываем «Обновлено»
        void ShowUpdatedIfNeeded()
        {
            if (!File.Exists(UpdFlag)) return;
            string v = ""; try { v = File.ReadAllText(UpdFlag).Trim(); } catch { }
            try { File.Delete(UpdFlag); } catch { }
            bool isZapret = v.StartsWith("zapret:");
            v = Regex.Replace(v, "^zapret:", "");
            if (v.Length == 0) v = AppVersion;
            updDoneShown = true;
            if (isZapret)
            {
                Tb("AuTitle").Text = Translate.T("zapret обновлён до v" + v);
                Tb("AuSub").Text = Translate.T("Твои сайты и настройки перенесены");
                El("AuNewsBtn").Visibility = Visibility.Collapsed;
            }
            else
            {
                Tb("AuTitle").Text = Translate.T("Zapret обновлён до v" + v);
                Tb("AuSub").Text = Translate.T("Все сайты и настройки на месте");
                El("AuNewsBtn").Visibility = Visibility.Visible;
                var list = E<Panel>("AuNewsList"); list.Children.Clear();
                foreach (var s in WhatsNew)
                {
                    var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                    row.Children.Add(new System.Windows.Shapes.Rectangle { Width = 6, Height = 6, Fill = Theme.B("#3DDC97"), Margin = new Thickness(0, 6, 10, 0), VerticalAlignment = VerticalAlignment.Top });
                    row.Children.Add(new TextBlock { Text = Translate.T(s), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Foreground = Theme.B("#E6E8EE") });
                    list.Children.Add(row);
                }
            }
            El("AuNews").Visibility = Visibility.Collapsed;
            El("AuBtns").Visibility = Visibility.Visible;
            OpenAu(true);
        }

        // ---------- zapret ----------
        void StartUpdateCheck(bool manual = false)
        {
            Tb("UpdText").Text = Translate.T("Проверяю…");
            ((ScaleTransform)Win.FindName("UpdFillS")).ScaleX = 0; El("UpdBar").Opacity = 1;
            Anim.D(A("UpdTrack"), Anim.OP, 1, 200);
            Anim.D(A("UpdBarT"), Anim.TX, 170, 1100, -40, forever: true, ease: Anim.Sine());
            string v = "";
            Background(() =>
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                using (var wc = new WebClient())
                {
                    wc.Headers.Add("Cache-Control", "no-cache"); wc.Headers.Add("User-Agent", "ZapretGUI");
                    v = wc.DownloadString("https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/.service/version.txt").Trim();
                }
            }, err =>
            {
                if (err != null) v = "";
                Anim.Stop(A("UpdBarT"), Anim.TX); El("UpdBar").Opacity = 0;
                E<Shape>("UpdFill").Fill = Theme.B(v.Length > 0 ? "#3DDC97" : "#FF5E62");
                Anim.D(A("UpdFillS"), Anim.SX, 1, 350, 0);
                Anim.D(A("UpdTrack"), Anim.OP, 0, 400, null, begin: 900);
                if (v.Length == 0) { if (manual) Toast("Не удалось проверить обновления zapret — нет связи с GitHub", true); }
                else if (v == ZapretCore.LocalVersion) { latestZapret = v; if (manual) Toast("У тебя последняя версия zapret ✓"); }
                else
                {
                    latestZapret = v;
                    Tb("UpdText").Text = Translate.T("Обновить до " + v);
                    var ub = Btn("UpdateBtn");
                    ub.Style = (Style)Win.FindResource("AccentBtn");
                    if (ub.Visibility != Visibility.Visible) { ub.Visibility = Visibility.Visible; Anim.D(ub, Anim.OP, 1, 250, 0); }
                    Toast("Вышла новая версия zapret: " + v + ". Нажми «Обновить до " + v + "» — всё сделается само.");
                }
            });
        }

        // запрос от второго запуска «Zapret.exe -InstallZapret версия», когда окно уже открыто
        void CheckInstallRequest()
        {
            string f = System.IO.Path.Combine(GuiDir, "install-zapret.request");
            if (!File.Exists(f)) return;
            string tag = "";
            try { tag = File.ReadAllText(f).Trim(); File.Delete(f); } catch (Exception logEx) { Log.Err("install request", logEx); }
            if (Regex.IsMatch(tag, @"^[0-9A-Za-z._-]{1,40}$")) After(300, () => Safe(() => StartZapretUpdate(tag, true)));
        }

        bool zuRunning;
        void StartZapretUpdate(string tag) { StartZapretUpdate(tag, false); }

        // exact — поставить именно эту версию (в том числе более старую); так её ставит Zapret Dev: Zapret.exe -InstallZapret 1.10.3
        void StartZapretUpdate(string tag, bool exact)
        {
            if (exact && tag == ZapretCore.LocalVersion) { Toast("zapret " + tag + " уже установлен"); return; }
            if (ZapretCore.ServiceState() != null) { Toast("zapret установлен как служба Windows. Удали службу через service.bat, потом обнови здесь.", true); return; }
            if (zuRunning) return;
            zuRunning = true;
            bool wasOn = State == "on";
            OpenAu(false);
            Tb("AuTitle").Text = Translate.T("Обновление zapret до " + tag);
            Tb("AuSub").Text = Translate.T("Скачиваю zapret…"); Tb("AuPct").Text = "";
            string parent = System.IO.Path.GetDirectoryName(ZapretCore.Root), newPath = null;
            long got = 0, total = 0; int phase = 0;
            var tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            tick.Tick += (s, e) =>
            {
                if (phase == 0) ShowProgress(Interlocked.Read(ref got), Interlocked.Read(ref total), 0.7);
                else { Tb("AuSub").Text = Translate.T("Распаковываю…"); Tb("AuPct").Text = ""; Anim.D(A("AuFillS"), Anim.SX, 0.8, 400); }
            };
            tick.Start();
            Background(() =>
            {
                // последний релиз через API GitHub: точный тег и SHA-256 архива; без них не обновляем
                var rel = exact ? GitHub.ByTag("Flowseal/zapret-discord-youtube", tag) : GitHub.Latest("Flowseal/zapret-discord-youtube");
                var asset = rel.Need(n => n.StartsWith("zapret-discord-youtube-", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase), "архива zapret");
                tag = rel.Tag;
                string tmp = Sec.TempDir("zapret");
                string zip = System.IO.Path.Combine(tmp, "zapret-" + tag + ".zip");
                GitHub.Fetch(asset, zip, (g, t) => { Interlocked.Exchange(ref got, g); Interlocked.Exchange(ref total, t); });
                phase = 1;
                // распаковываем рядом с zapret (в защищённую папку), а не во временную папку пользователя
                string ex = System.IO.Path.Combine(parent, ".unpack-" + Guid.NewGuid().ToString("N").Substring(0, 10));
                Sec.Extract(zip, ex);
                var top = Directory.GetDirectories(ex);
                string src = top.Length == 1 && !File.Exists(System.IO.Path.Combine(ex, "bin\\winws.exe")) ? top[0] : ex;
                if (!File.Exists(System.IO.Path.Combine(src, "bin\\winws.exe"))) throw new Exception("в архиве нет zapret (bin\\winws.exe)");
                string dst = System.IO.Path.Combine(parent, "zapret-discord-youtube-" + tag);
                if (Directory.Exists(dst)) dst += "-" + DateTime.Now.ToString("HHmmss");
                Directory.Move(src, dst);
                try { Directory.Delete(ex, true); Directory.Delete(tmp, true); } catch { }
                newPath = dst;
            }, err =>
            {
                tick.Stop(); zuRunning = false;
                if (err != null) { CloseAu(); Toast("Не удалось обновить zapret: " + err.Message, true); return; }
                // перенос сайтов и настроек, затем перезапуск уже на новой версии
                Tb("AuSub").Text = Translate.T("Переношу твои сайты и настройки…"); Anim.D(A("AuFillS"), Anim.SX, 0.92, 400);
                MigrateNow(ZapretCore.Root, newPath);
                Tb("AuSub").Text = Translate.T("Перезапускаю на новой версии…"); Anim.D(A("AuFillS"), Anim.SX, 1, 500);
                if (wasOn) S.Set("autoStartOnce", true);
                bool svc; ZapretCore.Stop(out svc);
                File.WriteAllText(UpdFlag, "zapret:" + tag, new UTF8Encoding(false));
                string old = ZapretCore.Root;
                if (!string.Equals(old, newPath, StringComparison.OrdinalIgnoreCase) && string.Equals(System.IO.Path.GetDirectoryName(old), System.IO.Path.GetDirectoryName(newPath), StringComparison.OrdinalIgnoreCase))
                    Process.Start(new ProcessStartInfo("cmd.exe", "/c timeout /t 4 /nobreak >nul & rmdir /s /q \"" + old + "\"") { CreateNoWindow = true, UseShellExecute = false });
                After(700, RestartApp);
            });
        }
    }
}
