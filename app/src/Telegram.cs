// Zapret (C#) — Telegram: прокси tg-ws-proxy от Flowseal, «Подключить на ПК», QR для телефона
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using System.Windows.Threading;

namespace ZapretCS
{
    partial class MainUi
    {
        string TgDir, TgExe, TgData, TgCfg;
        const string TgUrl = "https://github.com/Flowseal/tg-ws-proxy/releases/latest/download/TgWsProxy_windows.exe";
        string tgState = "off", tgPrevState;
        long tgVisAt;
        bool? tgConnShown; long tgConnShownAt; bool tgIsConnected;
        DispatcherTimer tgTimer;
        // скачивание прокси
        volatile bool tgDlDone; long tgDlGot, tgDlTotal; string tgDlError; bool tgDownloading;
        // QR
        string qrLink; bool qrReady;

        Brush TgDotFill { get { return E<Shape>("TgDot").Fill; } }
        Brush TgPlaneBrush { get { return E<TextBlock>("TgPlane").Foreground; } }
        CheckBox TgToggle { get { return E<CheckBox>("TgToggle"); } }

        void WireTelegram()
        {
            TgDir = System.IO.Path.Combine(InHome ? HomeDir : GuiDir, "telegram");
            TgExe = System.IO.Path.Combine(TgDir, "TgWsProxy_windows.exe");
            TgData = System.IO.Path.Combine(TgDir, "TgWsProxy_data");
            TgCfg = System.IO.Path.Combine(TgData, "config.json");
            Mon.TgCfgPath = TgCfg;

            // свои кисти (анимируемые) и самолётик, который берёт их цвета
            E<Shape>("TgDot").Fill = Theme.B("#2A2E38");
            E<TextBlock>("TgPlane").Foreground = Theme.B("#8A90A0");
            E<Shape>("TgLed").Fill = Theme.B("#8A90A0");
            var card = E<Border>("TgCard"); card.Background = Theme.B("#1A1D24"); card.BorderBrush = Theme.B("#232731");
            E<Shape>("TgPlaneFill").Fill = TgPlaneBrush;
            E<Shape>("TgPlaneDraw").Fill = TgPlaneBrush;
            E<Shape>("TgPlaneFold").Fill = TgDotFill;

            var power = Btn("TgPower");
            power.Click += (s, e) => Safe(() =>
            {
                if (!TgToggle.IsEnabled) return;
                TgToggle.IsChecked = TgToggle.IsChecked != true;
                if (TgToggle.IsChecked == true) StartTg(); else StopTg();
            });
            power.MouseEnter += (s, e) => Px.Steps(A("TgLift"), Anim.TY, 0, 0, -2);
            power.MouseLeave += (s, e) => Px.Steps(A("TgLift"), Anim.TY, 0, 0, 0);
            power.PreviewMouseLeftButtonDown += (s, e) => Px.Steps(A("TgLift"), Anim.TY, 0, 0, 2);
            power.PreviewMouseLeftButtonUp += (s, e) => Px.Steps(A("TgLift"), Anim.TY, 0, 0, -2);

            Btn("TgConnect").Click += (s, e) => Safe(() =>
            {
                string link = TgLink();
                if (link == null) { Toast("Сначала включи Telegram-прокси", true); return; }
                Anim.Stop(Btn("TgConnect"), Anim.OP); Btn("TgConnect").Opacity = 1;
                S.Set("tgConnected", true); S.Save();
                try { Process.Start(link); }
                catch { Clipboard.SetText(link); Toast("Telegram не открылся. Ссылка скопирована — отправь её себе в «Избранное» и нажми.", true); return; }
                Toast("Открываю Telegram — нажми там «Подключить прокси».");
            });
            Btn("TgPhone").Click += (s, e) => Safe(OpenQr);
            Btn("QrClose").Click += (s, e) => CloseOverlay("Qr");
            El("QrDim").MouseLeftButtonUp += (s, e) => CloseOverlay("Qr");
            Btn("QrCopy").Click += (s, e) => Safe(() => { if (qrLink != null) { Clipboard.SetText(qrLink); Toast("Ссылка скопирована — можно отправить её себе на телефон"); } });

            tgTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            tgTimer.Tick += (s, e) => { try { TgTick(); } catch (Exception ex) { Log.Write("tg: " + ex); } };

            // при запуске: прокси уже работает — показываем; был включён — включаем
            if (Process.GetProcessesByName("TgWsProxy_windows").Length > 0) { TgToggle.IsChecked = true; SetTgVisual("on", "Работает · 127.0.0.1:" + TgPort()); }
            else if (S.Bool("tgEnabled") && File.Exists(TgExe)) { TgToggle.IsChecked = true; StartTg(); }
            else SetTgVisual("off", "Выключен · нажми на самолётик");
            tgTimer.Start();
        }

