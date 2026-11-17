// Zapret (C#) — перенос настроек в новую версию zapret
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ZapretCS
{
    partial class MainUi
    {
        static readonly string[,] MigSteps = {
            { "Мои сайты и группы", "list-general-user.txt" },
            { "Исключения", "list-exclude-user, ipset-exclude-user" },
            { "Игровой фильтр и автообновление", "game_filter, check_updates" },
            { "Сайты, вписанные в списки zapret", "list-general, list-google, исключения" },
            { "Сайты для теста", "targets.txt" },
            { "Меню выбора групп в тесте", "test zapret.ps1" }
        };
        string migFrom, migTo;
        bool migDemo, migRunning; int migIndex; string migPhase;
        DispatcherTimer migTimer;
        List<ZapretFolder> migFolders;
        class StepTag { public Ellipse Spin; public Grid Done; public Ellipse Circle; public TextBlock Mark, Title, Sub; public DropShadowEffect Glow; public Border Box; public RotateTransform Rot; public ScaleTransform DoneS; }

        // ---- шаги переноса ----
        static List<string> MigLines(string path) { return File.Exists(path) ? Regex.Split(File.ReadAllText(path, Encoding.UTF8), "\r?\n").ToList() : new List<string>(); }
        static void MigWrite(string path, IEnumerable<string> lines) { File.WriteAllText(path, string.Join("\r\n", lines), new UTF8Encoding(false)); }
        static void MigBackup(string path) { if (File.Exists(path)) File.Copy(path, path + ".bak", true); }
        int MigCopy(params string[] rels)
        {
            int n = 0;
            foreach (var f in rels)
            {
                var src = System.IO.Path.Combine(migFrom, f);
                if (!File.Exists(src)) continue;
                var dst = System.IO.Path.Combine(migTo, f);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst));
                File.Copy(src, dst, true); n++;
            }
            return n;
        }
        int MigMergeLists()
        {
            int added = 0;
            foreach (var f in new[] { "lists\\list-general.txt", "lists\\list-google.txt", "lists\\list-exclude.txt", "lists\\ipset-exclude.txt" })
            {
                string src = System.IO.Path.Combine(migFrom, f), dst = System.IO.Path.Combine(migTo, f);
                if (!File.Exists(src) || !File.Exists(dst)) continue;
                var newLines = MigLines(dst);
                var set = new HashSet<string>(newLines.Select(l => l.Trim()), StringComparer.OrdinalIgnoreCase);
                var missing = MigLines(src).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#") && !set.Contains(l)).Distinct().ToList();
                if (missing.Count > 0) { MigBackup(dst); MigWrite(dst, newLines.Where(l => l != "").Concat(missing).Concat(new[] { "" })); added += missing.Count; }
            }
            return added;
        }
        int MigTargets()
        {
            string srcT = System.IO.Path.Combine(migFrom, "utils\\targets.txt"), dstT = System.IO.Path.Combine(migTo, "utils\\targets.txt");
            if (!File.Exists(srcT) || !File.Exists(dstT)) return 0;
            var newT = MigLines(dstT);
            var haveKeys = new HashSet<string>(); var haveUrls = new HashSet<string>();
            var rx = new Regex("^\\s*(\\w+)\\s*=\\s*\"(.+)\"\\s*$");
            foreach (var l in newT) { var m = rx.Match(l); if (m.Success) { haveKeys.Add(m.Groups[1].Value); haveUrls.Add(m.Groups[2].Value.ToLowerInvariant()); } }
            string group = null, lastGroup = null; var add = new List<string>(); int count = 0;
            foreach (var l in MigLines(srcT))
            {
                var g = Regex.Match(l, @"^\s*###\s*(.+?)\s*$");
                if (g.Success) { group = g.Groups[1].Value; continue; }
                var m = rx.Match(l);
                if (!m.Success || haveKeys.Contains(m.Groups[1].Value) || haveUrls.Contains(m.Groups[2].Value.ToLowerInvariant())) continue;
                if (group != null && group != lastGroup) { add.Add(""); add.Add("### " + group); lastGroup = group; }
                add.Add(l.Trim()); count++;
            }
            if (count > 0)
            {
                MigBackup(dstT);
                while (newT.Count > 0 && newT[newT.Count - 1] == "") newT.RemoveAt(newT.Count - 1);
                MigWrite(dstT, newT.Concat(add).Concat(new[] { "" }));
            }
            return count;
        }
        string MigPatchTest()
        {
            var ps1 = System.IO.Path.Combine(migTo, "utils\\test zapret.ps1");
            if (!File.Exists(ps1)) return "none";
            string text = File.ReadAllText(ps1, Encoding.UTF8).Replace("\r\n", "\n");
            if (text.Contains("function Read-GroupSelection")) return "already";
            var lm = new Regex(@"^while \(\$true\) \{\n", RegexOptions.Multiline);
            string oldP = MigData.OldParse.Replace("\r\n", "\n"), newP = MigData.NewParse.Replace("\r\n", "\n"), func = MigData.GroupFunc.Replace("\r\n", "\n");
            if (!text.Contains(oldP) || !lm.IsMatch(text)) return "fail";
            MigBackup(ps1);
            text = text.Replace(oldP, newP);
            var m = lm.Match(text);
            text = text.Insert(m.Index, func);
            File.WriteAllText(ps1, text.Replace("\n", "\r\n"), new UTF8Encoding(true));
            return "ok";
        }
        static int CountSites(string path) { return File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8).Select(l => l.Trim()).Count(l => l.Length > 0 && !l.StartsWith("#") && l != ZapretCore.Placeholder) : 0; }

        string[] RunMigStep(int i)
        {
            if (migDemo)
            {
                var demo = new[] { new[] { "ok", "перенесено " + Plural(ReadUserGroups().Total) }, new[] { "ok", "файлов: 2" }, new[] { "ok", "настройки перенесены" },
                    new[] { "skip", "в новой версии уже всё есть" }, new[] { "ok", "добавлено: 5 сайтов" }, new[] { "ok", "меню вставлено" } };
                return demo[i];
            }
            int n;
            switch (i)
            {
                case 0: n = MigCopy("lists\\list-general-user.txt"); return n > 0 ? new[] { "ok", "перенесено " + Plural(CountSites(System.IO.Path.Combine(migTo, "lists\\list-general-user.txt"))) } : new[] { "skip", "нечего переносить" };
                case 1: n = MigCopy("lists\\list-exclude-user.txt", "lists\\ipset-exclude-user.txt"); return n > 0 ? new[] { "ok", "файлов: " + n } : new[] { "skip", "нечего переносить" };
                case 2: n = MigCopy("utils\\game_filter.enabled", "utils\\check_updates.enabled"); return n > 0 ? new[] { "ok", "настройки перенесены" } : new[] { "skip", "используются стандартные" };
                case 3: n = MigMergeLists(); return n > 0 ? new[] { "ok", "добавлено: " + Plural(n) } : new[] { "skip", "в новой версии уже всё есть" };
                case 4: n = MigTargets(); return n > 0 ? new[] { "ok", "добавлено: " + Plural(n) } : new[] { "skip", "в новой версии уже всё есть" };
                default:
                    switch (MigPatchTest())
                    {
                        case "ok": return new[] { "ok", "меню вставлено" };
                        case "already": return new[] { "skip", "уже есть" };
                        case "none": return new[] { "skip", "теста нет в новой версии" };
                        default: return new[] { "warn", "автор сильно изменил тест — меню не вставилось" };
                    }
            }
        }
        // все шаги сразу, без окна (для обновления zapret в один клик)
        public void MigrateNow(string from, string to)
        {
            migDemo = false; migFrom = from; migTo = to;
            for (int i = 0; i < MigSteps.GetLength(0); i++) { try { RunMigStep(i); } catch (Exception ex) { Log.Write("migrate: " + ex.Message); } }
        }

        // ---- окно переноса ----
        void WireMigrate()
        {
            migTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(380) };
            migTimer.Tick += (s, e) => MigTick();
            Btn("MigRun").Click += (s, e) => Safe(() =>
            {
                if ((Btn("MigRun").Content as string) == Translate.T("Готово"))
                {
                    CloseMigrate();
                    if (migDemo) return;
                    if (string.Equals(migTo, ZapretCore.Root, StringComparison.OrdinalIgnoreCase) && State == "on") { Toast("Перезапускаю zapret на новой версии…"); RestartZapret(); }
                    else if (!string.Equals(migTo, ZapretCore.Root, StringComparison.OrdinalIgnoreCase)) Toast("Перезапусти приложение, чтобы перейти на новую версию");
                }
                else StartMigrate();
            });
            Btn("MigClose").Click += (s, e) => CloseMigrate();
            El("MigDim").MouseLeftButtonUp += (s, e) => CloseMigrate();
            Btn("MigOpenFolder").Click += (s, e) => Safe(() => System.Diagnostics.Process.Start("explorer.exe", "\"" + HomeDir + "\""));
            Btn("MigDemo").Click += (s, e) => Safe(() =>
            {
                migDemo = true;
                El("MigEmpty").Visibility = Visibility.Collapsed; El("MigCards").Visibility = Visibility.Visible; El("MigRun").Visibility = Visibility.Visible; El("MigScroll").Visibility = Visibility.Visible;
                Btn("MigRun").IsEnabled = false;
                loading = true;
                var pairs = new[] { new { C = E<ComboBox>("MigFromBox"), T = "v" + ZapretCore.LocalVersion }, new { C = E<ComboBox>("MigToBox"), T = "v-next (демо)" } };
                foreach (var p in pairs) { p.C.Items.Clear(); p.C.Items.Add(new ComboBoxItem { Content = Translate.T(p.T) }); p.C.SelectedIndex = 0; }
                loading = false;
                FillMigSteps();
                After(700, StartMigrate);
            });
        }
        void FillMigSteps()
        {
            var steps = E<Panel>("MigSteps"); steps.Children.Clear();
            for (int i = 0; i < MigSteps.GetLength(0); i++) steps.Children.Add(StepRow(MigSteps[i, 0], MigSteps[i, 1], i));
        }
        FrameworkElement StepRow(string title, string sub, int i)
        {
            var b = new Border { CornerRadius = new CornerRadius(0), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 6), Background = Theme.B("#1E2129") };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var ic = new Grid { Width = 26, Height = 26, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            var ring = new Ellipse { Stroke = Theme.B("#3A3F4D"), StrokeThickness = 2 };
            var rot = new RotateTransform();
            var spin = new Ellipse { Stroke = Theme.B("#3DDC97"), StrokeThickness = 2.5, Opacity = 0, StrokeDashArray = new DoubleCollection(new double[] { 6, 6 }), StrokeDashCap = PenLineCap.Round, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = rot };
            var ds = new ScaleTransform(0.3, 0.3);
            var done = new Grid { Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = ds };
            var glow = new DropShadowEffect { Color = Theme.C("#3DDC97"), BlurRadius = 12, ShadowDepth = 0, Opacity = 0.8 };
            var dc = new Ellipse { Fill = Theme.B("#3DDC97"), Effect = glow };
            var mark = new TextBlock { Text = "✓", FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Theme.B("#0F1115"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            done.Children.Add(dc); done.Children.Add(mark);
            ic.Children.Add(ring); ic.Children.Add(spin); ic.Children.Add(done);
            g.Children.Add(ic);
            var tx = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var t1 = Txt(title, 13.5, "#9AA3B5"); t1.FontWeight = FontWeights.SemiBold;
            var t2 = Txt(sub, 11.5, "#5A6070"); t2.Margin = new Thickness(0, 2, 0, 0);
            tx.Children.Add(t1); tx.Children.Add(t2);
            Grid.SetColumn(tx, 1); g.Children.Add(tx);
            b.Child = g;
            b.Tag = new StepTag { Spin = spin, Done = done, Circle = dc, Mark = mark, Glow = glow, Title = t1, Sub = t2, Box = b, Rot = rot, DoneS = ds };
            b.Opacity = 0; var tt = new TranslateTransform(); b.RenderTransform = tt;
            Anim.D(b, Anim.OP, 1, 300, 0, begin: i * 60);
            Anim.D(tt, Anim.TX, 0, 380, -18, begin: i * 60);
            return b;
        }
        void StepRunning(StepTag t)
        {
            Anim.D(t.Spin, Anim.OP, 1, 150);
            Anim.D(t.Rot, Anim.Ang, 360, 800, 0, forever: true, noEase: true);
            t.Title.Foreground = Theme.B("#E6E8EE");
            Anim.Color(t.Box.Background, "#232833", 200);
        }
        void StepFinish(StepTag t, string[] r)
        {
            Anim.Stop(t.Rot, Anim.Ang); Anim.D(t.Spin, Anim.OP, 0, 120);
            string col = r[0] == "ok" ? "#3DDC97" : r[0] == "warn" ? "#FFB86B" : "#5A6070";
            t.Circle.Fill = Theme.B(col); t.Glow.Color = Theme.C(col);
            t.Mark.Text = r[0] == "ok" ? "✓" : r[0] == "warn" ? "!" : "–";
            if (r[0] == "skip") { t.Glow.Opacity = 0; t.Mark.Foreground = Theme.B("#C9CED8"); }
            Anim.D(t.Done, Anim.OP, 1, 200);
            Anim.D(t.DoneS, Anim.SX, 1, 450, 0.3, ease: Anim.Back()); Anim.D(t.DoneS, Anim.SY, 1, 450, 0.3, ease: Anim.Back());
            t.Sub.Text = Translate.T(r[1]);
            t.Sub.Foreground = Theme.B(r[0] == "ok" ? "#7FE8B8" : r[0] == "warn" ? "#FFB86B" : "#8A90A0");
            t.Title.Foreground = Theme.B(r[0] == "skip" ? "#9AA3B5" : "#E6E8EE");
            Anim.Color(t.Box.Background, "#1E2129", 300);
        }

        void OpenMigrate()
        {
            migDemo = false;
            migFolders = ZapretCore.FindFolders(GuiDir);
            El("MigDone").Visibility = Visibility.Collapsed; El("MigDone").Opacity = 0;
            Btn("MigRun").Content = Translate.T("Перенести"); Btn("MigRun").IsEnabled = true;
            var glow = (DropShadowEffect)Win.FindName("MigToGlow"); Anim.Stop(glow, DropShadowEffect.BlurRadiusProperty); glow.BlurRadius = 0;
            E<Panel>("MigSteps").Children.Clear();
            if (migFolders.Count < 2)
            {
                El("MigCards").Visibility = Visibility.Collapsed; El("MigEmpty").Visibility = Visibility.Visible; El("MigRun").Visibility = Visibility.Collapsed; El("MigScroll").Visibility = Visibility.Collapsed;
            }
            else
            {
                El("MigCards").Visibility = Visibility.Visible; El("MigEmpty").Visibility = Visibility.Collapsed; El("MigRun").Visibility = Visibility.Visible; El("MigScroll").Visibility = Visibility.Visible;
                loading = true;
                foreach (var cb in new[] { E<ComboBox>("MigFromBox"), E<ComboBox>("MigToBox") })
                {
                    cb.Items.Clear();
                    foreach (var f in migFolders) cb.Items.Add(new ComboBoxItem { Content = f.Ver > new Version(0, 0) ? "v" + f.Ver : f.Name, Tag = f.Path, ToolTip = f.Path });
                }
                E<ComboBox>("MigFromBox").SelectedIndex = migFolders.Count - 2;
                E<ComboBox>("MigToBox").SelectedIndex = migFolders.Count - 1;
                loading = false;
                FillMigSteps();
            }
            OpenOverlay("Mig");
            Anim.D(A("MigDotsT"), Anim.TX, 40, 1400, -40, forever: true, noEase: true);
            Translate.Tree(El("MigOverlay"));
        }
        void CloseMigrate()
        {
            if (migRunning) return;
            CloseOverlay("Mig");
            After(220, () => Anim.Stop(A("MigDotsT"), Anim.TX));
        }
        void StartMigrate()
        {
            if (!migDemo)
            {
                migFrom = ((ComboBoxItem)E<ComboBox>("MigFromBox").SelectedItem).Tag as string;
                migTo = ((ComboBoxItem)E<ComboBox>("MigToBox").SelectedItem).Tag as string;
                if (migFrom == null || migFrom == migTo) { Toast("Выбери разные версии: откуда и куда", true); return; }
            }
            migRunning = true; migIndex = 0; migPhase = "run";
            Btn("MigRun").IsEnabled = false; Btn("MigRun").Content = Translate.T("Переношу…");
            E<ComboBox>("MigFromBox").IsEnabled = false; E<ComboBox>("MigToBox").IsEnabled = false;
            Anim.D(A("MigDotsT"), Anim.TX, 40, 500, -40, forever: true, noEase: true);
            migTimer.Start();
        }
        void MigTick()
        {
            try
            {
                var rows = E<Panel>("MigSteps").Children;
                if (migIndex >= rows.Count)
                {
                    migTimer.Stop(); migRunning = false;
                    Btn("MigRun").Content = Translate.T("Готово"); Btn("MigRun").IsEnabled = true;
                    E<ComboBox>("MigFromBox").IsEnabled = true; E<ComboBox>("MigToBox").IsEnabled = true;
                    Anim.D(A("MigDotsT"), Anim.TX, 40, 1400, -40, forever: true, noEase: true);
                    Anim.D((DropShadowEffect)Win.FindName("MigToGlow"), DropShadowEffect.BlurRadiusProperty, 34, 600, 0);
                    var md = Tb("MigDone"); md.Visibility = Visibility.Visible; Anim.D(md, Anim.OP, 1, 400, 0);
                    md.Text = Translate.T(migDemo ? "Это было демо — файлы не менялись. Так всё пройдёт при настоящем переносе." : "Готово! Оригиналы файлов автора сохранены как .bak");
                    if (!migDemo && string.Equals(migTo, ZapretCore.Root, StringComparison.OrdinalIgnoreCase)) RefreshSites();
                    return;
                }
                var tag = (StepTag)((FrameworkElement)rows[migIndex]).Tag;
                if (migPhase == "run") { StepRunning(tag); migPhase = "finish"; return; }
                StepFinish(tag, RunMigStep(migIndex));
                migIndex++; migPhase = "run";
            }
            catch (Exception ex)
            {
                migTimer.Stop(); migRunning = false;
                Log.Write(ex.ToString()); Toast("Ошибка переноса: " + ex.Message, true);
                Btn("MigRun").Content = Translate.T("Перенести"); Btn("MigRun").IsEnabled = true;
            }
        }
    }
}
