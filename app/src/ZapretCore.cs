// Zapret (C#) — работа с самим zapret: поиск папки, стратегии, запуск winws, фоновый монитор
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace ZapretCS
{
    class ZapretFolder
    {
        public string Path, Name;
        public Version Ver;
        public DateTime Time;
    }

    static class ZapretCore
    {
        public static string Root, Lists, Utils, UserListFile, GameFile, LocalVersion;
        public const string Placeholder = "domain.example.abc";

        // ---- поиск папок zapret (как Find-ZapretFolders) ----
        public static List<ZapretFolder> FindFolders(string guiDir)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string desk = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var dirs = new List<string> {
                guiDir, Path.GetDirectoryName(guiDir.TrimEnd('\\')), Path.Combine(desk, "Zapret"), desk,
                Path.Combine(home, "Desktop"), Path.Combine(home, "OneDrive\\Desktop"), Path.Combine(home, "Downloads"), "C:\\"
            };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var found = new List<ZapretFolder>();
            foreach (var d0 in dirs)
            {
                if (string.IsNullOrEmpty(d0) || !Directory.Exists(d0)) continue;
                string d = Path.GetFullPath(d0);
                if (!seen.Add(d)) continue;
                string[] subs;
                try { subs = Directory.GetDirectories(d, "zapret*"); } catch { continue; }
                foreach (var s in subs)
                {
                    var cands = new List<string> { s };
                    try { cands.AddRange(Directory.GetDirectories(s, "zapret*")); } catch { }
                    foreach (var c in cands)
                    {
                        if (!File.Exists(Path.Combine(c, "bin\\winws.exe"))) continue;
                        if (found.Any(f => string.Equals(f.Path, c, StringComparison.OrdinalIgnoreCase))) continue;
                        var zf = new ZapretFolder { Path = c, Name = Path.GetFileName(c), Ver = new Version(0, 0) };
                        var m = Regex.Match(zf.Name, @"(\d+(\.\d+)+)");
                        if (m.Success) { Version v; if (Version.TryParse(m.Groups[1].Value, out v)) zf.Ver = v; }
                        try { zf.Time = Directory.GetCreationTime(c); } catch { }
                        found.Add(zf);
                    }
                }
            }
            return found.OrderBy(f => f.Ver).ThenBy(f => f.Time).ToList();
        }

        public static void UseFolder(string root)
        {
            Root = root;
            Lists = Path.Combine(root, "lists");
            Utils = Path.Combine(root, "utils");
            UserListFile = Path.Combine(Lists, "list-general-user.txt");
            GameFile = Path.Combine(Utils, "game_filter.enabled");
            LocalVersion = null;
            try
            {
                foreach (var l in File.ReadAllLines(Path.Combine(root, "service.bat")))
                {
                    var m = Regex.Match(l, "set \"LOCAL_VERSION=([^\"]+)\"");
                    if (m.Success) { LocalVersion = m.Groups[1].Value; break; }
                }
            }
            catch { }
        }

        // ---- стратегии: все .bat, кроме service*, в «человеческом» порядке ----
        public static List<string> Strategies()
        {
            var r = new List<string>();
            try
            {
                foreach (var f in Directory.GetFiles(Root, "*.bat"))
                {
                    var n = Path.GetFileName(f);
                    if (n.StartsWith("service", StringComparison.OrdinalIgnoreCase)) continue;
                    r.Add(n);
                }
            }
            catch { }
            return r.OrderBy(n => Regex.Replace(n, @"\d+", m => m.Value.PadLeft(8, '0')), StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ---- игровой фильтр ----
        public static void ReadGame(out string mode, out string tcp, out string udp)
        {
            mode = "disabled"; tcp = "1024-65535"; udp = "1024-65535";
            if (!File.Exists(GameFile)) return;
            foreach (var l in File.ReadAllLines(GameFile))
            {
                var m = Regex.Match(l, @"^\s*(\w+)\s*=\s*(.*?)\s*$");
                if (m.Success)
                {
                    string k = m.Groups[1].Value.ToLowerInvariant(), v = m.Groups[2].Value;
                    if (k == "mode") mode = v; else if (k == "tcp" && v.Length > 0) tcp = v; else if (k == "udp" && v.Length > 0) udp = v;
                }
                else
                {
                    var m2 = Regex.Match(l.Trim(), "^(all|tcp|udp)$");
                    if (m2.Success) mode = m2.Groups[1].Value;
                }
            }
            mode = mode.ToLowerInvariant();
        }

        // ---- командная строка winws из батника (как это сделал бы cmd) ----
        public static string WinwsArgs(string batPath)
        {
            string buf = null;
            foreach (var l in File.ReadAllLines(batPath))
            {
                if (buf == null)
                {
                    var m = Regex.Match(l, "start\\s+\"zapret[^\"]*\"\\s+/min\\s+\"%BIN%winws\\.exe\"(.*)$");
                    if (m.Success) buf = m.Groups[1].Value; else continue;
                }
                else buf += l;
                if (buf.TrimEnd().EndsWith("^")) { buf = buf.TrimEnd(); buf = buf.Substring(0, buf.Length - 1) + " "; }
                else break;
            }
            if (buf == null) return null;

            string mode, tcp, udp; ReadGame(out mode, out tcp, out udp);
            string t = "12", u = "12";
            if (mode == "all") { t = tcp; u = udp; } else if (mode == "tcp") t = tcp; else if (mode == "udp") u = udp;
            var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                { "BIN", Root + "\\bin\\" }, { "LISTS", Lists + "\\" }, { "GameFilterTCP", t }, { "GameFilterUDP", u }, { "GameFilter", mode == "udp" ? u : t }
            };
            string s = Regex.Replace(buf, @"%(\w+)%", m => vars.ContainsKey(m.Groups[1].Value) ? vars[m.Groups[1].Value] : m.Value);
            if (Regex.IsMatch(s, @"%\w+%")) return null;

            // каретки вне кавычек cmd убирает (^! -> !)
            var sb = new StringBuilder(); bool inQ = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') { inQ = !inQ; sb.Append(c); continue; }
                if (c == '^' && !inQ && i + 1 < s.Length) { i++; sb.Append(s[i]); continue; }
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        public static void PrepareUserLists()
        {
            try
            {
                var f = Path.Combine(Lists, "ipset-exclude-user.txt");
                if (!File.Exists(f)) File.WriteAllText(f, "203.0.113.113/32\r\n", Encoding.ASCII);
                if (!File.Exists(UserListFile)) File.WriteAllText(UserListFile, "# Never leave this file empty\r\n" + Placeholder + "\r\n", new UTF8Encoding(false));
                f = Path.Combine(Lists, "list-exclude-user.txt");
                if (!File.Exists(f)) File.WriteAllText(f, Placeholder + "\r\n", Encoding.ASCII);
            }
            catch (Exception ex) { Log.Write("lists: " + ex.Message); }
        }

        // Запуск стратегии. hidden = true — winws напрямую без окна, иначе через батник
        public static bool Start(string batName, bool showWindow, out bool hidden)
        {
            hidden = false;
            string bat = Path.Combine(Root, batName);
            string args = showWindow ? null : WinwsArgs(bat);
            if (args != null)
            {
                PrepareUserLists();
                try { Process.Start(new ProcessStartInfo("netsh.exe", "interface tcp set global timestamps=enabled") { CreateNoWindow = true, UseShellExecute = false }); } catch { }
                var psi = new ProcessStartInfo(Path.Combine(Root, "bin\\winws.exe"), args)
                { WorkingDirectory = Path.Combine(Root, "bin"), UseShellExecute = false, CreateNoWindow = true };
                Process.Start(psi);
                hidden = true;
            }
            else
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + bat + "\"\"") { WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true };
                psi.EnvironmentVariables["NO_UPDATE_CHECK"] = "1";
                Process.Start(psi);
            }
            return true;
        }

        public static string ServiceState()
        {
            try { using (var s = new ServiceController("zapret")) return s.Status.ToString(); }
            catch { return null; }
        }

        public static void Stop(out bool wasService)
        {
            wasService = false;
            try
            {
                using (var s = new ServiceController("zapret"))
                {
                    if (s.Status == ServiceControllerStatus.Running) { s.Stop(); wasService = true; }
                }
            }
            catch { }
            foreach (var p in Process.GetProcessesByName("winws")) { try { p.Kill(); } catch { } p.Dispose(); }
        }

        // Есть ли запущенный winws и с какой стратегией (по заголовку окна батника)
        public static bool RunningInfo(out string name)
        {
            name = null;
            var ps = Process.GetProcessesByName("winws");
            foreach (var p in ps)
            {
                try { var m = Regex.Match(p.MainWindowTitle ?? "", @"^zapret:\s*(.+)$"); if (m.Success) name = m.Groups[1].Value.Trim(); } catch { }
                p.Dispose();
            }
            return ps.Length > 0;
        }
    }

    // Фоновый монитор: все проверки в отдельном потоке, окно только читает готовые значения
    class Monitor
    {
        public volatile bool Winws, Tg, TgConn, Fast, Idle;
        public volatile string WinwsName, Svc, SvcName, TgCfgPath;
        int tgPort = 1443, tick;
        public long At;   // момент начала последнего замера (UtcNow.Ticks)
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        volatile bool stop;
        Thread th;

        public void Start()
        {
            th = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "monitor" };
            th.Start();
        }
        public void Wake() { wake.Set(); }
        public void Stop() { stop = true; wake.Set(); }

        public void SampleNow()
        {
            long t0 = DateTime.UtcNow.Ticks;
            string name; bool w = ZapretCore.RunningInfo(out name);
            string svc = ZapretCore.ServiceState(), svcName = null;
            if (svc == "Running")
            {
                try { using (var k = Registry.LocalMachine.OpenSubKey("System\\CurrentControlSet\\Services\\zapret")) if (k != null) svcName = k.GetValue("zapret-discord-youtube") as string; } catch { }
            }
            var tgp = Process.GetProcessesByName("TgWsProxy_windows"); bool tg = tgp.Length > 0; foreach (var p in tgp) p.Dispose();
            bool conn = false;
            if (tg)
            {
                if (tick++ % 5 == 0 && TgCfgPath != null)
                {
                    try { var m = Regex.Match(File.ReadAllText(TgCfgPath), "\"port\"\\s*:\\s*(\\d+)"); if (m.Success) tgPort = int.Parse(m.Groups[1].Value); } catch { }
                }
                // клиент Telegram держит соединение с прокси на этом компьютере
                bool hasClient = false;
                foreach (var nm in new[] { "Telegram", "AyuGram", "Kotatogram", "64Gram", "Unigram" })
                {
                    var q = Process.GetProcessesByName(nm); if (q.Length > 0) hasClient = true; foreach (var x in q) x.Dispose();
                    if (hasClient) break;
                }
                if (hasClient)
                    foreach (var cn in System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections())
                        if (cn.RemoteEndPoint.Port == tgPort && cn.State == System.Net.NetworkInformation.TcpState.Established && System.Net.IPAddress.IsLoopback(cn.RemoteEndPoint.Address)) { conn = true; break; }
            }
            Winws = w; WinwsName = name; Svc = svc; SvcName = svcName; Tg = tg; TgConn = conn;
            Interlocked.Exchange(ref At, t0);
        }

        void Loop()
        {
            while (!stop)
            {
                try { SampleNow(); } catch (Exception ex) { Log.Write("monitor: " + ex.Message); }
                // переключение — часто; окно открыто — раз в 3 с; свёрнуто или в фоне — раз в 10 с
                int wait = Fast ? 300 : (Idle ? 10000 : 3000);
                wake.WaitOne(wait);
            }
        }
    }
}