        // ---- конфиг прокси ----
        Dictionary<string, object> TgConfig()
        {
            try { return new JavaScriptSerializer().DeserializeObject(File.ReadAllText(TgCfg, Encoding.UTF8)) as Dictionary<string, object>; }
            catch { return null; }
        }
        void SaveTgConfig(Dictionary<string, object> c)
        {
            var js = new JavaScriptSerializer();
            var sb = new StringBuilder("{\r\n"); int i = 0;
            foreach (var kv in c) { sb.Append("    ").Append(js.Serialize(kv.Key)).Append(": ").Append(js.Serialize(kv.Value)).Append(++i < c.Count ? ",\r\n" : "\r\n"); }
            File.WriteAllText(TgCfg, sb.Append("}").ToString(), new UTF8Encoding(false));
        }
        int TgPort()
        {
            var c = TgConfig(); object p;
            if (c != null && c.TryGetValue("port", out p)) { int v; if (int.TryParse(Convert.ToString(p), out v) && v > 0) return v; }
            return 1443;
        }
        void EnsureTgConfig()
        {
            // портативный режим: настройки рядом с exe, без окна приветствия и своей проверки обновлений
            Directory.CreateDirectory(TgData);
            if (!File.Exists(TgCfg))
            {
                var b = new byte[16]; using (var r = new RNGCryptoServiceProvider()) r.GetBytes(b);
                string secret = string.Concat(b.Select(x => x.ToString("x2")));
                var cfg = new Dictionary<string, object> { { "host", "127.0.0.1" }, { "port", 1443 }, { "secret", secret }, { "check_updates", false }, { "language", "ru" } };
                SaveTgConfig(cfg);
            }
            var marker = System.IO.Path.Combine(TgData, ".first_run_done_mtproto");
            if (!File.Exists(marker)) File.WriteAllText(marker, "");
        }
        string TgLink()
        {
            var c = TgConfig(); if (c == null) return null;
            object h, sec; c.TryGetValue("host", out h); c.TryGetValue("secret", out sec);
            string host = h as string; if (string.IsNullOrEmpty(host) || host == "0.0.0.0") host = "127.0.0.1";
            return "tg://proxy?server=" + host + "&port=" + TgPort() + "&secret=dd" + sec;
        }
        void StartTgProcess() { Process.Start(new ProcessStartInfo(TgExe) { WorkingDirectory = TgDir, UseShellExecute = false, CreateNoWindow = true }); }
        static void KillTg() { foreach (var p in Process.GetProcessesByName("TgWsProxy_windows")) { try { p.Kill(); } catch { } p.Dispose(); } }

        public void StartTg()
        {
            if (!File.Exists(TgExe)) { StartTgDownload(); return; }
            EnsureTgConfig();
            if (Process.GetProcessesByName("TgWsProxy_windows").Length == 0) StartTgProcess();
            S.Set("tgEnabled", true); S.Save();
            SetTgVisual("on", "Работает · 127.0.0.1:" + TgPort());
            if (!S.Bool("tgConnected"))
            {
                Toast("Telegram-прокси запущен. Нажми «Подключить», чтобы настроить Telegram Desktop.");
                Anim.D(Btn("TgConnect"), Anim.OP, 0.4, 500, 1, forever: true, autoReverse: true, low: true);
            }
            UpdateTrayMenu(false);
        }
        public void StopTg()
        {
            qrReady = false;
            KillTg();
            S.Set("tgEnabled", false); S.Save();
            SetTgVisual("off", "Выключен · нажми на самолётик");
            UpdateTrayMenu(false);
        }

        void StartTgDownload()
        {
            SetTgVisual("busy", "Скачиваю tg-ws-proxy…");
            TgToggle.IsEnabled = false;
            Directory.CreateDirectory(TgDir);
            tgDownloading = true; tgDlDone = false; tgDlGot = 0; tgDlTotal = 0; tgDlError = null;
            string part = TgExe + ".part";
            new Thread(() =>
            {
                try { Net.Download(TgUrl, part, (got, total) => { Interlocked.Exchange(ref tgDlGot, got); Interlocked.Exchange(ref tgDlTotal, total); }); }
                catch (Exception ex) { tgDlError = ex.Message; }
                tgDlDone = true;
            }) { IsBackground = true }.Start();
        }

