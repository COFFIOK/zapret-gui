// Zapret (C#) — список стратегий с результатами последнего теста и запасные стратегии.
// Проценты: после каждого теста результат стратегии запоминается («scores») и показывается в выпадающем списке.
// Запасные: пока обход включён, раз в 2 минуты тихо проверяются несколько сайтов; если они дважды подряд
// не открываются (а интернет при этом есть) или winws сам закрылся — включается следующая стратегия из цепочки.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ZapretCS
{
    partial class MainUi
    {
        // ---- результаты тестов ----
        class Score { public int Ok, Total; public double Speed; public bool Failed; public int Pct { get { return Total > 0 ? Ok * 100 / Total : 0; } } }

        Dictionary<string, Score> Scores()
        {
            var res = new Dictionary<string, Score>(StringComparer.OrdinalIgnoreCase);
            var d = S.Raw("scores") as Dictionary<string, object>;
            if (d == null) return res;
            foreach (var kv in d)
            {
                var p = Convert.ToString(kv.Value, CultureInfo.InvariantCulture).Split('/');
                if (p.Length < 3) continue;
                int ok, tot; double sp;
                if (int.TryParse(p[0], out ok) && int.TryParse(p[1], out tot) && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out sp))
                    res[kv.Key] = new Score { Ok = ok, Total = tot, Speed = sp, Failed = p.Length > 3 && p[3] == "x" };
            }
            return res;
        }

        void SaveScores(IEnumerable<StratResult> results)
        {
            var d = (S.Raw("scores") as Dictionary<string, object>) ?? new Dictionary<string, object>();
            var copy = new Dictionary<string, object>(d, StringComparer.OrdinalIgnoreCase);
            foreach (var r in results)
                copy[r.File] = r.OK + "/" + r.Total + "/" + r.Speed.ToString("0", CultureInfo.InvariantCulture) + (r.Failed ? "/x" : "");
            S.Set("scores", copy); S.Save();
        }

        static string SpeedText(double kbs)
        {
            if (kbs <= 0) return "";
            return kbs >= 1024 ? (kbs / 1024).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + " МБ/с" : ((int)kbs) + " КБ/с";
        }

        // список стратегий: проверенные — сверху по проценту (при равенстве — по скорости), нерабочие — серые
        void RefreshStrategyBox()
        {
            var box = E<ComboBox>("StrategyBox");
            var curItem = box.SelectedItem as ComboBoxItem;
            string cur = curItem != null ? curItem.Tag as string : S.Str("strategy", "general") + ".bat";
            var files = ZapretCore.Strategies().ToList();
            var sc = Scores();
            var order = files.Select((f, i) => new { f, i, s = sc.ContainsKey(f) ? sc[f] : null })
                .OrderBy(x => x.s == null ? 1 : 0)
                .ThenByDescending(x => x.s == null ? 0 : (x.s.Failed ? -1 : x.s.Pct))
                .ThenByDescending(x => x.s == null ? 0 : x.s.Speed)
                .ThenBy(x => x.i).ToList();
            bool was = loading; loading = true;
            box.Items.Clear();
            foreach (var x in order)
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(x.f);
                var it = new ComboBoxItem { Content = x.s == null ? name : name + "  ·  " + (x.s.Failed ? "не запустилась" : x.s.Pct + "%"), Tag = x.f };
                if (x.s != null && (x.s.Failed || x.s.Ok == 0)) it.Opacity = 0.45;
                box.Items.Add(it);
            }
            SelectByTag(box, cur);
            if (box.SelectedItem == null && box.Items.Count > 0) box.SelectedIndex = 0;
            loading = was;
        }

        static string StratName(ComboBoxItem it) { return it == null ? null : System.IO.Path.GetFileNameWithoutExtension((string)it.Tag); }

        // ---- запасные стратегии ----
        DispatcherTimer fbTimer;
        bool fbWant;            // пользователь включил обход (а не тест/выключение)
        int fbFails, fbDead;
        DateTime fbGraceUntil;
        bool fbBusy, fbGaveUp;

        void WireFallback()
        {
            foreach (var n in new[] { "Fallback1Box", "Fallback2Box" })
            {
                string key = n == "Fallback1Box" ? "fallback1" : "fallback2";
                E<ComboBox>(n).SelectionChanged += (s, e) => Safe(() =>
                {
                    if (loading) return;
                    var it = E<ComboBox>(n).SelectedItem as ComboBoxItem;
                    S.Set(key, it == null ? "" : (string)it.Tag); S.Save();
                    fbGaveUp = false;
                });
            }
            fbTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            int tick = 0;
            fbTimer.Tick += (s, e) => Safe(() =>
            {
                tick++;
                if (!testing && State == "on") fbWant = true;           // обход включён (в т.ч. автозапуском) — следим
                if (testing || fbBusy || !fbWant || fbGaveUp || DateTime.Now < fbGraceUntil) return;
                if (Chain().Count < 2) return;                                  // запасных нет — нечего делать
                if (ZapretCore.ServiceState() == "Running") return;              // службой управляет Windows
                // winws сам закрылся при включённом обходе
                if (State == "off" && !Mon.Winws) { if (++fbDead >= 2) { fbDead = 0; Switch("zapret закрылся"); } return; }
                fbDead = 0;
                if (State != "on" || tick % 4 != 0) return;                     // сайты — раз в 2 минуты
                var probes = ProbeTargets();
                if (probes.Count == 0) return;
                fbBusy = true;
                int ok = 0; bool net = true;
                Background(() =>
                {
                    net = Curl("https://ya.ru", 1, 4)[0].EndsWith(":OK") || Curl("https://vk.com", 1, 4)[0].EndsWith(":OK");
                    if (net) foreach (var t in probes) if (Curl(t.Url, 1, 5)[0].EndsWith(":OK")) ok++;
                }, err =>
                {
                    fbBusy = false;
                    if (err != null || !net || testing || State != "on") return;   // нет интернета — стратегия ни при чём
                    if (ok * 2 >= probes.Count) { fbFails = 0; return; }
                    if (++fbFails >= 2) { fbFails = 0; Switch("сайты перестали открываться"); }
                });
            });
            fbTimer.Start();
        }

        // основная (выбранная пользователем) → запасная 1 → запасная 2
        List<string> Chain()
        {
            var c = new List<string>();
            string main = S.Str("fallbackMain", null) ?? (S.Str("strategy", "general") + ".bat");
            foreach (var f in new[] { main, S.Str("fallback1", ""), S.Str("fallback2", "") })
                if (!string.IsNullOrEmpty(f) && !c.Contains(f, StringComparer.OrdinalIgnoreCase) && File.Exists(System.IO.Path.Combine(ZapretCore.Root, f))) c.Add(f);
            return c;
        }

        // по одному адресу из каждой группы (до 4) — то, что обход должен открывать
        List<TestTarget> ProbeTargets()
        {
            try { return TestTargets().Where(t => t.Url != null).GroupBy(t => t.Group).Select(g => g.First()).Take(4).ToList(); }
            catch (Exception ex) { Log.Err("fallback probes", ex); return new List<TestTarget>(); }
        }

        void Switch(string why)
        {
            var chain = Chain();
            var box = E<ComboBox>("StrategyBox");
            string cur = box.SelectedItem is ComboBoxItem ? (string)((ComboBoxItem)box.SelectedItem).Tag : chain[0];
            int i = chain.FindIndex(f => string.Equals(f, cur, StringComparison.OrdinalIgnoreCase));
            if (i + 1 >= chain.Count)
            {
                fbGaveUp = true;
                Toast("Запасные стратегии тоже не помогли — запусти тест стратегий", true);
                Log.Write("FALLBACK: " + why + " на " + System.IO.Path.GetFileNameWithoutExtension(cur) + ", запасные кончились");
                if (State == "off") StartZapret();
                return;
            }
            string next = chain[i + 1];
            Log.Write("FALLBACK: " + why + " на " + System.IO.Path.GetFileNameWithoutExtension(cur) + " → " + next);
            fbGraceUntil = DateTime.Now.AddMinutes(3);
            fbSwitching = true;
            SelectByTag(box, next);                       // SelectionChanged перезапустит zapret, если он включён
            fbSwitching = false;
            if (State == "off") StartZapret();
            Toast(System.IO.Path.GetFileNameWithoutExtension(cur) + " перестала работать — включил запасную " + System.IO.Path.GetFileNameWithoutExtension(next));
        }
        bool fbSwitching;

        // выпадающие списки запасных в настройках: «Нет» + все стратегии
        void FillFallbackBoxes()
        {
            bool was = loading; loading = true;
            foreach (var n in new[] { "Fallback1Box", "Fallback2Box" })
            {
                var cb = E<ComboBox>(n); cb.Items.Clear();
                cb.Items.Add(new ComboBoxItem { Content = Translate.T("Нет"), Tag = "" });
                foreach (var f in ZapretCore.Strategies()) cb.Items.Add(new ComboBoxItem { Content = ShortName(System.IO.Path.GetFileNameWithoutExtension(f)), Tag = f });
                SelectByTag(cb, S.Str(n == "Fallback1Box" ? "fallback1" : "fallback2", ""));
                if (cb.SelectedItem == null) cb.SelectedIndex = 0;
            }
            loading = was;
        }
    }
}
