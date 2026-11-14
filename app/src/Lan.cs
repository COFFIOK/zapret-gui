// Zapret (C#) — «Игровая сеть» (бета): аналог Хамачи для игр с друзьями.
// Внутри — EasyTier (github.com/EasyTier/EasyTier), скачивается с GitHub при первом подключении.
// Комната = код вроде K7M4-QX2D-P9WA; из кода получаются имя и пароль сети EasyTier, своего сервера не нужно.
// Сеть работает, пока Zapret открыт (в том числе в трее); при выходе из Zapret — отключается.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
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
    // ---------- код комнаты ----------
    static class LanRoom
    {
        const string Abc = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";   // без 0/O, 1/I
        public static string NewCode()
        {
            var b = new byte[12];
            using (var r = RandomNumberGenerator.Create()) r.GetBytes(b);
            var sb = new StringBuilder();
            for (int i = 0; i < 12; i++) { if (i > 0 && i % 4 == 0) sb.Append('-'); sb.Append(Abc[b[i] % 32]); }
            return sb.ToString();
        }
        // «k7m4 qx2d p9wa» → «K7M4-QX2D-P9WA»; null — код неверный
        public static string Normalize(string s)
        {
            var t = new StringBuilder();
            foreach (char ch in (s ?? "").ToUpperInvariant())
            {
                if (ch == '0' || ch == '1' || ch == 'O' || ch == 'I') return null;
                if (Abc.IndexOf(ch) >= 0) t.Append(ch);
            }
            if (t.Length != 12) return null;
            string x = t.ToString();
            return x.Substring(0, 4) + "-" + x.Substring(4, 4) + "-" + x.Substring(8, 4);
        }
        static string Hash(string s)
        {
            using (var h = SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-", "").ToLowerInvariant();
        }
        public static string NetName(string code) { return "zapretlan-" + Hash("name:" + code).Substring(0, 20); }
        public static string NetSecret(string code) { return Hash("secret:" + code); }
    }

    // задание Windows: дочерние процессы закрываются вместе с Zapret, даже если он упал
    static class Job
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a, string name);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, ref ExtLimit info, int len);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proc);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct BasicLimit { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct IoCounters { public ulong a, b, c, d, e, f; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct ExtLimit { public BasicLimit Basic; public IoCounters Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
        static IntPtr handle;
        public static void Add(Process p)
        {
            try
            {
                if (handle == IntPtr.Zero)
                {
                    handle = CreateJobObject(IntPtr.Zero, null);
                    var info = new ExtLimit();
                    info.Basic.LimitFlags = 0x2000;   // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    SetInformationJobObject(handle, 9, ref info, System.Runtime.InteropServices.Marshal.SizeOf(typeof(ExtLimit)));
                }
                AssignProcessToJobObject(handle, p.Handle);
            }
            catch (Exception logEx) { Log.Err("lan job", logEx); }
        }
    }

    // Проверка связи с другом: пинг (20 раз) и скорость в обе стороны.
    // Для скорости у каждого в комнате работает маленький сервер на адресе игровой сети (порт 11099).
    class LanCheck { public bool Running; public string Stage = "", Line = "", Verdict = "", Color = "#8A90A0"; }

    static class LanSpeed
    {
        public const int Port = 11099;
        const int Bytes = 2 * 1024 * 1024;
        static System.Net.Sockets.TcpListener srv;
        static string srvIp;
        static int active;

        // сервер слушает только адрес игровой сети — из обычной сети и интернета к нему не подключиться
        public static void Serve(string ip)
        {
            if (ip == srvIp && srv != null) return;
            StopServe();
            try
            {
                srv = new System.Net.Sockets.TcpListener(IPAddress.Parse(ip), Port);
                srv.Start(4);
                srvIp = ip;
                var l = srv;
                new Thread(() =>
                {
                    while (true)
                    {
                        System.Net.Sockets.TcpClient c;
                        try { c = l.AcceptTcpClient(); } catch { return; }
                        if (Interlocked.Increment(ref active) > 3) { Interlocked.Decrement(ref active); try { c.Close(); } catch { } continue; }
                        ThreadPool.QueueUserWorkItem(_ => { try { Handle(c); } catch (Exception logEx) { Log.Err("lan speed", logEx); } finally { Interlocked.Decrement(ref active); try { c.Close(); } catch { } } });
                    }
                }) { IsBackground = true, Name = "lan-speed" }.Start();
            }
            catch { srv = null; srvIp = null; }
        }
        public static void StopServe() { try { if (srv != null) srv.Stop(); } catch { } srv = null; srvIp = null; }

        static void Handle(System.Net.Sockets.TcpClient c)
        {
            c.ReceiveTimeout = 10000; c.SendTimeout = 10000;
            var st = c.GetStream();
            int cmd = st.ReadByte();
            var buf = new byte[65536];
            if (cmd == 'D') { for (int sent = 0; sent < Bytes; sent += buf.Length) st.Write(buf, 0, buf.Length); }
            else if (cmd == 'U')
            {
                int got = 0;
                while (got < Bytes) { int n = st.Read(buf, 0, buf.Length); if (n <= 0) return; got += n; }
                st.WriteByte(1);
            }
        }

        // мегабит/с; -1 — у друга нет сервера (старая версия Zapret) или не достучались
        public static double Measure(string ip, bool download)
        {
            try
            {
                using (var c = new System.Net.Sockets.TcpClient())
                {
                    var ar = c.BeginConnect(ip, Port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(3000)) return -1;
                    c.EndConnect(ar);
                    c.ReceiveTimeout = 12000; c.SendTimeout = 12000; c.NoDelay = true;
                    var st = c.GetStream();
                    var buf = new byte[65536];
                    var sw = Stopwatch.StartNew();
                    st.WriteByte((byte)(download ? 'D' : 'U'));
                    if (download)
                    {
                        int got = 0;
                        while (got < Bytes) { int n = st.Read(buf, 0, buf.Length); if (n <= 0) break; got += n; }
                        if (got < Bytes / 4) return -1;
                        return got * 8.0 / 1e6 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                    }
                    for (int sent = 0; sent < Bytes; sent += buf.Length) st.Write(buf, 0, buf.Length);
                    if (st.ReadByte() != 1) return -1;
                    return Bytes * 8.0 / 1e6 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                }
            }
            catch { return -1; }
        }
    }

    class LanPeer { public string Nick, Ip, Cost, Lat; public bool Local; }

    // ---------- EasyTier: скачать, запустить, спросить список ----------
    static class LanEt
    {
        // общие узлы сообщества (старый public.easytier.cn закрыт); подключаемся ко всем сразу, хватает любого живого
        public static readonly string[] DefaultNodes = { "tcp://us01.225284.xyz:11010", "udp://us01.225284.xyz:11010", "tcp://sh.vomiku.com:7910", "udp://sh.vomiku.com:7910" };
        public const string Rpc = "127.0.0.1:15899";
        public static string Dir;
        public static string Core { get { return Path.Combine(Dir, "easytier-core.exe"); } }
        public static string Cli { get { return Path.Combine(Dir, "easytier-cli.exe"); } }
        public static string LogFile { get { return Path.Combine(Dir, "easytier.log"); } }
        public static bool Installed { get { return File.Exists(Core) && File.Exists(Cli); } }

        // скачать последнюю версию для Windows x64 с GitHub (вызывать в фоне); вернёт версию
        public static string Install(Action<string> progress)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            progress("Ищу последнюю версию EasyTier…");
            string json;
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "ZapretGUI";
                wc.Encoding = Encoding.UTF8;
                json = wc.DownloadString("https://api.github.com/repos/EasyTier/EasyTier/releases/latest");
            }
            var rel = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            string tag = Convert.ToString(rel["tag_name"]);
            string url = null, digest = null;
            foreach (var a in (IEnumerable)rel["assets"])
            {
                var d = a as Dictionary<string, object>;
                string n = Convert.ToString(d["name"]);
                if (n.StartsWith("easytier-windows-x86_64", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    url = Convert.ToString(d["browser_download_url"]);
                    object dg; if (d.TryGetValue("digest", out dg) && dg is string && ((string)dg).StartsWith("sha256:")) digest = ((string)dg).Substring(7).ToLowerInvariant();
                    break;
                }
            }
            if (url == null) throw new Exception("в релизе " + tag + " нет сборки для Windows x64");

            Directory.CreateDirectory(Dir);
            string zip = Path.Combine(Dir, "easytier.zip.part");
            long last = 0;
            Net.Download(url, zip, (got, total) =>
            {
                if (total <= 0 || got - last < 300000 && got != total) return;
                last = got; progress("Скачиваю EasyTier " + tag + " · " + (int)(100 * got / total) + "%");
            });
            if (digest != null && Sec.Sha256(zip) != digest) { try { File.Delete(zip); } catch { } throw new Exception("скачанный файл не совпадает с релизом (контрольная сумма)"); }
            progress("Распаковываю…");
            Stop();
            using (var z = ZipFile.OpenRead(zip))
                foreach (var e in z.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue;
                    string n = e.Name.ToLowerInvariant();
                    if (!(n.EndsWith(".exe") || n.EndsWith(".dll") || n.EndsWith(".sys"))) continue;
                    if (n.StartsWith("easytier-web")) continue;    // веб-панель не нужна
                    e.ExtractToFile(Path.Combine(Dir, e.Name), true);
                }
            try { File.Delete(zip); } catch { }
            if (!Installed) throw new Exception("в архиве нет easytier-core.exe");
            return tag;
        }

        static Process proc;
        static StreamWriter log;
        static readonly object logLock = new object();
        static long logBytes;
        public static bool Running { get { try { return proc != null && !proc.HasExited; } catch { return false; } } }

        public static void Start(string code, string nick, string nodes)
        {
            Stop();
            try { log = new StreamWriter(LogFile, false, new UTF8Encoding(false)) { AutoFlush = true }; } catch { log = null; }
            var args = new StringBuilder();
            args.Append("--network-name ").Append(LanRoom.NetName(code));
            args.Append(" --network-secret ").Append(LanRoom.NetSecret(code));
            args.Append(" -d");                                   // адрес в сети раздаётся сам
            var list = string.IsNullOrWhiteSpace(nodes) ? DefaultNodes : nodes.Split(new[] { ',', ';', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var n in list) args.Append(" -p ").Append(Q(n.Trim()));
            args.Append(" --hostname ").Append(Q(nick));
            args.Append(" --rpc-portal ").Append(Rpc);
            args.Append(" --dev-name ZapretLAN");
            // слушаем только TCP и UDP: этого хватает для прямой связи, а лишние протоколы (faketcp) ставят драйвер и занимают порты
            args.Append(" -l tcp://0.0.0.0:11010,udp://0.0.0.0:11010");
            var psi = new ProcessStartInfo(Core, args.ToString())
            {
                WorkingDirectory = Dir, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            proc = new Process { StartInfo = psi };
            logBytes = 0;
            DataReceivedEventHandler w = (s, e) =>
            {
                if (e.Data == null || log == null) return;
                lock (logLock)
                {
                    try
                    {
                        // журнал не больше ~2 МБ: при обрывах EasyTier пишет одно и то же сотнями строк
                        if (logBytes > 2000000) { if (logBytes < 2000100) { log.WriteLine("… журнал обрезан"); logBytes = 2000200; } return; }
                        log.WriteLine(e.Data); logBytes += e.Data.Length + 2;
                    }
                    catch { }
                }
            };
            proc.OutputDataReceived += w; proc.ErrorDataReceived += w;
            proc.Start();
            Job.Add(proc);                      // если Zapret упадёт, Windows сама закроет и EasyTier
            proc.BeginOutputReadLine(); proc.BeginErrorReadLine();
            AllowFirewall();
        }

        public static void Stop()
        {
            try { if (proc != null && !proc.HasExited) { proc.Kill(); proc.WaitForExit(3000); } } catch { }
            proc = null;
            if (Dir != null)
                foreach (var p in Process.GetProcessesByName("easytier-core"))
                    try { if (string.Equals(p.MainModule.FileName, Core, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(2000); } } catch { }
            lock (logLock) { try { if (log != null) log.Dispose(); } catch { } log = null; }
        }

        // «tcp://1.2.3.4:11010» и т.п.; null — всё верно, иначе — что не так
        public static string CheckNodes(string nodes)
        {
            if (string.IsNullOrWhiteSpace(nodes)) return null;
            foreach (var n in nodes.Split(new[] { ',', ';', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                if (!Regex.IsMatch(n.Trim(), @"^(tcp|udp|ws|wss|quic|wg)://[^\s/:]+:\d{1,5}/?$", RegexOptions.IgnoreCase)) return n.Trim();
            return null;
        }

        static string Q(string s) { return "\"" + (s ?? "").Replace("\"", "") + "\""; }

        // Windows считает новую сеть «общественной» и закрывает порты игр — разрешаем входящие из адресов игровой сети
        static void AllowFirewall()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Run("netsh", "advfirewall firewall delete rule name=\"Zapret LAN\"", 8000);
                    Run("netsh", "advfirewall firewall add rule name=\"Zapret LAN\" dir=in action=allow remoteip=10.126.126.0/24 enable=yes profile=any", 8000);
                    Run("netsh", "advfirewall firewall add rule name=\"Zapret LAN\" dir=in action=allow program=\"" + Core + "\" enable=yes profile=any", 8000);
                }
                catch (Exception logEx) { Log.Err("lan firewall", logEx); }
            });
        }

        public static void RemoveFirewall() { try { Run("netsh", "advfirewall firewall delete rule name=\"Zapret LAN\"", 8000); } catch (Exception logEx) { Log.Err("lan firewall remove", logEx); } }

        static string Run(string exe, string args, int ms)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
            using (var p = Process.Start(psi))
            {
                var outTask = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(ms)) { try { p.Kill(); } catch { } return null; }
                return outTask.Result;
            }
        }

        // список участников; null — EasyTier ещё не отвечает
        public static int NodesSeen;   // сколько узлов поиска видно (при последнем опросе)
        public static List<LanPeer> Peers()
        {
            string o = Run(Cli, "-p " + Rpc + " -o json peer list", 4000);
            if (string.IsNullOrWhiteSpace(o)) return null;
            int i = o.IndexOf('['), j = o.IndexOf('{');
            if (i < 0 && j < 0) return null;
            object root = new JavaScriptSerializer().DeserializeObject(o.Substring(i >= 0 && (j < 0 || i < j) ? i : j));
            var d0 = root as Dictionary<string, object>;
            if (d0 != null && d0.ContainsKey("result")) root = d0["result"];
            var list = new List<LanPeer>();
            int nodes = 0;
            var e = root as IEnumerable;
            if (e == null) return list;
            foreach (var x in e)
            {
                var d = x as Dictionary<string, object>;
                if (d == null) continue;
                var p = new LanPeer { Ip = S(d, "ipv4"), Nick = S(d, "hostname"), Cost = S(d, "cost"), Lat = S(d, "lat_ms") };
                if (p.Ip == "") { string cidr = S(d, "cidr"); int k = cidr.IndexOf('/'); p.Ip = k > 0 ? cidr.Substring(0, k) : cidr; }
                p.Local = p.Cost == "Local";
                if (!p.Local && p.Ip == "") { nodes++; continue; }   // узел поиска, не игрок
                list.Add(p);
            }
            NodesSeen = nodes;
            return list;
        }
        static string S(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : ""; }
    }

    partial class MainUi
    {
        string lanCode;
        readonly Dictionary<string, LanCheck> lanChecks = new Dictionary<string, LanCheck>();
        List<LanPeer> lanLastList;
        bool lanBusy, lanPolling;
        DateTime lanStarted;
        DispatcherTimer lanTimer;

        void WireLan()
        {
            LanEt.Dir = System.IO.Path.Combine(InHome ? HomeDir : GuiDir, "easytier");
            lanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            lanTimer.Tick += (s, e) => LanPoll();

            Btn("LanBtn").Click += (s, e) => Safe(OpenLan);
            Btn("LanClose").Click += (s, e) => Safe(() => CloseOverlay("Lan"));
            Win.KeyDown += (s, e) => { if (e.Key == Key.Escape && El("LanOverlay").Visibility == Visibility.Visible) CloseOverlay("Lan"); };
            Btn("LanCreate").Click += (s, e) => Safe(() => LanJoin(LanRoom.NewCode()));
            Btn("LanJoin").Click += (s, e) => Safe(LanJoinTyped);
            Btn("LanLeave").Click += (s, e) => Safe(LanLeave);
            Btn("LanCopy").Click += (s, e) => Safe(() => { LanCopyText(lanCode); LanStatus("Код скопирован — отправь его друзьям", "#3DDC97"); });
            Btn("LanReinstall").Click += (s, e) => Safe(LanReinstall);
            E<TextBlock>("LanRoomCode").MouseLeftButtonUp += (s, e) => { LanCopyText(lanCode); LanStatus("Код скопирован — отправь его друзьям", "#3DDC97"); };

            var nick = E<TextBox>("LanNick"); var code = E<TextBox>("LanCode"); var nodes = E<TextBox>("LanNodes");
            nick.Text = S.Str("lanNick", S.Str("fbNick", Environment.UserName));
            code.Text = S.Str("lanLastRoom", "");
            nodes.Text = S.Str("lanNodes", "");
            Action hint = () => El("LanCodeHint").Visibility = code.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            code.TextChanged += (s, e) => hint(); hint();
            code.KeyDown += (s, e) => { if (e.Key == Key.Enter) Safe(LanJoinTyped); };

            var more = E<TextBlock>("LanMoreBtn");
            more.MouseLeftButtonUp += (s, e) =>
            {
                var p = El("LanMore");
                bool open = p.Visibility != Visibility.Visible;
                p.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                more.Text = Translate.T(open ? "Дополнительно ▴" : "Дополнительно ▾");
                if (open) { p.Opacity = 0; Anim.D(p, Anim.OP, 1, 200, 0); }
            };
            MouseButtonEventHandler openLog = (s, e) => { try { if (File.Exists(LanEt.LogFile)) Process.Start("notepad.exe", "\"" + LanEt.LogFile + "\""); else Toast("Журнала пока нет — он появится после первого подключения"); } catch { } };
            E<TextBlock>("LanLog").MouseLeftButtonUp += openLog;
            E<TextBlock>("LanLog2").MouseLeftButtonUp += openLog;
            E<TextBlock>("LanMyIp").MouseLeftButtonUp += (s, e) =>
            {
                string ip = E<TextBlock>("LanMyIp").Text;
                if (Regex.IsMatch(ip, @"^\d")) { LanCopyText(ip); LanStatus("Адрес скопирован", "#3DDC97"); }
            };

            // доступ только через «Бета-тестирование» в настройках
            var beta = E<CheckBox>("BetaToggle");
            beta.IsChecked = S.Bool("betaLan");
            El("LanBtn").Visibility = S.Bool("betaLan") ? Visibility.Visible : Visibility.Collapsed;
            beta.Click += (s, e) => Safe(() => SetBeta(beta.IsChecked == true));

            // выход из Zapret — сеть выключается (закрытие в трей сеть не трогает)
            Win.Closed += (s, e) => { try { LanSpeed.StopServe(); LanEt.Stop(); } catch (Exception logEx) { Log.Err("lan stop", logEx); } };
            LanStatus(LanEt.Installed ? "Готово к подключению" : "EasyTier скачается при первом подключении (≈30 МБ)", "#8A90A0");
            LanShowRoom(false, false);
        }

        // включили бета-тестирование — появляется кнопка и сразу ставится EasyTier; выключили — сеть останавливается и удаляется
        void SetBeta(bool on)
        {
            S.Set("betaLan", on); S.Save();
            var btn = El("LanBtn");
            if (on)
            {
                btn.Visibility = Visibility.Visible;
                if (!Anim.Eco) Anim.D(btn, Anim.OP, 1, 300, 0);
                if (LanEt.Installed) { Toast("Игровая сеть (бета) включена — значок в заголовке"); return; }
                lanBusy = true;
                LanStatus("Устанавливаю игровую сеть…", "#FFB547");
                Toast("Устанавливаю игровую сеть (бета)…");
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        string v = LanEt.Install(m => Win.Dispatcher.BeginInvoke(new Action(() => LanStatus(m, "#FFB547"))));
                        Win.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            lanBusy = false; S.Set("lanEtVer", v); S.Save();
                            LanStatus("Готово к подключению", "#8A90A0");
                            Toast("Игровая сеть (бета) готова — значок в заголовке");
                        }));
                    }
                    catch (Exception ex)
                    {
                        Log.Write("lan install: " + ex.Message);
                        Win.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            lanBusy = false;
                            LanStatus("Не получилось скачать EasyTier: " + ex.Message, "#FF5E62");
                            Toast("Не получилось установить игровую сеть — попробуй позже", true);
                        }));
                    }
                });
            }
            else
            {
                if (El("LanOverlay").Visibility == Visibility.Visible) CloseOverlay("Lan");
                lanTimer.Stop(); lanCode = null;
                LanShowRoom(false, false);
                btn.Visibility = Visibility.Collapsed;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    LanEt.Stop(); LanEt.RemoveFirewall();
                    try { if (System.IO.Directory.Exists(LanEt.Dir)) System.IO.Directory.Delete(LanEt.Dir, true); } catch (Exception logEx) { Log.Err("lan cleanup", logEx); }
                });
                Toast("Игровая сеть выключена и удалена");
            }
        }

        void OpenLan()
        {
            // тексты ставим при открытии: перевод интерфейса при запуске сбрасывает пустые поля
            var nick = E<TextBox>("LanNick");
            if (nick.Text.Trim() == "") nick.Text = S.Str("lanNick", S.Str("fbNick", Environment.UserName));
            if (lanCode == null && !lanBusy) LanStatus(LanEt.Installed ? "Готово к подключению" : "EasyTier скачается при первом подключении (≈30 МБ)", "#8A90A0");
            OpenOverlay("Lan");
            if (lanCode != null) LanPoll();
            var ver = S.Str("lanEtVer", "");
            E<TextBlock>("LanEtVer").Text = ver == "" ? "" : "EasyTier " + ver;
        }

        void LanJoinTyped()
        {
            string c = LanRoom.Normalize(E<TextBox>("LanCode").Text);
            if (c == null) { LanStatus("Код — 12 букв и цифр, например K7M4-QX2D-P9WA", "#FF5E62"); return; }
            LanJoin(c);
        }

        void LanJoin(string c)
        {
            if (lanBusy) return;
            string nick = Regex.Replace(E<TextBox>("LanNick").Text.Trim(), @"[^\p{L}\p{Nd}_\-\. ]", "");
            if (nick == "") { LanStatus("Впиши ник", "#FF5E62"); E<TextBox>("LanNick").Focus(); return; }
            string nodes = E<TextBox>("LanNodes").Text.Trim();
            string bad = LanEt.CheckNodes(nodes);
            if (bad != null) { LanStatus("Узел записан неверно: " + bad + " — нужно как tcp://1.2.3.4:11010", "#FF5E62"); return; }
            S.Set("lanNick", nick); S.Set("lanNodes", nodes); S.Save();
            lanBusy = true;
            LanStatus("Подготовка…", "#FFB547");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (!LanEt.Installed) { string v = LanEt.Install(m => Win.Dispatcher.BeginInvoke(new Action(() => LanStatus(m, "#FFB547")))); S.Set("lanEtVer", v); }
                    LanEt.Start(c, nick, nodes);
                    Win.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        lanBusy = false;
                        lanCode = c; S.Set("lanLastRoom", c); S.Save();
                        E<TextBox>("LanCode").Text = c;
                        E<TextBlock>("LanRoomCode").Text = c;
                        E<TextBlock>("LanMyIp").Text = "…";
                        var peers = E<StackPanel>("LanPeers"); peers.Children.Clear();
                        peers.Children.Add(LanNote("Ищу друзей…"));
                        LanShowRoom(true, true);
                        lanStarted = DateTime.Now;
                        LanStatus("Подключаюсь…", "#FFB547");
                        lanTimer.Start();
                        LanPoll();
                    }));
                }
                catch (Exception ex)
                {
                    Log.Write("lan: " + ex.Message);
                    Win.Dispatcher.BeginInvoke(new Action(() => { lanBusy = false; LanStatus("Не получилось: " + ex.Message, "#FF5E62"); }));
                }
            });
        }

        void LanLeave()
        {
            lanTimer.Stop();
            ThreadPool.QueueUserWorkItem(_ => { LanSpeed.StopServe(); LanEt.Stop(); });
            lanCode = null;
            lanChecks.Clear();
            LanShowRoom(false, true);
            LanStatus("Ты вышел из комнаты", "#8A90A0");
        }

        void LanReinstall()
        {
            if (lanBusy || lanCode != null) return;
            lanBusy = true;
            LanStatus("Обновляю EasyTier…", "#FFB547");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string v = LanEt.Install(m => Win.Dispatcher.BeginInvoke(new Action(() => LanStatus(m, "#FFB547"))));
                    S.Set("lanEtVer", v); S.Save();
                    Win.Dispatcher.BeginInvoke(new Action(() => { lanBusy = false; LanStatus("EasyTier " + v + " готов", "#3DDC97"); }));
                }
                catch (Exception ex) { Win.Dispatcher.BeginInvoke(new Action(() => { lanBusy = false; LanStatus("Не получилось: " + ex.Message, "#FF5E62"); })); }
            });
        }

        void LanPoll()
        {
            if (lanPolling || lanCode == null) return;
            if (!LanEt.Running)
            {
                lanTimer.Stop();
                LanStatus("EasyTier остановился — нажми «Журнал»", "#FF5E62");
                return;
            }
            // окно в трее или свёрнуто — список не нужен, сеть работает и так
            if (!Win.IsVisible || Win.WindowState == WindowState.Minimized || El("LanOverlay").Visibility != Visibility.Visible) { LanUpdateDot(true); return; }
            lanPolling = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                List<LanPeer> list = null;
                try { list = LanEt.Peers(); } catch (Exception logEx) { Log.Err("lan peers", logEx); }
                Win.Dispatcher.BeginInvoke(new Action(() => { lanPolling = false; if (lanCode != null) LanRender(list); }));
            });
        }

        void LanRender(List<LanPeer> list)
        {
            double secs = (DateTime.Now - lanStarted).TotalSeconds;
            if (list == null) { LanStatus(secs > 20 ? "EasyTier не отвечает — нажми «Журнал»" : "Подключаюсь…", "#FFB547"); return; }
            var me = list.FirstOrDefault(p => p.Local);
            bool hasIp = me != null && Regex.IsMatch(me.Ip ?? "", @"^\d+\.\d+\.\d+\.\d+$");
            E<TextBlock>("LanMyIp").Text = hasIp ? me.Ip : "…";
            if (hasIp) { string myIp = me.Ip; ThreadPool.QueueUserWorkItem(_ => LanSpeed.Serve(myIp)); }
            lanLastList = list;
            var others = list.Where(p => !p.Local).ToList();
            bool node = LanEt.NodesSeen > 0;
            if (hasIp) LanStatus(others.Count == 0 ? "В сети · ждём друзей" : "В сети · друзей: " + others.Count, "#3DDC97");
            else if (node) LanStatus("Связь с узлом есть · ждём друзей", "#3DDC97");     // адрес может появиться, когда войдёт первый друг
            else LanStatus(secs > 30 ? "Нет связи с узлами поиска — нажми «Журнал»" : "Ищу узлы поиска…", "#FFB547");
            E<TextBlock>("LanPeersTitle").Text = Translate.T("В комнате") + (others.Count > 0 ? " · " + others.Count : "");

            var panel = E<StackPanel>("LanPeers");
            panel.Children.Clear();
            if (others.Count == 0) { panel.Children.Add(LanNote("Пока никого. Отправь код комнаты другу — он появится здесь.")); return; }
            foreach (var p in others) panel.Children.Add(LanPeerRow(p));
        }

        UIElement LanPeerRow(LanPeer p)
        {
            bool direct = p.Cost == "p2p";
            var b = new Border { Background = Theme.B("#1E2129"), BorderBrush = Theme.B("#262A33"), BorderThickness = new Thickness(2), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 0, 8), Cursor = Cursors.Hand };
            var g = new DockPanel { ToolTip = Translate.T("Нажми, чтобы скопировать адрес") };
            double lat; bool hasLat = double.TryParse(p.Lat, NumberStyles.Any, CultureInfo.InvariantCulture, out lat) && lat > 0;
            string latColor = !hasLat ? "#8A90A0" : lat < 80 ? "#3DDC97" : lat < 160 ? "#FFB547" : "#FF5E62";
            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(new TextBlock { Text = hasLat ? Math.Round(lat) + " мс" : "—", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Theme.B(latColor), HorizontalAlignment = HorizontalAlignment.Right });
            right.Children.Add(new TextBlock { Text = Translate.T(direct ? "напрямую" : "через узел"), FontSize = 11.5, Foreground = Theme.B(direct ? "#8A90A0" : "#FFB547"), HorizontalAlignment = HorizontalAlignment.Right });
            DockPanel.SetDock(right, Dock.Right);
            g.Children.Add(right);
            var left = new StackPanel();
            var nl = new StackPanel { Orientation = Orientation.Horizontal };
            nl.Children.Add(new System.Windows.Shapes.Rectangle { Width = 8, Height = 8, Fill = Theme.B(direct ? "#3DDC97" : "#FFB547"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            nl.Children.Add(new TextBlock { Text = p.Nick == "" ? Translate.T("без ника") : p.Nick, FontSize = 14.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            left.Children.Add(nl);
            left.Children.Add(new TextBlock { Text = p.Ip, FontFamily = new FontFamily("Consolas"), FontSize = 13, Foreground = Theme.B("#8A90A0"), Margin = new Thickness(16, 2, 0, 0) });
            g.Children.Add(left);
            string ip = p.Ip;
            g.Background = Brushes.Transparent;
            g.MouseLeftButtonUp += (s, e) => { LanCopyText(ip); LanStatus("Адрес " + ip + " скопирован", "#3DDC97"); };

            var body = new StackPanel();
            body.Children.Add(g);
            body.Children.Add(LanCheckView(p));
            b.Child = body;
            b.Cursor = null;
            g.Cursor = Cursors.Hand;
            b.MouseEnter += (s, e) => b.BorderBrush = (Brush)Win.FindResource("Accent");
            b.MouseLeave += (s, e) => b.BorderBrush = Theme.B("#262A33");
            return b;
        }

        UIElement LanCheckView(LanPeer p)
        {
            LanCheck c; lanChecks.TryGetValue(p.Ip, out c);
            var box = new StackPanel { Margin = new Thickness(16, 8, 0, 0) };
            if (c != null && (c.Running || c.Line != ""))
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                if (c.Running) line.Children.Add(new TextBlock { Text = Translate.T(c.Stage), FontSize = 12, Foreground = Theme.B("#FFB547") });
                else
                {
                    line.Children.Add(new System.Windows.Shapes.Rectangle { Width = 6, Height = 6, Fill = Theme.B(c.Color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
                    line.Children.Add(new TextBlock { Text = Translate.T(c.Verdict), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.B(c.Color) });
                }
                box.Children.Add(line);
                if (!c.Running) box.Children.Add(new TextBlock { Text = c.Line, FontSize = 12, Foreground = Theme.B("#8A90A0"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(13, 2, 0, 0) });
            }
            if (c == null || !c.Running)
            {
                var link = new TextBlock { Text = Translate.T(c != null && c.Line != "" ? "Проверить ещё раз" : "Проверить связь"), FontSize = 12, Foreground = Theme.B("#7FE8B8"), Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, c != null && c.Line != "" ? 6 : 0, 0, 0) };
                link.TextDecorations = TextDecorations.Underline;
                string ip = p.Ip, nick = p.Nick;
                link.MouseLeftButtonUp += (s, e) => { e.Handled = true; LanRunCheck(ip, nick); };
                box.Children.Add(link);
            }
            return box;
        }

        void LanRunCheck(string ip, string nick)
        {
            LanCheck c;
            if (lanChecks.TryGetValue(ip, out c) && c.Running) return;
            c = new LanCheck { Running = true, Stage = "Проверяю пинг…" };
            lanChecks[ip] = c;
            LanRefreshPeers();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                // 1) пинг: 20 раз, задержка, разброс, потери
                var times = new List<long>(); int sent = 0;
                using (var ping = new System.Net.NetworkInformation.Ping())
                    for (int i = 0; i < 20 && lanCode != null; i++)
                    {
                        sent++;
                        try { var r = ping.Send(ip, 1500); if (r.Status == System.Net.NetworkInformation.IPStatus.Success) times.Add(r.RoundtripTime); } catch { }
                        Thread.Sleep(120);
                    }
                double loss = sent == 0 ? 100 : 100.0 * (sent - times.Count) / sent;
                double avg = times.Count > 0 ? times.Average() : 0;
                double jitter = 0;
                for (int i = 1; i < times.Count; i++) jitter += Math.Abs(times[i] - times[i - 1]);
                if (times.Count > 1) jitter /= times.Count - 1;

                // 2) скорость в обе стороны
                Win.Dispatcher.BeginInvoke(new Action(() => { c.Stage = "Проверяю скорость…"; LanRefreshPeers(); }));
                double down = lanCode != null ? LanSpeed.Measure(ip, true) : -1;
                double up = lanCode != null && down >= 0 ? LanSpeed.Measure(ip, false) : -1;

                // 3) вывод
                var parts = new List<string>();
                if (times.Count > 0) parts.Add(Translate.T("пинг") + " " + Math.Round(avg) + " " + Translate.T("мс") + " (±" + Math.Round(jitter) + ")");
                parts.Add(Translate.T("потери") + " " + Math.Round(loss) + "%");
                if (down >= 0) parts.Add("↓ " + Mbit(down) + "  ↑ " + Mbit(up));
                else parts.Add(Translate.T("скорость не проверить — у друга старая версия Zapret"));
                string verdict, color;
                if (times.Count == 0 && down < 0) { verdict = "Друг не отвечает — проверь, что у него открыт Zapret"; color = "#FF5E62"; }
                else if (times.Count == 0) { verdict = "Пинг не проходит — брандмауэр друга, но связь есть"; color = "#FFB547"; }
                else if (loss >= 5) { verdict = "Нестабильно: теряются пакеты — в играх будут рывки"; color = "#FF5E62"; }
                else if (avg >= 150) { verdict = "Высокий пинг — в быстрых играх будет тяжело"; color = "#FFB547"; }
                else if (jitter >= 30) { verdict = "Пинг скачет — возможны рывки"; color = "#FFB547"; }
                else if (down >= 0 && Math.Min(down, up) < 2) { verdict = "Пинг хороший, но скорость низкая — большие карты и моды будут грузиться долго"; color = "#FFB547"; }
                else { verdict = "Связь хорошая для игр"; color = "#3DDC97"; }
                Win.Dispatcher.BeginInvoke(new Action(() =>
                {
                    c.Running = false; c.Verdict = verdict; c.Color = color; c.Line = string.Join(" · ", parts);
                    LanRefreshPeers();
                    if (El("LanOverlay").Visibility != Visibility.Visible) Toast((nick == "" ? ip : nick) + ": " + Translate.T(verdict), color == "#FF5E62");
                }));
            });
        }

        static string Mbit(double v) { return v < 0 ? "—" : (v >= 10 ? Math.Round(v).ToString(CultureInfo.InvariantCulture) : v.ToString("0.0", CultureInfo.InvariantCulture)) + " " + Translate.T("Мбит/с"); }

        // перерисовать список по последним данным, не дожидаясь опроса
        void LanRefreshPeers() { if (lanLastList != null && lanCode != null) LanRender(lanLastList); }

        TextBlock LanNote(string t) { return new TextBlock { Text = Translate.T(t), FontSize = 12.5, Foreground = Theme.B("#8A90A0"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 8) }; }

        void LanShowRoom(bool room, bool animate)
        {
            var show = El(room ? "LanRoom" : "LanIdle"); var hide = El(room ? "LanIdle" : "LanRoom");
            hide.Visibility = Visibility.Collapsed;
            show.Visibility = Visibility.Visible;
            E<TextBox>("LanNick").IsEnabled = !room;
            LanUpdateDot(room);
            if (!animate || Anim.Eco) return;
            var tt = new TranslateTransform(); show.RenderTransform = tt;
            Anim.D(show, Anim.OP, 1, 220, 0);
            Anim.D(tt, Anim.TY, 0, 300, 10, ease: Anim.Ease());
        }

        void LanUpdateDot(bool on) { El("LanDot").Visibility = on && lanCode != null ? Visibility.Visible : Visibility.Collapsed; }

        void LanStatus(string text, string color)
        {
            E<TextBlock>("LanStatus").Text = Translate.T(text);
            E<System.Windows.Shapes.Rectangle>("LanStatusDot").Fill = Theme.B(color);
        }

        static void LanCopyText(string s)
        {
            for (int i = 0; i < 5; i++) { try { Clipboard.SetText(s ?? ""); return; } catch { Thread.Sleep(40); } }
        }
    }
}