        void TgTick()
        {
            if (tgDownloading)
            {
                long tot = Interlocked.Read(ref tgDlTotal), got = Interlocked.Read(ref tgDlGot);
                if (tot > 0) E<TextBlock>("TgStatus").Text = Translate.T("Скачиваю tg-ws-proxy · " + (int)(100 * got / tot) + "%");
                if (!tgDlDone) return;
                tgDownloading = false; TgToggle.IsEnabled = true;
                string part = TgExe + ".part";
                if (tgDlError != null || !File.Exists(part) || new FileInfo(part).Length < 100000)
                {
                    try { File.Delete(part); } catch { }
                    TgToggle.IsChecked = false; SetTgVisual("off", "Не удалось скачать");
                    Toast("Не удалось скачать tg-ws-proxy с GitHub: " + tgDlError, true);
                }
                else { if (File.Exists(TgExe)) File.Delete(TgExe); File.Move(part, TgExe); StartTg(); }
                return;
            }
            // данные от фонового монитора; свежие — только если сняты после последнего переключения
            if (Interlocked.Read(ref Mon.At) <= tgVisAt) return;
            UpdateTgConnected();
            bool running = Mon.Tg;
            if (tgState == "on" && !running) { TgToggle.IsChecked = false; SetTgVisual("off", "Выключен · нажми на самолётик"); }
            else if (tgState == "off" && running) { TgToggle.IsChecked = true; SetTgVisual("on", "Работает · 127.0.0.1:" + TgPort()); }
        }

        void UpdateTgConnected()
        {
            if (tgState != "on") return;
            bool now = Mon.TgConn;
            if (tgConnShown.HasValue && tgConnShown.Value == now && tgConnShownAt == tgVisAt) return;
            tgConnShown = now; tgConnShownAt = tgVisAt;
            var connect = Btn("TgConnect");
            if (now)
            {
                E<TextBlock>("TgStatus").Text = Translate.T("Подключён к Telegram");
                Anim.Color(E<Shape>("TgLed").Fill, "#3DDC97");
                if (connect.Visibility == Visibility.Visible)
                {
                    Anim.Stop(connect, Anim.OP); Anim.D(connect, Anim.OP, 0, 250);
                    After(260, () => { if (tgIsConnected) connect.Visibility = Visibility.Collapsed; });
                }
                if (!S.Bool("tgConnected")) { S.Set("tgConnected", true); S.Save(); }
            }
            else
            {
                E<TextBlock>("TgStatus").Text = Translate.T("Работает · 127.0.0.1:" + TgPort());
                Anim.Color(E<Shape>("TgLed").Fill, "#5ED3F3");
                if (connect.Visibility != Visibility.Visible) { connect.Visibility = Visibility.Visible; Anim.D(connect, Anim.OP, 1, 250, 0); }
            }
            tgIsConnected = now;
        }

