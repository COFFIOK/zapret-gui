// Zapret (C#) — «Собрать свою стратегию» (бета): после теста программа берёт настройки из лучших стратегий,
// подбирает для каждой группы сайтов свои и собирает из них одну стратегию «general (Z).bat».
// Запускается только кнопкой после теста.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ZapretCS
{
    partial class MainUi
    {
        const int RepairBudget = 60;            // сколько изменённых вариантов пробуем при доводке (всего)
        const int GroupStale = 20;              // столько вариантов подряд без улучшения — группу больше не доводим
        const int Donors = 6;                   // сколько лучших стратегий из теста берём за основу
        const string ZFile = "general (Z).bat";
        bool repairMode;                        // окно теста сейчас в режиме сборки своей стратегии
        List<string> lastGroups;
        List<StratResult> lastResults;

        void EnterRepairMode()
        {
            repairMode = true;
            Tb("TestTitle").Text = Translate.T("Своя стратегия");
            Tb("TestSub").Text = Translate.T("Собираю стратегию под все твои сайты из лучших настроек");
        }

        void ExitRepairMode()
        {
            if (!repairMode) return;
            repairMode = false;
            Tb("TestTitle").Text = Translate.T("Тест стратегий");
            Tb("TestSub").Text = Translate.T("Проверяю, какая стратегия лучше открывает твои сайты");
        }

        void AddStrategyItem(string file)
        {
            var box = E<ComboBox>("StrategyBox");
            foreach (ComboBoxItem it in box.Items) if (string.Equals(it.Tag as string, file, StringComparison.OrdinalIgnoreCase)) return;
            bool was = loading; loading = true;
            box.Items.Add(new ComboBoxItem { Content = System.IO.Path.GetFileNameWithoutExtension(file), Tag = file });
            loading = was;
        }

        // домены для списка группы: свои группы — все сайты группы, встроенные — домены проверочных адресов
        static string ZListDomain(string host)
        {
            var p = host.Trim('.').Split('.');
            if (p.Length <= 2) return host;
            int take = p[p.Length - 1].Length == 2 && p[p.Length - 2].Length <= 3 && p.Length >= 3 ? 3 : 2;
            return string.Join(".", p.Skip(p.Length - take));
        }
        Dictionary<string, List<string>> GroupHosts(List<TestTarget> targets)
        {
            var res = new Dictionary<string, List<string>>();
            Groups ug = null; try { ug = ReadUserGroups(); } catch (Exception ex) { Log.Err("z hosts", ex); }
            foreach (var g in targets.Select(t => t.Group).Distinct())
            {
                var set = new List<string>();
                if (ug != null && ug.Has(g)) set.AddRange(ug[g].Where(x => !x.StartsWith("#")).Select(x => x.Trim()));
                foreach (var t in targets.Where(t => t.Group == g))
                {
                    Uri u; if (Uri.TryCreate(t.Url, UriKind.Absolute, out u)) set.Add(ZListDomain(u.Host));
                }
                res[g] = set.Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            return res;
        }

        void StartRepair()
        {
            if (ZapretCore.ServiceState() == "Running") { Toast("zapret работает как служба. Удали службу в «Сервис и диагностика», чтобы собрать стратегию.", true); return; }
            if (lastResults == null || lastGroups == null) return;
            var targets = TestTargets().Where(t => lastGroups.Contains(t.Group) && t.Url != null).ToList();
            var donors = lastResults.Where(r => !r.Failed && r.OK > 0 && !string.Equals(r.File, ZFile, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.OK).Take(Donors).Select(r => new[] { r.Name, r.File }).ToList();
            if (targets.Count == 0) { Toast("Нужны группы с сайтами — по пингу собрать стратегию нельзя", true); return; }
            if (donors.Count == 0) { Toast("Ни одна стратегия в тесте ничего не открыла — собирать не из чего", true); return; }
            var hosts = GroupHosts(targets);

            EnterRepairMode();
            El("FixBtn").Visibility = Visibility.Collapsed;
            testWasOn = State == "on";
            testing = true; testShown = 0; testTotal = 4 + donors.Count * 3 + RepairBudget; testBegan = DateTime.Now;
            Btn("PowerBtn").IsEnabled = false; E<ComboBox>("StrategyBox").IsEnabled = false;
            SetVisual("off", "Собираю свою стратегию…");
            testSync = new TestSync(); abortChecks = false;
            var sync = testSync; string root = ZapretCore.Root, bin = System.IO.Path.Combine(root, "bin");
            int total = testTotal;
            new Thread(() => BuildWorker(sync, donors, targets, hosts, root, bin, 3, total)) { IsBackground = true, Name = "zbuild" }.Start();

            E<Panel>("TestResults").Children.Clear();
            El("TestSetup").Visibility = Visibility.Collapsed;
            El("ResultsHead").Visibility = Visibility.Visible;
            Btn("RunBtn").Content = Translate.T("Остановить");
            ((ScaleTransform)Win.FindName("TestBarScale")).ScaleX = 0;
            Anim.D(A("TestShimmer"), Anim.OP, 1, 300);
            Anim.D(A("TestShimmerT"), Anim.TX, 700, 1400, -120, forever: true, noEase: true);
            testTimer.Start();
        }

        void FinishRepair(bool cancelled)
        {
            testTimer.Stop(); testing = false;
            var sync = testSync;
            Anim.D(A("TestShimmer"), Anim.OP, 0, 300);
            Btn("PowerBtn").IsEnabled = true; E<ComboBox>("StrategyBox").IsEnabled = true;
            Btn("RunBtn").Content = Translate.T("Новый тест"); Btn("RunBtn").IsEnabled = true;
            Anim.D(A("TestBarScale"), Anim.SX, 1, 400);
            List<StratResult> res; lock (sync.Results) res = sync.Results.ToList();
            var z = res.FirstOrDefault(r => r.Apply);
            Tb("TestStatus").Text = Translate.T(cancelled ? "Сборка остановлена" : sync.Summary != null ? sync.Summary
                : z != null ? "Готово · стратегия Z открывает " + z.OK + " из " + z.Total : "Собрать лучше готовых не получилось");
            if (sync.Error != null) Toast("Ошибка сборки: " + sync.Error, true);
            try { if (sync.RepairLog.Count > 0) Log.Write("Z BUILD:\r\n  " + string.Join("\r\n  ", sync.RepairLog)); } catch (Exception logEx) { Log.Err("z log", logEx); }
            State = "x";
            if (testWasOn) { State = "off"; StartZapret(); } else { visAt = 0; Mon.Wake(); }
            if (!cancelled) Toast(z != null ? "Стратегия Z готова — нажми «Включить»" : sync.Summary ?? "Лучшая готовая стратегия и так открывает больше — оставь её");
        }

        class Cand { public string Raw, What; public double Order; }

        static bool StartWinws(string bin, string args, bool first)
        {
            KillWinws();
            Process.Start(new ProcessStartInfo(System.IO.Path.Combine(bin, "winws.exe"), args) { WorkingDirectory = bin, UseShellExecute = false, CreateNoWindow = true });
            bool up = false;
            for (int i = 0; i < 50; i++) { if (Process.GetProcessesByName("winws").Length > 0) { up = true; break; } Thread.Sleep(100); }
            if (up) { Thread.Sleep(first ? 2500 : 700); up = Process.GetProcessesByName("winws").Length > 0; }
            return up;
        }

        const int Canaries = 6;

        static int Score(List<SiteResult> det, out int total)
        {
            int ok = 0; total = 0;
            foreach (var r in det) foreach (var tk in r.Tokens) { if (!tk.Contains("UNSUP")) total++; if (tk.EndsWith(":OK")) ok++; }
            return ok;
        }
        static bool Unreachable(SiteResult d)
        {
            if (d.Tokens.Any(t => t.EndsWith(":OK"))) return false;
            // не находится адрес, нет соединения, или у самого сайта неверный сертификат — это не обходится стратегией
            return Regex.IsMatch(d.Diag ?? "", "Could not resolve host|Connection timed out after|Failed to connect|SEC_E_|certificate");
        }
        // точечные проверки (сайт, вид проверки) на уже запущенном winws; null — проверку остановили
        static bool[] Probe(List<TestTarget> fx, List<int[]> probes, int timeout)
        {
            var res = new bool[probes.Count];
            System.Threading.Tasks.Parallel.For(0, probes.Count, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 48 }, i =>
            {
                if (abortChecks) return;
                res[i] = Curl(fx[probes[i][0]].Url, probes[i][1], timeout)[0].EndsWith(":OK");
            });
            return abortChecks ? null : res;
        }
        static List<SiteResult> FullCheck(List<TestTarget> fx, int timeout, bool firstRun)
        {
            List<SiteResult> det = null;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                det = RunChecks(fx, timeout);
                if (abortChecks) return null;
                int t; if (Score(det, out t) > 0 || !firstRun) break;
                Thread.Sleep(2000);    // первый запуск: драйвер WinDivert мог ещё не загрузиться
            }
            return det;
        }

        static void Add(TestSync sync, StratResult r) { lock (sync.Results) sync.Results.Add(r); }

        // ---- варианты: одна правка в одном профиле (между --new), который обходит сайты (TCP 80/443) ----
        static readonly string[] RModes = { "fake", "multisplit", "multidisorder", "fakedsplit", "fakeddisorder", "hostfakesplit", "fake,multisplit", "fake,multidisorder", "fake,fakedsplit", "fake,hostfakesplit" };
        static readonly string[] RSplitPos = { "1", "2", "midsld", "1,midsld", "sniext+1", "host+1" };
        static readonly string[] RRepeats = { "2", "3", "4", "6", "8", "10", "11", "12" };
        static readonly string[] RFooling = { "ts", "badseq", "md5sig", "badsum", "badseq,ts" };
        static readonly string[] RSeqovl = { "1", "336", "568", "681" };
        static readonly string[] RTlsMod = { "rnd,dupsid,sni=www.google.com", "rnd,dupsid,sni=ya.ru", "rnd,sni=vk.com", "rnd,rndsni", "none" };
        static readonly string[] RHostMod = { "host=www.google.com,altorder=1", "host=ya.ru,altorder=1", "host=vk.com,altorder=1", "host=www.google.com" };
        static readonly string[] RCutoff = { "n2", "n3", "d3" };

        static string OptRx(string name) { return @"(?<=\s|^)--" + Regex.Escape(name) + @"=(""[^""]*""|\S*)"; }
        static string GetOpt(string p, string name) { var m = Regex.Match(p, OptRx(name)); return m.Success ? m.Groups[1].Value : null; }
        static string RemoveOpt(string p, string name) { return Regex.Replace(p, @"\s?" + OptRx(name), ""); }
        static string SetOpt(string p, string name, string value)
        {
            var ms = Regex.Matches(p, OptRx(name));
            if (ms.Count == 0) { var tm = Regex.Match(p, @"\s*$"); return p.Substring(0, tm.Index) + " --" + name + "=" + value + tm.Value; }
            var sb = new StringBuilder(p);
            for (int i = ms.Count - 1; i >= 0; i--)
            {
                var m = ms[i];
                if (i == 0) { sb.Remove(m.Index, m.Length); sb.Insert(m.Index, "--" + name + "=" + value); }
                else { int at = m.Index > 0 && char.IsWhiteSpace(p[m.Index - 1]) ? m.Index - 1 : m.Index; sb.Remove(at, m.Index + m.Length - at); }
            }
            return sb.ToString();
        }

        static List<Cand> Candidates(string raw, string bin, Random rnd)
        {
            var parts = Regex.Split(raw, @"(?<=\s)--new(?=\s)");
            var list = new List<Cand>();
            string[] fakes = new string[0];
            string[] httpFakes = new string[0];
            try { fakes = Directory.GetFiles(bin, "tls_clienthello*.bin").Select(f => System.IO.Path.GetFileName(f)).ToArray(); } catch { }
            try { httpFakes = Directory.GetFiles(bin, "http*.bin").Select(f => System.IO.Path.GetFileName(f)).ToArray(); } catch { }
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                var ft = Regex.Match(p, @"--filter-tcp=(\S+)");
                if (!ft.Success || !Regex.IsMatch(ft.Groups[1].Value, @"(^|,)(80|443)(?=,|$|-)")) continue;
                if (GetOpt(p, "dpi-desync") == null) continue;
                int idx = i;
                Action<string, string, int> add = (np, what, tier) =>
                {
                    if (np == p) return;
                    var cp = (string[])parts.Clone(); cp[idx] = np;
                    list.Add(new Cand { Raw = string.Join("--new", cp), What = "профиль " + (idx + 1) + ": " + what, Order = tier + rnd.NextDouble() * 1.5 });
                };
                foreach (var v in RFooling) add(SetOpt(p, "dpi-desync-fooling", v), "порча " + v, 0);
                foreach (var v in RRepeats) add(SetOpt(p, "dpi-desync-repeats", v), "повторы " + v, 0);
                foreach (var v in RSplitPos) add(SetOpt(p, "dpi-desync-split-pos", v), "разрез " + v, 0);
                foreach (var f in fakes) add(SetOpt(p, "dpi-desync-fake-tls", "\"%BIN%" + f + "\""), "подделка " + f, 0);
                foreach (var v in new[] { "0x00000000", "!" }) add(SetOpt(p, "dpi-desync-fake-tls", v), "подделка " + v, 0);
                foreach (var v in RTlsMod) add(SetOpt(p, "dpi-desync-fake-tls-mod", v), "вид подделки " + v, 1);
                if ((GetOpt(p, "dpi-desync") ?? "").Contains("hostfakesplit")) foreach (var v in RHostMod) add(SetOpt(p, "dpi-desync-hostfakesplit-mod", v), "подмена хоста " + v, 0);
                foreach (var f in httpFakes) add(SetOpt(p, "dpi-desync-fake-http", "\"%BIN%" + f + "\""), "подделка http " + f, 1);
                foreach (var v in RCutoff) add(SetOpt(p, "dpi-desync-cutoff", v), "отсечка " + v, 1);
                if (GetOpt(p, "dpi-desync-split-seqovl") != null && GetOpt(p, "dpi-desync-split-seqovl-pattern") == null)
                    foreach (var f in fakes.Take(3)) add(SetOpt(p, "dpi-desync-split-seqovl-pattern", "\"%BIN%" + f + "\""), "узор перекрытия " + f, 1);
                if (GetOpt(p, "dpi-desync-split-seqovl") != null) add(RemoveOpt(RemoveOpt(p, "dpi-desync-split-seqovl"), "dpi-desync-split-seqovl-pattern"), "без перекрытия", 1);
                foreach (var v in RSeqovl) add(SetOpt(p, "dpi-desync-split-seqovl", v), "перекрытие " + v, 1);
                if (GetOpt(p, "dpi-desync-ttl") != null || GetOpt(p, "dpi-desync-autottl") != null) add(RemoveOpt(RemoveOpt(p, "dpi-desync-ttl"), "dpi-desync-autottl"), "без ttl", 1);
                foreach (var v in new[] { "1", "2", "3", "4", "5" }) add(SetOpt(RemoveOpt(p, "dpi-desync-autottl"), "dpi-desync-ttl", v), "ttl " + v, 1);
                foreach (var v in new[] { "1", "2", "3" }) add(SetOpt(RemoveOpt(p, "dpi-desync-ttl"), "dpi-desync-autottl", v), "autottl " + v, 1);
                foreach (var v in RModes) add(SetOpt(p, "dpi-desync", v), "режим " + v, 2);
            }
            return list.OrderBy(c => c.Order).ToList();
        }

        // ---- сборка (отдельный поток) ----
        class GBest { public string Opts, From; public int Ok, Total; public List<SiteResult> Det; }

        static readonly string OptTokRx = @"--[\w-]+(=(""[^""]*""|\S*))?";
        // настройки обхода профиля — всё, кроме того, ЧТО обходить (фильтры, списки)
        static string DesyncOpts(string profile)
        {
            var keep = Regex.Matches(profile, OptTokRx).Cast<Match>().Select(m => m.Value)
                .Where(t => !Regex.IsMatch(t, @"^--(filter-|hostlist|ipset|wf-|new$)"));
            return string.Join(" ", keep);
        }
        static string Globals(string part0) { return string.Join(" ", Regex.Matches(part0, OptTokRx).Cast<Match>().Select(m => m.Value).Where(t => t.StartsWith("--wf-"))); }
        static string WithoutGlobals(string part0) { return Regex.Replace(part0, @"\s?--wf-[\w-]+=(""[^""]*""|\S*)", ""); }
        const string TestHead = "--wf-tcp=80,443 --filter-tcp=80,443 ";

        static Dictionary<string, int[]> GroupScore(List<SiteResult> det, List<TestTarget> fx)
        {
            var g = new Dictionary<string, int[]>();
            for (int i = 0; i < fx.Count; i++)
            {
                int[] a; if (!g.TryGetValue(fx[i].Group, out a)) g[fx[i].Group] = a = new int[2];
                foreach (var tk in det[i].Tokens) { if (!tk.Contains("UNSUP")) a[1]++; if (tk.EndsWith(":OK")) a[0]++; }
            }
            return g;
        }

        static void BuildWorker(TestSync sync, List<string[]> donors, List<TestTarget> targets, Dictionary<string, List<string>> hosts, string root, string bin, int timeout, int total)
        {
            ThreadPool.SetMinThreads(56, 56);
            var rnd = new Random();
            Action<string> note = m => { lock (sync.RepairLog) sync.RepairLog.Add(m); };
            try
            {
                // 0. лучшая готовая как есть: с ней сравниваем результат; заодно отсеиваем сайты, недоступные по адресу
                var bat0 = ZapretCore.ReadBat(System.IO.Path.Combine(root, donors[0][1]));
                if (bat0 == null) { sync.Error = "не читается " + donors[0][1]; return; }
                sync.Current = "Z · проверяю лучшую готовую (" + ShortName(donors[0][0]) + ")";
                string a0 = ZapretCore.ResolveArgs(bat0.Raw);
                if (a0 == null || !StartWinws(bin, a0, true)) { sync.Error = "не запустилась " + donors[0][1]; return; }
                var det0 = FullCheck(targets, timeout, true);
                if (det0 == null || sync.Cancel) return;
                sync.Done++;
                var fx = new List<TestTarget>(); var detB = new List<SiteResult>(); int dropped = 0;
                for (int i = 0; i < targets.Count; i++) { if (Unreachable(det0[i])) dropped++; else { fx.Add(targets[i]); detB.Add(det0[i]); } }
                int totalT; int baseOk = Score(detB, out totalT);
                string dropNote = dropped > 0 ? " · без " + dropped + " недоступных по адресу" : "";
                note("лучшая готовая " + donors[0][1] + ": " + baseOk + "/" + totalT + dropNote);
                if (totalT == 0) { Add(sync, new StratResult { Name = "Z", File = ZFile, Failed = true, Note = "сайты недоступны по адресу — собрать нельзя" }); return; }
                var groups = fx.Select(t => t.Group).Distinct().ToList();
                string bestName = ShortName(donors[0][0]);

                // что недоступно не из-за DPI: адрес не находится (проверяем через защищённый DNS) или у сайта неверный сертификат
                var noDns = Enumerable.Range(0, targets.Count).Where(i => (det0[i].Diag ?? "").Contains("Could not resolve host")).Select(i => targets[i]).ToList();
                var badCert = Enumerable.Range(0, targets.Count).Where(i => Regex.IsMatch(det0[i].Diag ?? "", "SEC_E_|certificate") && !det0[i].Tokens.Any(t => t.EndsWith(":OK"))).Select(i => targets[i]).ToList();
                var extra = new List<StratResult>();
                if (noDns.Count > 0)
                {
                    sync.Current = "Z · проверяю адреса через защищённый DNS";
                    var dnsRes = new int[noDns.Count];
                    if (DohResolves("https://www.google.com") == 1)      // сам защищённый DNS доступен — иначе ничего не утверждаем
                        System.Threading.Tasks.Parallel.For(0, noDns.Count, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 16 }, i => dnsRes[i] = DohResolves(noDns[i].Url));
                    else for (int i = 0; i < dnsRes.Length; i++) dnsRes[i] = -1;
                    var blocked = Enumerable.Range(0, noDns.Count).Where(i => dnsRes[i] == 1).Select(i => noDns[i].Name).ToList();
                    var missing = Enumerable.Range(0, noDns.Count).Where(i => dnsRes[i] == 0).Select(i => noDns[i].Name).ToList();
                    note("адрес не находится: " + string.Join(", ", noDns.Select(t => t.Name)) + " → через защищённый DNS находятся: " + (blocked.Count > 0 ? string.Join(", ", blocked) : "нет") + (dnsRes.Any(x => x < 0) ? " (проверка DNS не сработала)" : ""));
                    if (blocked.Count > 0) extra.Add(new StratResult { Name = "DNS", File = ZFile, Info = true, Note = "провайдер прячет адреса: " + Short(blocked) + " — поможет защищённый DNS в Windows" });
                    if (missing.Count > 0) extra.Add(new StratResult { Name = "Нет адреса", File = ZFile, Info = true, Note = "таких адресов нет совсем: " + Short(missing) + " — убери их из списка" });
                }
                if (badCert.Count > 0) extra.Add(new StratResult { Name = "Сертификат", File = ZFile, Info = true, Note = "ошибка на стороне сайта: " + Short(badCert.Select(t => t.Name).ToList()) });
                if (baseOk >= totalT)
                {
                    sync.Summary = bestName + " уже открывает все доступные сайты — своя стратегия не нужна";
                    Add(sync, new StratResult { Name = bestName, File = donors[0][1], OK = baseOk, Total = totalT, Details = detB, Note = "100% по сайтам, которые вообще можно открыть" + dropNote });
                    foreach (var r in extra) Add(sync, r);
                    sync.Done = total;
                    return;
                }

                // 1. настройки из профилей лучших стратегий (обход сайтов, TCP 80/443)
                var sets = new List<string[]>();     // [opts, откуда]
                foreach (var d in donors)
                {
                    var b = ZapretCore.ReadBat(System.IO.Path.Combine(root, d[1])); if (b == null) continue;
                    foreach (var part in Regex.Split(b.Raw, @"(?<=\s)--new(?=\s)"))
                    {
                        var ft = Regex.Match(part, @"--filter-tcp=(\S+)");
                        if (!ft.Success || !Regex.IsMatch(ft.Groups[1].Value, @"(^|,)(80|443)(?=,|$|-)")) continue;
                        string o = DesyncOpts(part);
                        if (o.Contains("--dpi-desync=") && !sets.Any(x => x[0] == o)) sets.Add(new[] { o, ShortName(d[0]) });
                    }
                }
                var best = groups.ToDictionary(g => g, g => (GBest)null);
                int si = 0;
                foreach (var set in sets)
                {
                    if (sync.Cancel) return;
                    si++; sync.Current = "Z · настройки из " + set[1] + " (" + si + " из " + sets.Count + ")";
                    if (sync.Done < total - RepairBudget - 3) sync.Done++;
                    string args = ZapretCore.ResolveArgs(TestHead + set[0]);
                    if (args == null || !StartWinws(bin, args, false)) continue;
                    var d = FullCheck(fx, timeout, false); if (d == null) return;
                    foreach (var kv in GroupScore(d, fx))
                    {
                        var cur = best[kv.Key];
                        if (cur == null || kv.Value[0] > cur.Ok)
                            best[kv.Key] = new GBest { Opts = set[0], From = set[1], Ok = kv.Value[0], Total = kv.Value[1], Det = Enumerable.Range(0, fx.Count).Where(i => fx[i].Group == kv.Key).Select(i => d[i]).ToList() };
                    }
                }
                sync.Done = total - RepairBudget - 3;

                // 2. доводка: группы, где лучшие настройки не открывают всё, — меняем по одной настройке;
                //    быстрая проверка (только то, что не открывается, + контрольные) → полная проверка группы.
                //    Если одиночные правки кончились — идём от самого «многообещающего» варианта (вторые правки).
                var gBase = GroupScore(detB, fx);
                var tried = new HashSet<string>(sets.SelectMany(x => groups.Select(g => g + "|" + x[0])));
                var queues = new Dictionary<string, List<Cand>>();
                var stale = groups.ToDictionary(g => g, g => 0);
                var promising = groups.ToDictionary(g => g, g => new List<KeyValuePair<int, string>>());
                var expanded = new HashSet<string>();
                int used = 0, turn = 0;
                while (used < RepairBudget && !sync.Cancel)
                {
                    var weak = groups.Where(g => best[g] != null && best[g].Ok < best[g].Total && stale[g] < GroupStale).ToList();
                    // очередь пуста — пробуем продолжить от многообещающего варианта
                    foreach (var g in weak.ToList())
                    {
                        if (queues.ContainsKey(g) && queues[g].Count > 0) continue;
                        string from = !queues.ContainsKey(g) ? best[g].Opts
                            : promising[g].OrderByDescending(x => x.Key).Select(x => x.Value).FirstOrDefault(o => !expanded.Contains(g + "|" + o));
                        if (from == null) { weak.Remove(g); continue; }
                        expanded.Add(g + "|" + from);
                        queues[g] = Candidates(TestHead + from, bin, rnd).Where(cd => !tried.Contains(g + "|" + DesyncOpts(cd.Raw))).ToList();
                        if (queues[g].Count == 0) weak.Remove(g);
                    }
                    if (weak.Count == 0) break;
                    string grp = weak.OrderBy(g => (double)best[g].Ok / Math.Max(1, best[g].Total)).ElementAt(turn++ % weak.Count);
                    var gb = best[grp];
                    var q = queues[grp];
                    var c = q[0]; q.RemoveAt(0);
                    string opts = DesyncOpts(c.Raw);
                    if (!tried.Add(grp + "|" + opts)) continue;
                    used++; stale[grp]++; if (sync.Done < total - 2) sync.Done++;
                    sync.Current = "Z · доводка «" + grp + "» · вариант " + used + " из " + RepairBudget + " · сейчас " + gb.Ok + "/" + gb.Total;
                    string args = ZapretCore.ResolveArgs(TestHead + opts);
                    if (args == null || !StartWinws(bin, args, false) || sync.Cancel) continue;
                    var gi = Enumerable.Range(0, fx.Count).Where(i => fx[i].Group == grp).ToList();
                    var gfx = gi.Select(i => fx[i]).ToList();
                    var probes = new List<int[]>(); var isFail = new List<bool>();
                    for (int j = 0; j < gfx.Count; j++)
                        for (int k = 0; k < gb.Det[j].Tokens.Length; k++)
                            if (!gb.Det[j].Tokens[k].Contains("UNSUP")) { probes.Add(new[] { j, k }); isFail.Add(!gb.Det[j].Tokens[k].EndsWith(":OK")); }
                    var fail = Enumerable.Range(0, probes.Count).Where(i => isFail[i]).Select(i => probes[i]).ToList();
                    var can = Enumerable.Range(0, probes.Count).Where(i => !isFail[i]).OrderBy(i => rnd.Next()).Take(Canaries).Select(i => probes[i]).ToList();
                    var r = Probe(gfx, fail.Concat(can).ToList(), Math.Min(timeout, 2));
                    if (r == null) return;
                    int fixedN = 0, broke = 0;
                    for (int i = 0; i < r.Length; i++) { if (i < fail.Count) { if (r[i]) fixedN++; } else if (!r[i]) broke++; }
                    if (fixedN > 0) promising[grp].Add(new KeyValuePair<int, string>(fixedN - broke, opts));
                    if (fixedN == 0 || broke > 0) continue;
                    sync.Current = "Z · доводка «" + grp + "» · вариант " + used + " из " + RepairBudget + " · полная проверка";
                    var d = FullCheck(gfx, timeout, false); if (d == null) return;
                    int tt; int sc = Score(d, out tt);
                    if (sc <= gb.Ok) continue;
                    best[grp] = new GBest { Opts = opts, From = gb.From.EndsWith("*") ? gb.From : gb.From + "*", Ok = sc, Total = tt, Det = d };
                    queues.Remove(grp); stale[grp] = 0; promising[grp].Clear();
                    note("«" + grp + "»: " + c.What + " → " + sc + "/" + tt);
                }
                if (sync.Cancel) return;
                sync.Done = total - 2;

                // 3. сборка: свой профиль (со своим списком сайтов) — только для групп, где найденное лучше лучшей готовой как есть;
                //    профили стоят ПЕРЕД профилями лучшей готовой, всё остальное (голос, QUIC, игры) — без изменений
                var listDir = System.IO.Path.Combine(root, "lists");
                var use = groups.Where(g => best[g] != null && best[g].Ok > (gBase.ContainsKey(g) ? gBase[g][0] : 0)).ToList();
                foreach (var g in groups) note("«" + g + "»: как есть " + (gBase.ContainsKey(g) ? gBase[g][0] + "/" + gBase[g][1] : "-") + ", найдено " + (best[g] == null ? "-" : best[g].Ok + "/" + best[g].Total + " (" + best[g].From + ")") + (use.Contains(g) ? " → свой профиль" : ""));
                var parts = Regex.Split(bat0.Raw, @"(?<=\s)--new(?=\s)");
                string raw = null; List<SiteResult> dF = null; int okF = 0, tf = 0;
                for (int round = 1; round <= 3 && !sync.Cancel; round++)
                {
                    foreach (var f in Directory.GetFiles(listDir, "zgui-*.txt")) { try { File.Delete(f); } catch (Exception ex) { Log.Err("z lists", ex); } }
                    var profiles = new List<string>(); int n = 0;
                    foreach (var byOpts in use.GroupBy(g => best[g].Opts))
                    {
                        var lists = new List<string>();
                        foreach (var g in byOpts)
                        {
                            List<string> h; if (!hosts.TryGetValue(g, out h) || h.Count == 0) continue;
                            n++; string lf = "zgui-" + n + ".txt";
                            File.WriteAllLines(System.IO.Path.Combine(listDir, lf), h, new UTF8Encoding(false));
                            lists.Add("--hostlist=\"%LISTS%" + lf + "\"");
                        }
                        if (lists.Count > 0) profiles.Add("--filter-tcp=80,443 " + string.Join(" ", lists) + " " + byOpts.Key);
                    }
                    if (profiles.Count == 0) { raw = null; break; }
                    string first = WithoutGlobals(parts[0]).Trim();
                    var tail = new List<string>(profiles);
                    if (first.Length > 0) tail.Add(first);
                    tail.AddRange(parts.Skip(1).Select(x => x.Trim()).Where(x => x.Length > 0));
                    raw = " " + Globals(parts[0]) + " " + string.Join(" --new ", tail);

                    // 4. проверка собранной; группы, которые стали хуже, чем в лучшей готовой, отдаём обратно ей и проверяем снова
                    sync.Current = "Z · финальная проверка" + (round > 1 ? " (" + round + ")" : "");
                    string af = ZapretCore.ResolveArgs(raw);
                    if (af == null || !StartWinws(bin, af, false)) { sync.Error = "собранная стратегия не запустилась"; return; }
                    dF = FullCheck(fx, timeout, false); if (dF == null) return;
                    okF = Score(dF, out tf);
                    var gsF = GroupScore(dF, fx);
                    var worse = use.Where(g => gsF.ContainsKey(g) && gBase.ContainsKey(g) && gsF[g][0] < gBase[g][0]).ToList();
                    note("сборка " + round + ": " + okF + "/" + tf + (worse.Count > 0 ? ", хуже в: " + string.Join(", ", worse) : ""));
                    if (worse.Count == 0) break;
                    use = use.Except(worse).ToList();
                    if (use.Count == 0) { raw = null; break; }
                }
                if (sync.Cancel) return;
                sync.Done = total;
                if (raw == null || dF == null)
                {
                    Add(sync, new StratResult { Name = "Z", File = ZFile, OK = baseOk, Total = totalT, Details = detB, Note = ShortName(donors[0][0]) + " и так лучшая для твоих сайтов — не сохранено" });
                }
                else if (okF > baseOk)
                {
                    SaveAs(root, ZFile, donors[0][1], bat0, raw);
                    Add(sync, new StratResult { Name = "Z", File = ZFile, OK = okF, Total = tf, Details = dF, Badge = "собрано", Note = "лучшая готовая " + ShortName(donors[0][0]) + ": " + baseOk + "/" + totalT + dropNote, Apply = true });
                }
                else Add(sync, new StratResult { Name = "Z", File = ZFile, OK = okF, Total = tf, Details = dF, Note = "не лучше " + ShortName(donors[0][0]) + " (" + baseOk + "/" + totalT + ") — не сохранено" });
                foreach (var r in extra) Add(sync, r);
                var gsR = GroupScore(dF ?? detB, fx);
                foreach (var g in groups)
                {
                    var b = best[g]; int[] sc; gsR.TryGetValue(g, out sc);
                    string from = !use.Contains(g) || raw == null ? "как в " + ShortName(donors[0][0]) : "настройки " + b.From.TrimEnd('*') + (b.From.EndsWith("*") ? " (доведены)" : "");
                    Add(sync, new StratResult { Name = g, File = ZFile, OK = sc != null ? sc[0] : 0, Total = sc != null ? sc[1] : 0, Note = from });
                }
            }
            catch (Exception ex) { sync.Error = ex.Message; }
            finally { KillWinws(); sync.Finished = true; }
        }

        // строка-пояснение в результатах: что мешает, кроме DPI (переносится по словам, без полосы и процентов)
        UIElement InfoRow(StratResult r)
        {
            var b = new Border { Padding = new Thickness(14, 10, 14, 10), Background = Theme.B("#1E2129"), BorderThickness = new Thickness(2), BorderBrush = Theme.B("#3A3320"), Margin = new Thickness(0, 0, 0, 6) };
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = Theme.B("#C9CCD6") };
            tb.Inlines.Add(new System.Windows.Documents.Run(Translate.T(r.Name) + "  ") { FontWeight = FontWeights.SemiBold, Foreground = Theme.B("#FFB547") });
            tb.Inlines.Add(new System.Windows.Documents.Run(Translate.T(r.Note)));
            b.Child = tb;
            return b;
        }

        static string Short(List<string> names) { return string.Join(", ", names.Take(4)) + (names.Count > 4 ? " и ещё " + (names.Count - 4) : ""); }

        // 1 — адрес находится через защищённый DNS (значит, его прячет DNS провайдера), 0 — не находится и там, -1 — проверить не удалось
        static int DohResolves(string url)
        {
            try
            {
                var psi = new ProcessStartInfo(CurlExe(), "-s -I -o NUL -m 6 --connect-timeout 4 --doh-url https://1.1.1.1/dns-query \"" + url + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } return -1; }
                    if (p.ExitCode == 6) return 0;
                    if (p.ExitCode == 2 || p.ExitCode == 4) return -1;      // нет поддержки DoH у этого curl
                    return 1;
                }
            }
            catch (Exception ex) { Log.Err("doh", ex); return -1; }
        }

        static void SaveAs(string root, string name, string fromFile, ZapretCore.BatCmd bat, string raw)
        {
            var lines = new List<string>();
            for (int i = 0; i < bat.Start; i++) lines.Add(bat.Lines[i]);
            lines.Add("rem built by Zapret GUI from \"" + fromFile + "\" " + DateTime.Now.ToString("yyyy-MM-dd"));
            lines.Add(bat.Prefix + " " + Regex.Replace(raw.Trim(), @"\s{2,}", " "));
            for (int i = bat.End + 1; i < bat.Lines.Length; i++) lines.Add(bat.Lines[i]);
            File.WriteAllLines(System.IO.Path.Combine(root, name), lines, new UTF8Encoding(false));
        }

    }
}
