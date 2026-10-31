// Zapret (C#) — тест стратегий: выбор групп и стратегий, проверка в фоне, результаты
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ZapretCS
{
    class TestTarget { public string Name, Url, Ping, Group; }
    class SiteResult { public string Name, Group, Ping = "n/a", Diag = ""; public string[] Tokens; public bool IsUrl; }
    class StratResult { public string Name, File; public bool Failed; public int OK, Total, PingOK, PingTotal; public List<SiteResult> Details = new List<SiteResult>(); }
    class TestSync
    {
        public volatile int Done; public volatile string Current = "", Phase = ""; public volatile bool Finished, Cancel; public string Error;
        public readonly List<StratResult> Results = new List<StratResult>();
    }

    partial class MainUi
    {
        bool testing, testWasOn;
        TestSync testSync;
        int testShown, testTotal;
        DateTime testBegan;
        DispatcherTimer testTimer;

        List<TestTarget> TestTargets()
        {
            var list = new List<TestTarget>();
            var file = System.IO.Path.Combine(ZapretCore.Utils, "targets.txt");
            if (!File.Exists(file)) return list;
            string grp = "Другое";
            foreach (var l in File.ReadAllLines(file, Encoding.UTF8))
            {
                var g = Regex.Match(l, @"^\s*###\s*(.+?)\s*$");
                if (g.Success) { grp = g.Groups[1].Value; continue; }
                var m = Regex.Match(l, "^\\s*(\\w+)\\s*=\\s*\"(.+)\"\\s*$");
                if (!m.Success) continue;
                string v = m.Groups[2].Value;
                if (v.StartsWith("PING:")) list.Add(new TestTarget { Name = m.Groups[1].Value, Ping = Regex.Replace(v, @"^PING:\s*", ""), Group = grp });
                else list.Add(new TestTarget { Name = m.Groups[1].Value, Url = v, Ping = Regex.Replace(Regex.Replace(v, "^https?://", ""), "/.*$", ""), Group = grp });
            }
            return list;
        }
        static string ShortName(string n) { var m = Regex.Match(n, @"^general\s*\((.+)\)$"); return m.Success ? m.Groups[1].Value : n; }

        void WireTest()
        {
            Btn("TestBtn").Click += (s, e) => Safe(OpenTest);
            Btn("TestClose").Click += (s, e) => Safe(() => CloseOverlay("Test"));
            El("TestDim").MouseLeftButtonUp += (s, e) => { if (!testing) CloseOverlay("Test"); };
            Btn("GroupAll").Click += (s, e) => ToggleAllRows(E<Panel>("GroupChips"));
            Btn("StratAll").Click += (s, e) => ToggleAllRows(E<Panel>("StratChips"));
            Btn("RunBtn").Click += (s, e) => Safe(() =>
            {
                var rb = Btn("RunBtn");
                if (testing) { testSync.Cancel = true; rb.IsEnabled = false; rb.Content = Translate.T("Останавливаю…"); }
                else if ((rb.Content as string) == Translate.T("Новый тест"))
                {
                    rb.Content = Translate.T("Запустить тест");
                    El("ResultsHead").Visibility = Visibility.Collapsed;
                    E<Panel>("TestResults").Children.Clear();
                    El("TestSetup").Visibility = Visibility.Visible; Anim.D(A("TestSetup"), Anim.OP, 1, 250, 0);
                    ((ScaleTransform)Win.FindName("TestBarScale")).ScaleX = 0;
                    FillTestSetup();
                }
                else StartTest();
            });
            testTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            testTimer.Tick += (s, e) => { try { TestTick(); } catch (Exception ex) { Log.Write("test: " + ex); } };
        }

        void OpenTest()
        {
            if (!testing) FillTestSetup();
            OpenOverlay("Test");
            Translate.Tree(El("TestOverlay"));
        }

        ToggleButton PickRow(string text, string tag, bool check, string color, string right, string badge)
        {
            var c = new ToggleButton { Style = (Style)Win.FindResource("PickRow"), Tag = tag, IsChecked = check };
            var dp = new DockPanel { LastChildFill = true };
            if (!string.IsNullOrEmpty(right)) { var r = Txt(right, 12, "#5A6070"); r.Margin = new Thickness(8, 0, 2, 0); DockPanel.SetDock(r, Dock.Right); dp.Children.Add(r); }
            if (!string.IsNullOrEmpty(badge))
            {
                var bb = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(7, 1, 7, 1), Margin = new Thickness(8, 0, 0, 0), Background = Theme.B("#1B3329"), VerticalAlignment = VerticalAlignment.Center };
                bb.Child = Txt(badge, 10.5, "#7FE8B8"); DockPanel.SetDock(bb, Dock.Right); dp.Children.Add(bb);
            }
            if (color != null) dp.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = Theme.B(color), Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center });
            var tb = new TextBlock { Text = Translate.T(text), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            dp.Children.Add(tb);
            c.Content = dp;
            c.Click += (s, e) => UpdateTestHint();
            return c;
        }
        void FillTestSetup()
        {
            var gp = E<Panel>("GroupChips"); var sp = E<Panel>("StratChips");
            gp.Children.Clear(); sp.Children.Clear();
            var tg = TestTargets();
            foreach (var g in tg.Select(t => t.Group).Distinct())
                gp.Children.Add(PickRow(g, g, true, GroupColor(g, false), tg.Count(t => t.Group == g).ToString(), null));
            var cur = E<ComboBox>("StrategyBox").SelectedItem as ComboBoxItem;
            foreach (ComboBoxItem it in E<ComboBox>("StrategyBox").Items)
                sp.Children.Add(PickRow(ShortName((string)it.Content), (string)it.Tag, true, null, null, it == cur ? "сейчас" : null));
            UpdateTestHint();
        }
        void ToggleAllRows(Panel p)
        {
            bool v = p.Children.OfType<ToggleButton>().Any(c => c.IsChecked != true);
            foreach (ToggleButton c in p.Children) c.IsChecked = v;
            UpdateTestHint();
        }
        static List<string> Picked(Panel p) { return p.Children.OfType<ToggleButton>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList(); }
        void UpdateTestHint()
        {
            foreach (var t in new[] { new[] { "GroupChips", "GroupCount", "GroupAll" }, new[] { "StratChips", "StratCount", "StratAll" } })
            {
                var p = E<Panel>(t[0]); int all = p.Children.Count, on = Picked(p).Count;
                Tb(t[1]).Text = on + " / " + all;
                Btn(t[2]).Content = Translate.T(on < all ? "Выбрать все" : "Снять все");
            }
            var groups = Picked(E<Panel>("GroupChips")); var strats = Picked(E<Panel>("StratChips"));
            int sites = TestTargets().Count(t => groups.Contains(t.Group));
            int sec = (int)(strats.Count * (5 + Math.Ceiling(sites / 30.0)) + 3);
            Tb("TestStatus").Text = Translate.T(strats.Count == 0 || sites == 0 ? "Выбери хотя бы одну группу и одну стратегию"
                : strats.Count + " стратегий × " + sites + " сайтов · примерно " + Math.Max(1, (int)Math.Round(sec / 60.0)) + " мин");
            Btn("RunBtn").IsEnabled = strats.Count > 0 && sites > 0;
        }

        void StartTest()
        {
            if (ZapretCore.ServiceState() == "Running") { Toast("zapret работает как служба. Удали службу в «Сервис и диагностика», чтобы тестировать.", true); return; }
            var groups = Picked(E<Panel>("GroupChips"));
            var targets = TestTargets().Where(t => groups.Contains(t.Group)).ToList();
            var strategies = new List<string[]>();
            foreach (var f in Picked(E<Panel>("StratChips")))
            {
                string a = ZapretCore.WinwsArgs(System.IO.Path.Combine(ZapretCore.Root, f));
                if (a != null) strategies.Add(new[] { System.IO.Path.GetFileNameWithoutExtension(f), f, a });
            }
            if (strategies.Count == 0 || targets.Count == 0) return;

            testWasOn = State == "on";
            testing = true; testShown = 0; testTotal = strategies.Count; testBegan = DateTime.Now;
            Btn("PowerBtn").IsEnabled = false; E<ComboBox>("StrategyBox").IsEnabled = false;
            SetVisual("off", "Идёт тест стратегий…");
            testSync = new TestSync();
            var sync = testSync; string bin = System.IO.Path.Combine(ZapretCore.Root, "bin");
            new Thread(() => TestWorker(sync, strategies, targets, bin, 3)) { IsBackground = true, Name = "test" }.Start();

            E<Panel>("TestResults").Children.Clear();
            Anim.D(A("TestSetup"), Anim.OP, 0, 200);
            El("TestSetup").Visibility = Visibility.Collapsed;
            El("ResultsHead").Visibility = Visibility.Visible;
            Btn("RunBtn").Content = Translate.T("Остановить");
            ((ScaleTransform)Win.FindName("TestBarScale")).ScaleX = 0;
            Anim.D(A("TestShimmer"), Anim.OP, 1, 300);
            Anim.D(A("TestShimmerT"), Anim.TX, 700, 1400, -120, forever: true, noEase: true);
            testTimer.Start();
        }

        // ---- фоновая проверка (отдельный поток) ----
        static void KillWinws()
        {
            foreach (var p in Process.GetProcessesByName("winws")) { try { p.Kill(); } catch { } p.Dispose(); }
            for (int i = 0; i < 20 && Process.GetProcessesByName("winws").Length > 0; i++) Thread.Sleep(100);
            Thread.Sleep(200);
        }
        static readonly string[] Kinds = { "HTTP", "TLS1.2", "TLS1.3" };
        static readonly string[] KindArgs = { "--http1.1", "--tlsv1.2 --tls-max 1.2", "--tlsv1.3 --tls-max 1.3" };
        static string CurlExe()
        {
            var p = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");
            return File.Exists(p) ? p : "curl.exe";
        }
        // одна проверка = один запуск curl; возвращает метку вида "TLS1.2:OK" и текст ошибки
        static string[] Curl(string url, int kind, int timeout)
        {
            string label = Kinds[kind];
            try
            {
                var psi = new ProcessStartInfo(CurlExe(), "-I -s -m " + timeout + " --connect-timeout " + Math.Min(2, timeout) + " -o NUL -w %{http_code} --show-error " + KindArgs[kind] + " \"" + url + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    var errTask = p.StandardError.ReadToEndAsync(); p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit((timeout + 4) * 1000)) { try { p.Kill(); } catch { } return new[] { label + ":ERR", label + " timeout" }; }
                    string err = errTask.Result.Trim(); int code = p.ExitCode;
                    if (Regex.IsMatch(err, "Could not resolve host|certificate|self[- ]?signed")) return new[] { label + ":SSL", label + " exit=" + code + " " + err };
                    if (code == 35 || Regex.IsMatch(err, "not supported|unsupported|Unknown option|schannel")) return new[] { label + ":UNSUP", "" };
                    if (code == 0) return new[] { label + ":OK", "" };
                    return new[] { label + ":ERR", label + " exit=" + code + " " + err };
                }
            }
            catch (Exception ex) { return new[] { label + ":ERR", label + " exception: " + ex.Message }; }
        }
        static string PingHost(string host)
        {
            try { using (var pg = new Ping()) { var r = pg.Send(host, 1000); return r.Status == IPStatus.Success ? r.RoundtripTime + " ms" : "Timeout"; } }
            catch { return "Timeout"; }
        }
        static List<SiteResult> RunChecks(List<TestTarget> targets, int timeout)
        {
            // все проверки идут параллельно (до 32 одновременно)
            var res = targets.Select(t => new SiteResult { Name = t.Name, Group = t.Group, Tokens = new string[3], IsUrl = t.Url != null }).ToList();
            var jobs = new List<Action>();
            for (int i = 0; i < targets.Count; i++)
            {
                var t = targets[i]; var r = res[i];
                if (t.Url != null) for (int k = 0; k < 3; k++) { int kk = k; jobs.Add(() => { var c = Curl(t.Url, kk, timeout); r.Tokens[kk] = c[0]; if (c[1].Length > 0) lock (r) r.Diag += (r.Diag.Length > 0 ? " | " : "") + c[1]; }); }
                if (t.Ping != null) jobs.Add(() => r.Ping = PingHost(t.Ping));
            }
            Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = 32 }, j => j());
            foreach (var r in res) r.Tokens = r.Tokens.Where(x => x != null).ToArray();
            return res;
        }
        static void TestWorker(TestSync sync, List<string[]> strategies, List<TestTarget> targets, string bin, int timeout)
        {
            ThreadPool.SetMinThreads(40, 40);
            bool first = true;
            try
            {
                foreach (var s in strategies)
                {
                    if (sync.Cancel) break;
                    sync.Current = s[0]; sync.Phase = "start";
                    KillWinws();
                    Process.Start(new ProcessStartInfo(System.IO.Path.Combine(bin, "winws.exe"), s[2]) { WorkingDirectory = bin, UseShellExecute = false, CreateNoWindow = true });
                    bool up = false;
                    for (int i = 0; i < 50; i++) { if (Process.GetProcessesByName("winws").Length > 0) { up = true; break; } Thread.Sleep(100); }
                    // первой стратегии нужно больше времени: драйвер WinDivert загружается
                    if (up) { Thread.Sleep(first ? 2500 : 700); up = Process.GetProcessesByName("winws").Length > 0; }
                    if (!up)
                    {
                        lock (sync.Results) sync.Results.Add(new StratResult { Name = s[0], File = s[1], Failed = true });
                        sync.Done++; first = false; continue;
                    }
                    sync.Phase = "test";
                    List<SiteResult> res = null; int ok = 0, total = 0, pok = 0, ptot = 0;
                    for (int attempt = 1; attempt <= 2; attempt++)
                    {
                        res = RunChecks(targets, timeout);
                        ok = 0; total = 0; pok = 0; ptot = 0;
                        foreach (var r in res)
                        {
                            foreach (var tk in r.Tokens) { if (!tk.Contains("UNSUP")) total++; if (tk.EndsWith(":OK")) ok++; }
                            if (r.Ping != "n/a") { ptot++; if (r.Ping != "Timeout") pok++; }
                        }
                        // перепроверка только у первой стратегии (там 0 бывает из-за загрузки драйвера)
                        if (ok > 0 || total == 0 || !first || sync.Cancel) break;
                        sync.Phase = "retry"; Thread.Sleep(2000);
                    }
                    first = false;
                    lock (sync.Results) sync.Results.Add(new StratResult { Name = s[0], File = s[1], OK = ok, Total = total, PingOK = pok, PingTotal = ptot, Details = res });
                    sync.Done++;
                }
            }
            catch (Exception ex) { sync.Error = ex.Message; }
            finally { KillWinws(); sync.Finished = true; }
        }

        void TestTick()
        {
            var sync = testSync; if (sync == null) return;
            int done = sync.Done;
            double frac = testTotal > 0 ? (done + (sync.Phase == "test" ? 0.5 : 0.1)) / testTotal : 0;
            Anim.D(A("TestBarScale"), Anim.SX, Math.Min(1, frac), 450);
            string eta = "";
            if (done > 0)
            {
                double per = (DateTime.Now - testBegan).TotalSeconds / done; int left = (int)(per * (testTotal - done));
                eta = left >= 60 ? " · осталось ~" + Math.Round(left / 60.0) + " мин" : " · осталось ~" + left + " с";
            }
            string phase = sync.Phase == "test" ? "проверяю сайты" : sync.Phase == "retry" ? "перепроверяю" : "запускаю";
            Tb("TestStatus").Text = Translate.T("Стратегия " + Math.Min(done + 1, testTotal) + " из " + testTotal + " · " + sync.Current + " · " + phase + eta);
            lock (sync.Results)
                while (testShown < sync.Results.Count) { E<Panel>("TestResults").Children.Insert(0, ResultRow(sync.Results[testShown], false, 0)); testShown++; }
            if (sync.Finished) FinishTest(sync.Cancel);
        }

        FrameworkElement ResultRow(StratResult r, bool best, int rank)
        {
            var wrap = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            var b = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10, 14, 10), Cursor = Cursors.Hand, Background = Theme.B(best ? "#18302A" : "#1E2129"), BorderThickness = new Thickness(1), BorderBrush = Theme.B(best ? "#3DDC97" : "#262A33") };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var nameSp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (rank > 0) { var rk = Txt(rank.ToString(), 12, "#5A6070"); rk.Width = 22; nameSp.Children.Add(rk); }
            var nm = Txt(r.Name, 13.5, "#E6E8EE"); nm.FontWeight = FontWeights.SemiBold; nameSp.Children.Add(nm);
            if (best)
            {
                var bd = new Border { Background = Theme.B("#3DDC97"), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(10, 0, 0, 0) };
                var bt = Txt("★ лучшая", 11, "#0F1115"); bt.FontWeight = FontWeights.SemiBold; bd.Child = bt; nameSp.Children.Add(bd);
            }
            g.Children.Add(nameSp);
            double ratio = r.Total > 0 ? (double)r.OK / r.Total : 0;
            string col = r.Failed ? "#FF5E62" : ratio >= 0.85 ? "#3DDC97" : ratio >= 0.5 ? "#FFB86B" : "#FF5E62";
            var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Theme.B("#2A2E38"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
            var sc = new ScaleTransform(0, 1);
            track.Child = new Border { CornerRadius = new CornerRadius(3), Background = Theme.B(col), RenderTransformOrigin = new Point(0, 0.5), RenderTransform = sc };
            Grid.SetColumn(track, 1); g.Children.Add(track);
            Anim.D(sc, Anim.SX, ratio, 700, 0, begin: 150);
            var txt = Txt(r.Failed ? "не запустилась" : r.OK + "/" + r.Total + " · " + (int)(ratio * 100) + "%" + (r.PingTotal > 0 ? " · ping " + r.PingOK + "/" + r.PingTotal : ""), 12, col);
            txt.Width = 175; txt.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(txt, 2); g.Children.Add(txt);
            if (best)
            {
                var ap = new Button { Style = (Style)Win.FindResource("AccentBtn"), Content = Translate.T("Включить"), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(12, 0, 0, 0) };
                string file = r.File;
                ap.Click += (s, e) => Safe(() => { CloseOverlay("Test"); SelectByTag(E<ComboBox>("StrategyBox"), file); if (State != "on") StartZapret(); });
                Grid.SetColumn(ap, 3); g.Children.Add(ap);
            }
            b.Child = g;
            // подробности по сайтам
            var det = new WrapPanel { Margin = new Thickness(14, 6, 0, 4), Visibility = Visibility.Collapsed };
            foreach (var d in r.Details)
            {
                bool okAll = true, any = false;
                foreach (var tk in d.Tokens) if (!tk.Contains("UNSUP")) { any = true; if (!tk.EndsWith(":OK")) okAll = false; }
                if (!d.IsUrl) okAll = d.Ping != "Timeout";
                var pill = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 6), Background = Theme.B(okAll ? "#18302A" : "#3A2328") };
                string toks = string.Join("  ", d.Tokens.Select(tk => Regex.Replace(Regex.Replace(Regex.Replace(tk, ":OK$", " ✓"), ":(ERR|SSL)$", " ✕"), ":UNSUP$", " –")));
                pill.Child = Txt(d.Name + "  " + toks + (d.Ping != null && d.Ping != "n/a" ? "  · " + d.Ping : ""), 11.5, okAll ? "#7FE8B8" : "#FF8C99");
                det.Children.Add(pill);
            }
            b.MouseLeftButtonUp += (s, e) => { if (det.Visibility == Visibility.Visible) det.Visibility = Visibility.Collapsed; else { det.Visibility = Visibility.Visible; Anim.D(det, Anim.OP, 1, 250, 0); } };
            wrap.Children.Add(b); wrap.Children.Add(det);
            wrap.Opacity = 0; var tt = new TranslateTransform(); wrap.RenderTransform = tt;
            Anim.D(wrap, Anim.OP, 1, 300, 0, begin: Math.Min(rank, 12) * 35);
            Anim.D(tt, Anim.TY, 0, 360, 14, begin: Math.Min(rank, 12) * 35);
            return wrap;
        }

        void FinishTest(bool cancelled)
        {
            testTimer.Stop(); testing = false;
            var sync = testSync;
            Anim.D(A("TestShimmer"), Anim.OP, 0, 300);
            Btn("PowerBtn").IsEnabled = true; E<ComboBox>("StrategyBox").IsEnabled = true;
            Btn("RunBtn").Content = Translate.T("Новый тест"); Btn("RunBtn").IsEnabled = true;
            List<StratResult> res;
            lock (sync.Results) res = sync.Results.OrderByDescending(r => r.Failed ? -1 : r.OK).ThenByDescending(r => r.PingOK).ToList();
            var panel = E<Panel>("TestResults"); panel.Children.Clear();
            int rank = 1;
            foreach (var r in res) { panel.Children.Add(ResultRow(r, rank == 1 && !r.Failed && r.OK > 0, rank)); rank++; }
            Anim.D(A("TestBarScale"), Anim.SX, 1, 400);
            var el = DateTime.Now - testBegan;
            string bestName = res.Count > 0 ? res[0].Name : "—";
            Tb("TestStatus").Text = Translate.T(cancelled ? "Тест остановлен · проверено " + res.Count + " из " + testTotal : "Готово за " + (int)el.TotalMinutes + " мин " + el.Seconds + " с · лучшая: " + bestName);
            if (sync.Error != null) Toast("Ошибка теста: " + sync.Error, true);
            // ошибки curl — в журнал (первые 6)
            try
            {
                var dl = res.SelectMany(r => r.Details.Where(d => d.Diag.Length > 0).Select(d => r.Name + " / " + d.Name + ": " + d.Diag)).Take(6).ToList();
                if (dl.Count > 0) Log.Write("TEST DIAG:\r\n  " + string.Join("\r\n  ", dl));
            }
            catch { }
            // сохранить результаты как обычный тест
            try
            {
                var dir = System.IO.Path.Combine(ZapretCore.Utils, "test results"); Directory.CreateDirectory(dir);
                var lines = new List<string>();
                foreach (var r in res)
                {
                    lines.Add("Config: " + r.File + " (Type: standard)  OK " + r.OK + "/" + r.Total);
                    foreach (var d in r.Details) lines.Add("  " + d.Name + " : " + string.Join(" ", d.Tokens) + " | Ping: " + d.Ping);
                }
                if (res.Count > 0) { lines.Add(""); lines.Add("Best config: " + res[0].File); }
                File.WriteAllLines(System.IO.Path.Combine(dir, "test_results_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt"), lines, new UTF8Encoding(false));
            }
            catch { }
            // вернуть zapret как был до теста
            State = "x";
            if (testWasOn) { State = "off"; StartZapret(); } else { visAt = 0; Mon.Wake(); }
            if (!cancelled) Toast("Тест завершён. Лучшая стратегия: " + bestName);
        }
    }
}
