// Zapret (C#) — починка стратегий (бета): меняет настройки стратегии по одной и проверяет сайты,
// пока не станет лучше; удачный вариант сохраняется рядом как «general (ALT3 FIX).bat».
// Запускается только кнопкой «Починить стратегии» после теста.
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
        const int RepairBudget = 24;            // сколько вариантов пробуем на одну стратегию
        bool repairMode;
        List<string> lastGroups;
        List<StratResult> lastResults;

        // ---- режим окна ----
        void EnterRepairMode()
        {
            repairMode = true;
            El("FixBtn").Visibility = Visibility.Collapsed;
            Tb("TestTitle").Text = Translate.T("Починка стратегий");
            Tb("TestSub").Text = Translate.T("Меняю настройки стратегии и проверяю, пока сайты не откроются");
            El("ResultsHead").Visibility = Visibility.Collapsed;
            E<Panel>("TestResults").Children.Clear();
            ((ScaleTransform)Win.FindName("TestBarScale")).ScaleX = 0;
            El("TestSetup").Visibility = Visibility.Visible; Anim.D(A("TestSetup"), Anim.OP, 1, 250, 0);
            FillTestSetup();
            // группы — как в последнем тесте; стратегии — те, что открыли не всё (их и чиним)
            if (lastGroups != null)
                foreach (ToggleButton c in E<Panel>("GroupChips").Children) c.IsChecked = lastGroups.Contains((string)c.Tag);
            if (lastResults != null)
            {
                var weak = new HashSet<string>(lastResults.Where(r => r.Failed || r.OK < r.Total).Select(r => r.File), StringComparer.OrdinalIgnoreCase);
                if (weak.Count > 0)
                    foreach (ToggleButton c in E<Panel>("StratChips").Children) c.IsChecked = weak.Contains((string)c.Tag);
            }
            Btn("RunBtn").Content = Translate.T("Начать починку");
            UpdateTestHint();
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

        void StartRepair()
        {
            if (ZapretCore.ServiceState() == "Running") { Toast("zapret работает как служба. Удали службу в «Сервис и диагностика», чтобы чинить стратегии.", true); return; }
            var groups = Picked(E<Panel>("GroupChips"));
            var targets = TestTargets().Where(t => groups.Contains(t.Group) && t.Url != null).ToList();
            var strategies = Picked(E<Panel>("StratChips")).Select(f => new[] { System.IO.Path.GetFileNameWithoutExtension(f), f }).ToList();
            if (strategies.Count == 0 || targets.Count == 0) { Toast("Нужны группы с сайтами — по пингу починка не работает", true); return; }
            lastGroups = groups;

            testWasOn = State == "on";
            testing = true; testShown = 0; testTotal = strategies.Count * (RepairBudget + 1); testBegan = DateTime.Now;
            Btn("PowerBtn").IsEnabled = false; E<ComboBox>("StrategyBox").IsEnabled = false;
            SetVisual("off", "Идёт починка стратегий…");
            testSync = new TestSync(); abortChecks = false;
            var sync = testSync; string root = ZapretCore.Root, bin = System.IO.Path.Combine(root, "bin");
            new Thread(() => RepairWorker(sync, strategies, targets, root, bin, 3)) { IsBackground = true, Name = "repair" }.Start();

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

        void FinishRepair(bool cancelled)
        {
            testTimer.Stop(); testing = false;
            var sync = testSync;
            Anim.D(A("TestShimmer"), Anim.OP, 0, 300);
            Btn("PowerBtn").IsEnabled = true; E<ComboBox>("StrategyBox").IsEnabled = true;
            Btn("RunBtn").Content = Translate.T("Новый тест"); Btn("RunBtn").IsEnabled = true;
            Anim.D(A("TestBarScale"), Anim.SX, 1, 400);
            List<StratResult> res; lock (sync.Results) res = sync.Results.ToList();
            int fixedN = res.Count(r => r.Apply);
            Tb("TestStatus").Text = Translate.T((cancelled ? "Починка остановлена" : "Починка завершена") + " · улучшено " + fixedN + " из " + res.Count);
            if (sync.Error != null) Toast("Ошибка починки: " + sync.Error, true);
            try { if (sync.RepairLog.Count > 0) Log.Write("REPAIR:\r\n  " + string.Join("\r\n  ", sync.RepairLog)); } catch (Exception logEx) { Log.Err("repair log", logEx); }
            State = "x";
            if (testWasOn) { State = "off"; StartZapret(); } else { visAt = 0; Mon.Wake(); }
            if (!cancelled) Toast(fixedN > 0 ? "Готово: починено стратегий — " + fixedN + ". Нажми «Включить» у нужной" : "Улучшить не получилось — попробуй другие стратегии");
        }

        // ---- фоновая починка (отдельный поток) ----
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

        // ---- быстрая починка ----
        // 1) базовая проверка всех сайтов; сайты, недоступные по адресу (не находится адрес / нет соединения), отбрасываем — их никакая стратегия не починит
        // 2) каждый вариант сначала проверяется быстро: только то, что сейчас НЕ открывается, плюс несколько уже работающих «контрольных» проверок
        // 3) если вариант что-то починил и ничего не сломал — полная проверка всех сайтов на том же запуске winws (она же — повторное подтверждение)
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
            return Regex.IsMatch(d.Diag ?? "", "Could not resolve host|Connection timed out after|Failed to connect");
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

        static void RepairWorker(TestSync sync, List<string[]> strategies, List<TestTarget> targets, string root, string bin, int timeout)
        {
            ThreadPool.SetMinThreads(56, 56);
            bool first = true; var rnd = new Random();
            try
            {
                int index = 0;
                foreach (var s in strategies)
                {
                    if (sync.Cancel) break;
                    index++;
                    string shortName = ShortName(s[0]), file = s[1], pos = " (" + index + " из " + strategies.Count + ")";
                    int doneAt = sync.Done;
                    Action skip = () => sync.Done = doneAt + RepairBudget + 1;
                    var bat = ZapretCore.ReadBat(System.IO.Path.Combine(root, file));
                    if (bat == null) { Add(sync, new StratResult { Name = shortName, File = file, Failed = true, Note = "не стратегия zapret" }); skip(); continue; }

                    // 1. как есть
                    sync.Current = shortName + pos + " · проверяю как есть";
                    string args0 = ZapretCore.ResolveArgs(bat.Raw);
                    bool up0 = args0 != null && StartWinws(bin, args0, first); bool wasFirst = first; first = false;
                    if (sync.Cancel) break;
                    if (!up0) { Add(sync, new StratResult { Name = shortName, File = file, Failed = true, Note = "не запустилась" }); skip(); continue; }
                    var det0 = FullCheck(targets, timeout, wasFirst);
                    if (det0 == null || sync.Cancel) break;
                    sync.Done++;
                    // недоступные по адресу сайты в починке не участвуют
                    var fx = new List<TestTarget>(); var det = new List<SiteResult>(); int dropped = 0;
                    for (int i = 0; i < targets.Count; i++) { if (Unreachable(det0[i])) dropped++; else { fx.Add(targets[i]); det.Add(det0[i]); } }
                    string dropNote = dropped > 0 ? " · без " + dropped + " недоступных по адресу" : "";
                    int total; int baseOk = Score(det, out total);
                    if (total == 0) { Add(sync, new StratResult { Name = shortName, File = file, Details = det0, Note = "сайты недоступны по адресу — не чинится" }); skip(); continue; }
                    if (baseOk >= total) { Add(sync, new StratResult { Name = shortName, File = file, OK = baseOk, Total = total, Details = det, Note = "уже работает на 100%" + dropNote }); skip(); continue; }

                    // все проверки (сайт, вид) и что из них сейчас проходит
                    var all = new List<int[]>(); var okNow = new Dictionary<string, bool>();
                    for (int i = 0; i < fx.Count; i++)
                        for (int k = 0; k < det[i].Tokens.Length; k++)
                            if (!det[i].Tokens[k].Contains("UNSUP")) { all.Add(new[] { i, k }); okNow[i + ":" + k] = det[i].Tokens[k].EndsWith(":OK"); }

                    string best = bat.Raw; int bestOk = baseOk; var bestDet = det;
                    var tried = new HashSet<string> { best };
                    var steps = new List<string>();
                    var cands = Candidates(best, bin, rnd);
                    int ci = 0, used = 0;
                    while (used < RepairBudget && !sync.Cancel && bestOk < total && ci < cands.Count)
                    {
                        var c = cands[ci++];
                        if (!tried.Add(c.Raw)) continue;
                        used++; sync.Done++;
                        sync.Current = shortName + pos + " · вариант " + used + " из " + RepairBudget + " · сейчас " + bestOk + "/" + total;
                        string args = ZapretCore.ResolveArgs(c.Raw);
                        if (args == null || !StartWinws(bin, args, false) || sync.Cancel) continue;
                        // 2. быстрая проверка
                        var failing = all.Where(p => !okNow[p[0] + ":" + p[1]]).ToList();
                        var canary = all.Where(p => okNow[p[0] + ":" + p[1]]).OrderBy(p => rnd.Next()).Take(Canaries).ToList();
                        var q = Probe(fx, failing.Concat(canary).ToList(), Math.Min(timeout, 2));
                        if (q == null) break;
                        int fixedN = 0, broke = 0;
                        for (int i = 0; i < q.Length; i++) { if (i < failing.Count) { if (q[i]) fixedN++; } else if (!q[i]) broke++; }
                        if (fixedN == 0 || broke > 0) continue;
                        // 3. полная проверка
                        sync.Current = shortName + pos + " · вариант " + used + " из " + RepairBudget + " · полная проверка";
                        var d = FullCheck(fx, timeout, false);
                        if (d == null) break;
                        int t2; int sc = Score(d, out t2);
                        if (sc <= bestOk) continue;
                        best = c.Raw; bestOk = sc; bestDet = d; steps.Add(c.What + " → " + sc + "/" + total);
                        foreach (var p in all) okNow[p[0] + ":" + p[1]] = d[p[0]].Tokens[p[1]].EndsWith(":OK");
                        cands = Candidates(best, bin, rnd).Where(x => !tried.Contains(x.Raw)).ToList(); ci = 0;
                    }
                    skip();
                    if (bestOk > baseOk)
                    {
                        string nf = SaveFixed(root, file, bat, best);
                        lock (sync.RepairLog) sync.RepairLog.Add(file + ": " + baseOk + "/" + total + " → " + bestOk + "/" + total + " → " + nf + " (" + string.Join("; ", steps) + ")" + dropNote);
                        Add(sync, new StratResult { Name = ShortName(System.IO.Path.GetFileNameWithoutExtension(nf)), File = nf, OK = bestOk, Total = total, Details = bestDet, Badge = "починено", Note = "было " + baseOk + "/" + total + dropNote, Apply = true });
                    }
                    else
                    {
                        lock (sync.RepairLog) sync.RepairLog.Add(file + ": " + baseOk + "/" + total + ", улучшить не удалось (вариантов: " + used + ")" + dropNote);
                        Add(sync, new StratResult { Name = shortName, File = file, OK = baseOk, Total = total, Details = det, Note = (sync.Cancel ? "остановлено" : "не удалось улучшить") + dropNote });
                    }
                }
            }
            catch (Exception ex) { sync.Error = ex.Message; }
            finally { KillWinws(); sync.Finished = true; }
        }
        static void Add(TestSync sync, StratResult r) { lock (sync.Results) sync.Results.Add(r); }

        // ---- варианты: одна правка в одном профиле (между --new), который обходит сайты (TCP 80/443) ----
        static readonly string[] RModes = { "fake", "multisplit", "multidisorder", "fakedsplit", "fakeddisorder", "hostfakesplit", "fake,multisplit", "fake,multidisorder", "fake,fakedsplit", "fake,hostfakesplit" };
        static readonly string[] RSplitPos = { "1", "2", "midsld", "1,midsld", "sniext+1", "host+1" };
        static readonly string[] RRepeats = { "2", "4", "6", "8", "11" };
        static readonly string[] RFooling = { "ts", "badseq", "md5sig", "badsum", "badseq,ts" };
        static readonly string[] RSeqovl = { "1", "336", "568", "681" };
        static readonly string[] RTlsMod = { "rnd,dupsid,sni=www.google.com", "rnd,rndsni" };

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
            try { fakes = Directory.GetFiles(bin, "tls_clienthello*.bin").Select(f => System.IO.Path.GetFileName(f)).ToArray(); } catch { }
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
                foreach (var v in RTlsMod) add(SetOpt(p, "dpi-desync-fake-tls-mod", v), "вид подделки " + v, 1);
                if (GetOpt(p, "dpi-desync-split-seqovl") != null) add(RemoveOpt(RemoveOpt(p, "dpi-desync-split-seqovl"), "dpi-desync-split-seqovl-pattern"), "без перекрытия", 1);
                foreach (var v in RSeqovl) add(SetOpt(p, "dpi-desync-split-seqovl", v), "перекрытие " + v, 1);
                if (GetOpt(p, "dpi-desync-ttl") != null || GetOpt(p, "dpi-desync-autottl") != null) add(RemoveOpt(RemoveOpt(p, "dpi-desync-ttl"), "dpi-desync-autottl"), "без ttl", 1);
                foreach (var v in new[] { "3", "5" }) add(SetOpt(RemoveOpt(p, "dpi-desync-autottl"), "dpi-desync-ttl", v), "ttl " + v, 1);
                add(SetOpt(RemoveOpt(p, "dpi-desync-ttl"), "dpi-desync-autottl", "2"), "autottl 2", 1);
                foreach (var v in RModes) add(SetOpt(p, "dpi-desync", v), "режим " + v, 2);
            }
            return list.OrderBy(c => c.Order).ToList();
        }

        // ---- сохранение: копия исходного .bat, где строка winws заменена на найденную ----
        static string SaveFixed(string root, string file, ZapretCore.BatCmd bat, string raw)
        {
            string baseName = System.IO.Path.GetFileNameWithoutExtension(file);
            var m = Regex.Match(baseName, @"^general\s*\((.+?)(?:\s+FIX\d*)?\)$");
            string stem = m.Success ? "general (" + m.Groups[1].Value + " FIX" : Regex.Replace(baseName, @"\s+FIX\d*$", "") + " FIX";
            string close = m.Success ? ")" : "", name = null;
            for (int n = 1; n < 100; n++) { name = stem + (n > 1 ? n.ToString() : "") + close + ".bat"; if (!File.Exists(System.IO.Path.Combine(root, name))) break; }
            var lines = new List<string>();
            for (int i = 0; i < bat.Start; i++) lines.Add(bat.Lines[i]);
            lines.Add("rem repaired by Zapret GUI from \"" + file + "\" " + DateTime.Now.ToString("yyyy-MM-dd"));
            lines.Add(bat.Prefix + " " + raw.Trim());
            for (int i = bat.End + 1; i < bat.Lines.Length; i++) lines.Add(bat.Lines[i]);
            File.WriteAllLines(System.IO.Path.Combine(root, name), lines, new UTF8Encoding(false));
            return name;
        }
    }
}