        // ---- внешний вид карточки и самолётик ----
        void SetTgVisual(string mode, string text)
        {
            tgPrevState = tgState;
            tgState = mode;
            E<TextBlock>("TgStatus").Text = Translate.T(text);
            tgVisAt = DateTime.UtcNow.Ticks; Mon.Wake();
            var card = E<Border>("TgCard");
            switch (mode)
            {
                case "on":
                    Anim.Color(TgDotFill, "#5ED3F3"); Anim.Color(TgPlaneBrush, "#0F1115"); Anim.Color(E<Shape>("TgLed").Fill, "#5ED3F3");
                    Anim.Color(card.BorderBrush, "#2B5664");
                    Anim.Color(card.Background, Theme.Light ? Theme.Mix("#5ED3F3", "#FFFFFF", 0.10) : Theme.Mix("#5ED3F3", "#1A1D24", 0.09));
                    if (tgPrevState != "on") PlaneTakeoff();
                    El("TgActions").Visibility = Visibility.Visible;
                    foreach (var n in new[] { "TgConnect", "TgPhone" })
                        if (El(n).Visibility != Visibility.Visible) { El(n).Visibility = Visibility.Visible; Anim.D(A(n), Anim.OP, 1, 250, 0); }
                    break;
                case "busy":
                    Anim.Color(TgDotFill, "#2B5664"); Anim.Color(TgPlaneBrush, "#CFEFF8"); Anim.Color(E<Shape>("TgLed").Fill, "#5ED3F3");
                    PlaneBusy();
                    break;
                default:
                    tgIsConnected = false;
                    Anim.Color(TgDotFill, "#2A2E38"); Anim.Color(TgPlaneBrush, "#8A90A0"); Anim.Color(E<Shape>("TgLed").Fill, "#8A90A0");
                    Anim.Color(card.BorderBrush, "#232731"); Anim.Color(card.Background, "#1A1D24");
                    PlaneStop();
                    foreach (var n in new[] { "TgConnect", "TgPhone", "TgActions" }) El(n).Visibility = Visibility.Collapsed;
                    break;
            }
        }
        void PlaneStop()
        {
            var t = A("TgPlaneT"); Anim.Stop(t, Anim.TX); Anim.Stop(t, Anim.TY);
            var tt = (TranslateTransform)t; tt.X = 0; tt.Y = 0;
            var gl = E<Path>("TgGlow"); gl.BeginAnimation(Path.DataProperty, null); gl.Data = Px.Ring(80, 4, 40, 36);
            Anim.Stop(gl, Anim.OP); gl.Opacity = 0;
            Anim.Stop(A("TgPlaneFill"), Anim.OP); El("TgPlaneFill").Opacity = 1;
            Anim.Stop(A("TgPlaneFold"), Anim.OP); El("TgPlaneFold").Opacity = 1;
        }
        void PlaneTakeoff()
        {
            // пиксельный взлёт: рывками улетает вправо-вверх, влетает слева-снизу, мигает и расходится квадратная волна
            PlaneStop();
            var t = A("TgPlaneT");
            Px.Steps(t, Anim.TX, 0, 0, 6, 60, 15, 120, 27, 180, 40, 240, -40, 300, -27, 360, -15, 420, -6, 480, 0);
            Px.Steps(t, Anim.TY, 0, 0, -6, 60, -15, 120, -27, 180, -40, 240, 40, 300, 27, 360, 15, 420, 6, 480, 0);
            foreach (var n in new[] { "TgPlaneFill", "TgPlaneFold" })
                Px.Steps(A(n), Anim.OP, 480, 0, 0, 80, 1, 160, 0, 240, 1, 320, 0.4, 400, 1);
            var gl = E<Path>("TgGlow");
            var frames = new List<Geometry>();
            for (int k = 0; k < 5; k++) frames.Add(Px.Ring(80, 4, 30 + k * 2.5, 26 + k * 2.5));
            Px.PlayFrames(gl, frames, 80, false, 480);
            Px.Steps(gl, Anim.OP, 480, 0, 0.9, 80, 0.75, 160, 0.6, 240, 0.4, 320, 0.2, 400, 0);
        }
        void PlaneBusy()
        {
            // только пока скачивается прокси — самолётик пролетает через кнопку рывками
            PlaneStop();
            if (Anim.Eco) return;
            foreach (var p in new[] { Anim.TX, Anim.TY })
            {
                double sg = p == Anim.TX ? 1 : -1;
                var a = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = new Duration(TimeSpan.FromMilliseconds(900)) };
                for (int k = 0; k < 9; k++) a.KeyFrames.Add(new DiscreteDoubleKeyFrame(sg * (-36 + k * 9), KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(k * 100))));
                A("TgPlaneT").BeginAnimation(p, a);
            }
        }

        // ================= QR для телефона =================
        void OpenQr()
        {
            if (Process.GetProcessesByName("TgWsProxy_windows").Length == 0) { Toast("Сначала включи Telegram-прокси", true); return; }
            OpenOverlay("Qr");
            if (qrReady && qrLink != null) return;   // код уже готов
            E<System.Windows.Shapes.Path>("QrPath").Data = null; El("QrWait").Visibility = Visibility.Visible; Tb("QrAddr").Text = "";
            bool needLan = !S.Bool("tgLanReady");
            string cfgPath = TgCfg, exe = TgExe;
            // всё тяжёлое (адрес в сети, брандмауэр, построение кода) — в фоне, чтобы анимация не тормозила
            new Thread(() =>
            {
                string err = null, link = null, addr = null; bool[,] matrix = null;
                try
                {
                    string ip = LanIp();
                    if (ip == null) throw new Exception("Не нашёл адрес компьютера в домашней сети");
                    var c = TgConfig();
                    if (needLan)
                    {
                        object h; c.TryGetValue("host", out h);
                        if ((h as string) != "0.0.0.0")
                        {
                            c["host"] = "0.0.0.0"; SaveTgConfig(c);
                            KillTg(); Thread.Sleep(400); StartTgProcess();
                        }
                        // разрешаем подключения к прокси только из своей подсети
                        RunHidden("netsh.exe", "advfirewall firewall delete rule name=\"Zapret Telegram proxy\"");
                        RunHidden("netsh.exe", "advfirewall firewall add rule name=\"Zapret Telegram proxy\" dir=in action=allow program=\"" + exe + "\" enable=yes profile=any remoteip=localsubnet");
                    }
                    object sec; c.TryGetValue("secret", out sec);
                    int port = TgPort();
                    link = "https://t.me/proxy?server=" + ip + "&port=" + port + "&secret=dd" + sec;
                    addr = ip + ":" + port;
                    matrix = Qr.Encode(link);
                }
                catch (Exception ex) { err = ex.Message; }
                Win.Dispatcher.BeginInvoke(new Action(() => Safe(() =>
                {
                    if (err != null) { Tb("QrWait").Text = Translate.T(err); Toast(err, true); return; }
                    if (needLan) { S.Set("tgLanReady", true); S.Save(); }
                    qrLink = link; Tb("QrAddr").Text = addr;
                    DrawQr(matrix); qrReady = true;
                })));
            }) { IsBackground = true }.Start();
        }
        static void RunHidden(string exe, string args)
        {
            try { using (var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false })) p.WaitForExit(8000); } catch { }
        }
        static string LanIp()
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var p = ni.GetIPProperties();
                if (!p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) continue;
                foreach (var a in p.UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254.")) return a.Address.ToString();
            }
            return null;
        }
        void DrawQr(bool[,] q)
        {
            int n = q.GetLength(0);
            var sg = new StreamGeometry();
            using (var ctx = sg.Open())
            {
                for (int r = 0; r < n; r++)
                    for (int c = 0; c < n; c++)
                        if (q[r, c])
                        {
                            ctx.BeginFigure(new Point(c, r), true, true);
                            ctx.LineTo(new Point(c + 1.02, r), false, false);
                            ctx.LineTo(new Point(c + 1.02, r + 1.02), false, false);
                            ctx.LineTo(new Point(c, r + 1.02), false, false);
                        }
                // уголки рамки, чтобы код не обрезался по краям
                ctx.BeginFigure(new Point(0, 0), true, true); ctx.LineTo(new Point(0.001, 0), false, false);
                ctx.BeginFigure(new Point(n, n), true, true); ctx.LineTo(new Point(n - 0.001, n), false, false);
            }
            sg.Freeze();
            var path = E<System.Windows.Shapes.Path>("QrPath");
            path.Data = sg; El("QrWait").Visibility = Visibility.Collapsed;
            path.Opacity = 0; Anim.D(path, Anim.OP, 1, 300, 0);
            Anim.D(A("QrBoxS"), Anim.SX, 1, 450, 0.88, ease: Anim.Back()); Anim.D(A("QrBoxS"), Anim.SY, 1, 450, 0.88, ease: Anim.Back());
        }
    }

    // скачивание файла с прогрессом (в фоновом потоке)
    static class Net
    {
        public static void Download(string url, string file, Action<long, long> progress)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            if (!Sec.TrustedHost(req.RequestUri)) throw new Exception("скачивание разрешено только с GitHub");
            req.UserAgent = "ZapretGUI"; req.AllowAutoRedirect = true; req.MaximumAutomaticRedirections = 5;
            using (var resp = req.GetResponse())
            using (var input = resp.GetResponseStream())
            using (var fs = File.Create(file))
            {
                if (!Sec.TrustedHost(resp.ResponseUri)) throw new Exception("файл пришёл не с GitHub: " + resp.ResponseUri.Host);
                long total = resp.ContentLength, got = 0;
                var buf = new byte[65536]; int n;
                while ((n = input.Read(buf, 0, buf.Length)) > 0) { fs.Write(buf, 0, n); got += n; if (progress != null) progress(got, total); }
            }
        }
    }
}
