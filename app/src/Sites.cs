// Zapret (C#) — «Мои сайты»: группы, добавление и удаление, «Поделиться» и «Вставить»
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace ZapretCS
{
    // упорядоченный набор групп: имя -> список сайтов
    class Groups
    {
        public readonly List<string> Keys = new List<string>();
        readonly Dictionary<string, List<string>> d = new Dictionary<string, List<string>>();
        public bool Has(string k) { return d.ContainsKey(k); }
        public List<string> this[string k] { get { if (!d.ContainsKey(k)) { d[k] = new List<string>(); Keys.Add(k); } return d[k]; } }
        public void Remove(string k) { d.Remove(k); Keys.Remove(k); }
        public int Count { get { return Keys.Count; } }
        public int Total { get { return d.Values.Sum(l => l.Count); } }
    }

    partial class MainUi
    {
        readonly Dictionary<string, bool> expanded = new Dictionary<string, bool>();
        bool rowsShown, animateRows, pendingApply;

        static readonly string[,] BuiltinRules = {
            { "DNS-серверы", @"(^|\.)dns\.|^doh\.|-dns\.|freedns|^dns\." },
            { "Discord", @"discord|^dis\.gd$" },
            { "YouTube", @"youtu|ytimg|ggpht|googlevideo|^yt[0-9-]|jnn-pa" },
            { "Google", @"google|gstatic" },
            { "Cloudflare", @"cloudflare|cloudfront|encryptedsni" },
            { "Twitch и смайлы", @"frankerfacez|ffzap|betterttv|7tv|live-video" }
        };
        static readonly Dictionary<string, string> GroupColors = new Dictionary<string, string> {
            { "DNS-серверы", "#F5B44A" }, { "Discord", "#8C9BFF" }, { "YouTube", "#FF5E62" }, { "Google", "#4DA3FF" },
            { "Cloudflare", "#FF9A4D" }, { "Twitch и смайлы", "#B57BFF" }, { "Прочее", "#9AA3B5" }
        };
        static readonly string[] UserPalette = { "#3DDC97", "#5ED3F3", "#FFB86B", "#FF7AB6", "#B69CFF", "#7EE081", "#FFD166", "#6EC1FF" };

        public static string GroupColor(string name, bool builtin)
        {
            string c; if (builtin && GroupColors.TryGetValue(name, out c)) return c;
            int h = 0; foreach (char ch in name) h = (h * 31 + ch) % 100003;
            return UserPalette[h % UserPalette.Length];
        }
        public static string Plural(int n)
        {
            if (Translate.En) return n == 1 ? n + " site" : n + " sites";
            int m10 = n % 10, m100 = n % 100;
            if (m10 == 1 && m100 != 11) return n + " сайт";
            if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return n + " сайта";
            return n + " сайтов";
        }

        // ---- чтение и запись list-general-user.txt (группы — строки "# @group Имя") ----
        static string BaseDomain(string dom)
        {
            var p = dom.Trim('.').Split('.');
            return p.Length <= 2 ? string.Join(".", p) : p[p.Length - 2] + "." + p[p.Length - 1];
        }
        static string FindGroupFor(Groups g, string dom)
        {
            string b = BaseDomain(dom);
            foreach (var k in g.Keys) foreach (var d in g[k]) if (BaseDomain(d) == b) return k;
            // похожие домены одного сервиса — в одну группу
            string lbl = b.Split('.')[0];
            foreach (var k in g.Keys)
                foreach (var d in g[k])
                {
                    string o = BaseDomain(d).Split('.')[0];
                    if (o.Length >= 4 && lbl.Length >= 4 && (lbl.StartsWith(o) || o.StartsWith(lbl))) return k;
                }
            string name = lbl;
            return name.Length > 0 ? char.ToUpper(name[0]) + name.Substring(1) : name;
        }
        public static Groups ReadUserGroups()
        {
            var g = new Groups();
            if (!File.Exists(ZapretCore.UserListFile)) return g;
            string cur = null;
            foreach (var l in File.ReadAllLines(ZapretCore.UserListFile, Encoding.UTF8))
            {
                string s = l.Trim();
                var m = Regex.Match(s, @"^#\s*@group\s+(.+)$");
                if (m.Success) { cur = m.Groups[1].Value.Trim(); var _ = g[cur]; continue; }
                if (s.Length == 0 || s.StartsWith("#") || s == ZapretCore.Placeholder) continue;
                string grp = cur ?? FindGroupFor(g, s);
                if (!g[grp].Contains(s)) g[grp].Add(s);
            }
            return g;
        }
        public static void WriteUserGroups(Groups g)
        {
            var lines = new List<string> { "# Never leave this file empty", ZapretCore.Placeholder };
            foreach (var k in g.Keys)
            {
                if (g[k].Count == 0) continue;
                lines.Add("# @group " + k);
                lines.AddRange(g[k]);
            }
            File.WriteAllText(ZapretCore.UserListFile, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
        }
        static Groups BuiltinGroups()
        {
            var g = new Groups();
            for (int i = 0; i < BuiltinRules.GetLength(0); i++) { var _ = g[BuiltinRules[i, 0]]; }
            var other = g["Прочее"];
            foreach (var f in new[] { "list-general.txt", "list-google.txt" })
            {
                var p = System.IO.Path.Combine(ZapretCore.Lists, f);
                if (!File.Exists(p)) continue;
                foreach (var l in File.ReadAllLines(p, Encoding.UTF8))
                {
                    string s = l.Trim().TrimStart('^');
                    if (s.Length == 0 || s.StartsWith("#")) continue;
                    string grp = "Прочее";
                    for (int i = 0; i < BuiltinRules.GetLength(0); i++) if (Regex.IsMatch(s, BuiltinRules[i, 1])) { grp = BuiltinRules[i, 0]; break; }
                    if (!g[grp].Contains(s)) g[grp].Add(s);
                }
            }
            foreach (var k in g.Keys.ToList()) if (g[k].Count == 0) g.Remove(k);
            return g;
        }
        static string NormalizeDomain(string raw)
        {
            string dom = raw.Trim().ToLowerInvariant();
            dom = Regex.Replace(dom, "^[a-z]+://", "");
            dom = Regex.Replace(dom, "[/?#].*$", "");
            dom = Regex.Replace(dom, @":\d+$", "");
            dom = Regex.Replace(dom, @"^www\.", "");
            return Regex.IsMatch(dom, @"^([a-z0-9а-яё-]+\.)+[a-zа-яё0-9-]{2,}$") ? dom : null;
        }

        // ---- список на экране ----
        void WireSites()
        {
            var box = E<TextBox>("DomainBox");
            box.TextChanged += (s, e) => El("DomainHint").Visibility = box.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
            box.KeyDown += (s, e) => { if (e.Key == Key.Return) Safe(() => AddSite(box.Text, null)); };
            Btn("AddBtn").Click += (s, e) => Safe(() => AddSite(box.Text, null));
            Btn("ApplyBtn").Click += (s, e) => Safe(() => { HideApply(); RestartZapret(); });
            WireShare();
            RefreshSites();
        }

        TextBlock Txt(string text, double size, string color)
        {
            return new TextBlock { Text = Translate.T(text), FontSize = size, Foreground = Theme.B(color), VerticalAlignment = VerticalAlignment.Center };
        }

        void RefreshSites()
        {
            var panel = E<Panel>("SitesPanel");
            panel.Children.Clear();
            animateRows = !rowsShown; rowsShown = true;
            var ug = ReadUserGroups();
            Tb("SitesCount").Text = Plural(ug.Total);
            panel.Children.Add(SectionLabel("МОИ"));
            if (ug.Count == 0)
                panel.Children.Add(new TextBlock { Text = Translate.T("Пока пусто. Добавь сайт, который не открывается."), Foreground = Theme.B("#5A6070"), Margin = new Thickness(4, 2, 0, 8), TextWrapping = TextWrapping.Wrap });
            int idx = 0;
            foreach (var k in ug.Keys) panel.Children.Add(GroupRow("u:" + k, k, ug[k], true, idx++));
            var bg = BuiltinGroups();
            if (bg.Count > 0)
            {
                panel.Children.Add(SectionLabel("ВСТРОЕННЫЕ В ZAPRET"));
                foreach (var k in bg.Keys) panel.Children.Add(GroupRow("b:" + k, k, bg[k], false, idx++));
            }
        }
        TextBlock SectionLabel(string text)
        {
            var t = Txt(text, 11, "#5A6070"); t.FontWeight = FontWeights.SemiBold; t.Margin = new Thickness(4, 10, 0, 6); return t;
        }

        FrameworkElement GroupRow(string key, string name, List<string> domains, bool editable, int index)
        {
            string color = GroupColor(name, !editable);
            var wrap = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
            var head = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 8, 9), Cursor = Cursors.Hand, Background = Theme.B("#20232B"), BorderThickness = new Thickness(3, 0, 0, 0), BorderBrush = Brushes.Transparent };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 11, 0), VerticalAlignment = VerticalAlignment.Center, Fill = Theme.B(color) };
            dot.Effect = new DropShadowEffect { Color = Theme.C(color), BlurRadius = 10, ShadowDepth = 0, Opacity = 0.9 };
            var nm = Txt(name, 13.5, "#E6E8EE"); nm.FontWeight = FontWeights.SemiBold; nm.TextTrimming = TextTrimming.CharacterEllipsis; Grid.SetColumn(nm, 1);
            var cnt = Txt(Plural(domains.Count), 12, "#8A90A0"); cnt.Margin = new Thickness(8, 0, 8, 0); Grid.SetColumn(cnt, 2);
            var dots = new Border { Width = 30, Height = 24, CornerRadius = new CornerRadius(7) };
            var dt = Txt("⋯", 16, "#8A90A0"); dt.HorizontalAlignment = HorizontalAlignment.Center; dt.Margin = new Thickness(0, -3, 0, 0);
            dots.Child = dt; Grid.SetColumn(dots, 3);
            g.Children.Add(dot); g.Children.Add(nm); g.Children.Add(cnt); g.Children.Add(dots);
            head.Child = g;

            // раскрывающийся список сайтов
            var body = new StackPanel { Margin = new Thickness(14, 4, 0, 6), RenderTransform = new TranslateTransform() };
            foreach (var d in domains)
            {
                var row = new DockPanel();
                // рамка строки: подсветка при наведении и полоска «вставить сюда» при перетаскивании
                var cell = new Border { CornerRadius = new CornerRadius(7), Background = Brushes.Transparent, BorderThickness = new Thickness(0, 2, 0, 2), BorderBrush = Brushes.Transparent, Padding = new Thickness(0, 0, 0, 0), Child = row };
                if (editable) MakeDraggable(cell, row, name, d);
                if (editable)
                {
                    var del = new Button { Style = (Style)Win.FindResource("DelBtn"), ToolTip = Translate.T("Удалить") };
                    string dom = d, grp = name;
                    del.Click += (s, e) => Safe(() =>
                    {
                        var gg = ReadUserGroups(); if (gg.Has(grp)) gg[grp].Remove(dom);
                        WriteUserGroups(gg); RefreshSites(); Toast("Удалён " + dom); MarkChanged();
                    });
                    DockPanel.SetDock(del, Dock.Right); row.Children.Add(del);
                }
                var tx = Txt(d, 12.5, "#B8BDC9"); tx.Margin = new Thickness(6, 3, 0, 3); tx.TextTrimming = TextTrimming.CharacterEllipsis;
                row.Children.Add(tx);
                if (d == lastMoved && editable) FlashMoved(cell);
                body.Children.Add(cell);
            }
            if (!editable)
            {
                var ro = Txt("встроенный список zapret · только просмотр", 11, "#5A6070"); ro.Margin = new Thickness(6, 6, 0, 2);
                body.Children.Add(ro);
            }
            else
            {
                var foot = new Grid { Margin = new Thickness(0, 6, 0, 2) };
                foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var bx = new Border { Background = Theme.B("#22262F"), CornerRadius = new CornerRadius(8), BorderBrush = Theme.B("#2E3340"), BorderThickness = new Thickness(1), Height = 32 };
                var tb = new TextBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Theme.B("#E6E8EE"), CaretBrush = Theme.B("#3DDC97"), Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5, ToolTip = Translate.T("Добавить сайт в группу " + name) };
                string grp = name;
                tb.KeyDown += (s, e) => { if (e.Key == Key.Return) Safe(() => AddSite(tb.Text, grp)); };
                bx.Child = tb;
                var add = new Button { Style = (Style)Win.FindResource("AccentBtn"), Content = "+", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0) };
                add.Click += (s, e) => Safe(() => AddSite(tb.Text, grp));
                Grid.SetColumn(add, 1);
                foot.Children.Add(bx); foot.Children.Add(add);
                body.Children.Add(foot);
                var rm = Txt("Удалить группу", 11.5, "#C9606F"); rm.Cursor = Cursors.Hand; rm.Margin = new Thickness(2, 8, 0, 0); rm.HorizontalAlignment = HorizontalAlignment.Left;
                int count = domains.Count;
                rm.MouseLeftButtonUp += (s, e) => Safe(() =>
                {
                    var r = MessageBox.Show(Translate.T("Удалить группу «" + grp + "» (" + Plural(count) + ")?"), "Zapret", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (r != MessageBoxResult.Yes) return;
                    var gg = ReadUserGroups(); gg.Remove(grp); WriteUserGroups(gg);
                    expanded.Remove("u:" + grp);
                    RefreshSites(); Toast("Группа " + grp + " удалена"); MarkChanged();
                });
                body.Children.Add(rm);
            }

            bool open; expanded.TryGetValue(key, out open);
            body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (open) { dots.Background = Theme.B("#2E3340"); dt.Foreground = Theme.B("#3DDC97"); head.BorderBrush = Theme.B(color); }
            head.MouseLeftButtonUp += (s, e) => Safe(() =>
            {
                bool o; expanded.TryGetValue(key, out o); o = !o; expanded[key] = o;
                if (o)
                {
                    body.Visibility = Visibility.Visible;
                    Anim.D((TranslateTransform)body.RenderTransform, Anim.TY, 0, 260, -8);
                    dots.Background = Theme.B("#2E3340"); dt.Foreground = Theme.B(color); head.BorderBrush = Theme.B(color);
                    int i = 0;
                    foreach (UIElement ch in body.Children) { ch.Opacity = 0; Anim.D(ch, Anim.OP, 1, 220, 0, begin: Math.Min(i * 18, 300)); i++; }
                }
                else
                {
                    body.Visibility = Visibility.Collapsed;
                    dots.Background = Brushes.Transparent; dt.Foreground = Theme.B("#8A90A0"); head.BorderBrush = Brushes.Transparent;
                }
            });
            if (editable && name == lastMovedGroup)
            {
                var fb = new SolidColorBrush(Theme.C("#2D5A47")); head.Background = fb;
                fb.BeginAnimation(SolidColorBrush.ColorProperty, new System.Windows.Media.Animation.ColorAnimation { To = Theme.C("#20232B"), BeginTime = TimeSpan.FromMilliseconds(250), Duration = new Duration(TimeSpan.FromMilliseconds(900)), EasingFunction = Anim.Ease() });
            }
            head.MouseEnter += (s, e) => Anim.Color(head.Background, "#2A2E38", 160);
            head.MouseLeave += (s, e) => Anim.Color(head.Background, "#20232B", 220);

            if (animateRows)
            {
                // плавное появление строк по очереди
                wrap.Opacity = 0; var tt = new TranslateTransform(); wrap.RenderTransform = tt;
                Anim.D(wrap, Anim.OP, 1, 320, 0, begin: index * 40);
                Anim.D(tt, Anim.TY, 0, 380, 12, begin: index * 40);
            }
            var topLine = new Border { Height = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(6, 0, 6, 2), Background = Brushes.Transparent };
            var botLine = new Border { Height = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(6, 2, 6, 0), Background = Brushes.Transparent };
            if (editable) MakeGroupDropTarget(head, body, topLine, botLine, name, key, color);
            wrap.Children.Add(topLine); wrap.Children.Add(head); wrap.Children.Add(body); wrap.Children.Add(botLine);
            return wrap;
        }

        // ================= перетаскивание сайтов =================
        const string DragFmt = "ZapretSite";
        string lastMoved;
        Point dragStart; bool dragArmed;

        void MakeDraggable(Border cell, DockPanel row, string grp, string dom)
        {
            var grip = Txt("⋮⋮", 12, "#5A6070");
            grip.Margin = new Thickness(2, 0, 0, 1); grip.Opacity = 0; grip.ToolTip = Translate.T("Перетащи, чтобы переместить");
            DockPanel.SetDock(grip, Dock.Left);
            row.Children.Insert(0, grip);
            cell.Cursor = Cursors.SizeAll;
            cell.MouseEnter += (s, e) => { if (!dragArmed || Mouse.LeftButton != MouseButtonState.Pressed) { Anim.Color(cell.Background, "#2A2E38", 140); grip.Opacity = 1; } };
            cell.MouseLeave += (s, e) => { Anim.Color(cell.Background, "#00000000", 200); grip.Opacity = 0; };
            cell.Background = new SolidColorBrush(Colors.Transparent);
            cell.PreviewMouseLeftButtonDown += (s, e) =>
            {
                // кнопку «удалить» не трогаем
                var src = e.OriginalSource as DependencyObject;
                while (src != null && src != cell) { if (src is Button) { dragArmed = false; return; } src = VisualTreeHelper.GetParent(src); }
                dragStart = e.GetPosition(Win); dragArmed = true;
            };
            cell.PreviewMouseLeftButtonUp += (s, e) => dragArmed = false;
            cell.MouseMove += (s, e) =>
            {
                if (!dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(Win);
                if (Math.Abs(p.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                dragArmed = false;
                cell.Opacity = 0.35;
                DoDrag(cell, new DataObject(DragFmt, grp + "\n" + dom));
                cell.Opacity = 1;
            };

            // строка — тоже место, куда можно бросить: выше или ниже неё
            cell.AllowDrop = true;
            cell.DragOver += (s, e) =>
            {
                e.Handled = true;
                if (!e.Data.GetDataPresent(DragFmt)) { e.Effects = DragDropEffects.None; return; }
                e.Effects = DragDropEffects.Move;
                bool below = e.GetPosition(cell).Y > cell.ActualHeight / 2;
                cell.BorderBrush = Theme.B(Theme.Accent);
                cell.BorderThickness = below ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0);
            };
            cell.DragLeave += (s, e) => { cell.BorderBrush = Brushes.Transparent; cell.BorderThickness = new Thickness(0, 2, 0, 2); };
            cell.Drop += (s, e) => Safe(() =>
            {
                e.Handled = true;
                cell.BorderBrush = Brushes.Transparent; cell.BorderThickness = new Thickness(0, 2, 0, 2);
                if (!e.Data.GetDataPresent(DragFmt)) return;
                bool below = e.GetPosition(cell).Y > cell.ActualHeight / 2;
                var parts = ((string)e.Data.GetData(DragFmt)).Split('\n');
                MoveSite(parts[0], parts[1], grp, dom, below);
            });
        }

        const string GroupFmt = "ZapretGroup";

        void MakeGroupDropTarget(Border head, FrameworkElement body, Border topLine, Border botLine, string grp, string key, string color)
        {
            // сама группа перетаскивается за заголовок — так меняется порядок групп
            Point start = new Point(); bool armed = false;
            head.PreviewMouseLeftButtonDown += (s, e) => { start = e.GetPosition(Win); armed = true; };
            head.PreviewMouseLeftButtonUp += (s, e) => armed = false;
            head.MouseMove += (s, e) =>
            {
                if (!armed || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(Win);
                if (Math.Abs(p.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                armed = false;
                var wrap = head.Parent as UIElement;
                if (wrap != null) wrap.Opacity = 0.35;
                DoDrag(head, new DataObject(GroupFmt, grp));
                if (wrap != null) wrap.Opacity = 1;
            };

            head.AllowDrop = true;
            System.Windows.Threading.DispatcherTimer openTimer = null;
            Action clearLines = () => { topLine.Background = Brushes.Transparent; botLine.Background = Brushes.Transparent; };
            head.DragEnter += (s, e) =>
            {
                if (!e.Data.GetDataPresent(DragFmt)) return;
                head.BorderBrush = Theme.B(color); Anim.Color(head.Background, "#2A2E38", 120);
                // подержал сайт над закрытой группой — она раскрывается, чтобы бросить в нужное место
                bool o; expanded.TryGetValue(key, out o);
                if (!o)
                {
                    openTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
                    openTimer.Tick += (s2, e2) =>
                    {
                        openTimer.Stop();
                        expanded[key] = true;
                        body.Visibility = Visibility.Visible;
                        Anim.D((TranslateTransform)body.RenderTransform, Anim.TY, 0, 220, -6);
                    };
                    openTimer.Start();
                }
            };
            head.DragOver += (s, e) =>
            {
                e.Handled = true;
                if (e.Data.GetDataPresent(GroupFmt))
                {
                    if ((string)e.Data.GetData(GroupFmt) == grp) { e.Effects = DragDropEffects.None; clearLines(); return; }
                    e.Effects = DragDropEffects.Move;
                    bool below = e.GetPosition(head).Y > head.ActualHeight / 2;
                    topLine.Background = below ? Brushes.Transparent : Theme.B(Theme.Accent);
                    botLine.Background = below ? Theme.B(Theme.Accent) : Brushes.Transparent;
                    return;
                }
                e.Effects = e.Data.GetDataPresent(DragFmt) ? DragDropEffects.Move : DragDropEffects.None;
            };
            Action reset = () =>
            {
                if (openTimer != null) openTimer.Stop();
                clearLines();
                bool o; expanded.TryGetValue(key, out o);
                head.BorderBrush = o ? Theme.B(color) : Brushes.Transparent;
                Anim.Color(head.Background, "#20232B", 200);
            };
            head.DragLeave += (s, e) => reset();
            head.Drop += (s, e) => Safe(() =>
            {
                e.Handled = true;
                bool below = e.GetPosition(head).Y > head.ActualHeight / 2;
                reset();
                if (e.Data.GetDataPresent(GroupFmt)) { MoveGroup((string)e.Data.GetData(GroupFmt), grp, below); return; }
                if (!e.Data.GetDataPresent(DragFmt)) return;
                var parts = ((string)e.Data.GetData(DragFmt)).Split('\n');
                MoveSite(parts[0], parts[1], grp, null, true);
            });
        }

        void MoveGroup(string src, string near, bool below)
        {
            if (src == near) return;
            var g = ReadUserGroups();
            if (!g.Has(src) || !g.Has(near)) { RefreshSites(); return; }
            g.Keys.Remove(src);
            int i = g.Keys.IndexOf(near);
            g.Keys.Insert(below ? i + 1 : i, src);
            WriteUserGroups(g);
            lastMovedGroup = src;
            RefreshSites();
            lastMovedGroup = null;
        }
        string lastMovedGroup;

        // ---- перетаскивание с прокруткой: колесо мыши и автопрокрутка у краёв списка ----
        [StructLayout(LayoutKind.Sequential)] struct MSLL { public int x, y; public uint mouseData, flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] struct PT { public int x, y; }
        delegate IntPtr LLProc(int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, LLProc fn, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out PT p);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
        LLProc hookProc;
        ScrollViewer dragScroll;

        ScrollViewer SitesScroll()
        {
            DependencyObject d = E<Panel>("SitesPanel");
            while (d != null && !(d is ScrollViewer)) d = VisualTreeHelper.GetParent(d);
            return d as ScrollViewer;
        }

        void DoDrag(DependencyObject src, DataObject data)
        {
            dragScroll = SitesScroll();
            IntPtr hook = IntPtr.Zero;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            try
            {
                // во время перетаскивания Windows не отдаёт окну колесо мыши — ловим его сами
                hookProc = (code, w, l) =>
                {
                    if (code >= 0 && (int)w == 0x020A && dragScroll != null)
                    {
                        var m = (MSLL)Marshal.PtrToStructure(l, typeof(MSLL));
                        int delta = (short)((m.mouseData >> 16) & 0xFFFF);
                        dragScroll.ScrollToVerticalOffset(dragScroll.VerticalOffset - delta * 0.8);
                        return (IntPtr)1;
                    }
                    return CallNextHookEx(IntPtr.Zero, code, w, l);
                };
                hook = SetWindowsHookEx(14, hookProc, GetModuleHandle(null), 0);
                // у верхнего и нижнего края список прокручивается сам
                timer.Tick += (s, e) =>
                {
                    if (dragScroll == null) return;
                    PT pt; if (!GetCursorPos(out pt)) return;
                    Point p;
                    try { p = dragScroll.PointFromScreen(new Point(pt.x, pt.y)); } catch { return; }
                    if (p.X < 0 || p.X > dragScroll.ActualWidth) return;
                    double edge = 42, h = dragScroll.ActualHeight, v = 0;
                    if (p.Y < edge && p.Y > -30) v = -(edge - p.Y) * 0.45;
                    else if (p.Y > h - edge && p.Y < h + 30) v = (p.Y - (h - edge)) * 0.45;
                    if (v != 0) dragScroll.ScrollToVerticalOffset(dragScroll.VerticalOffset + v);
                };
                timer.Start();
                DragDrop.DoDragDrop(src, data, DragDropEffects.Move);
            }
            catch (Exception ex) { Log.Write("drag: " + ex.Message); }
            finally
            {
                timer.Stop();
                if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
                dragScroll = null;
            }
        }

        // переместить сайт: в группу dstGrp рядом с сайтом near (выше/ниже) или в конец группы
        void MoveSite(string srcGrp, string dom, string dstGrp, string near, bool below)
        {
            if (near == dom) return;
            var g = ReadUserGroups();
            if (!g.Has(srcGrp) || !g[srcGrp].Contains(dom)) { RefreshSites(); return; }
            g[srcGrp].Remove(dom);
            var list = g[dstGrp];
            int i = near != null ? list.IndexOf(near) : -1;
            if (i < 0) list.Add(dom); else list.Insert(below ? i + 1 : i, dom);
            WriteUserGroups(g);
            expanded["u:" + dstGrp] = true;
            lastMoved = dom;
            RefreshSites();
            lastMoved = null;
            if (srcGrp != dstGrp) Toast(dom + " → " + dstGrp);
        }

        void FlashMoved(Border cell)
        {
            var b = new SolidColorBrush(Theme.C("#2D5A47"));
            cell.Background = b;
            var a = new System.Windows.Media.Animation.ColorAnimation { To = Colors.Transparent, BeginTime = TimeSpan.FromMilliseconds(250), Duration = new Duration(TimeSpan.FromMilliseconds(900)), EasingFunction = Anim.Ease() };
            b.BeginAnimation(SolidColorBrush.ColorProperty, a);
        }

        void AddSite(string raw, string group)
        {
            raw = (raw ?? "").Trim();
            if (raw.Length == 0) return;
            string dom = NormalizeDomain(raw);
            if (dom == null) { Toast("«" + raw + "» не похоже на адрес сайта", true); return; }
            var g = ReadUserGroups();
            foreach (var k in g.Keys) if (g[k].Contains(dom)) { Toast(dom + " уже есть в группе " + k); E<TextBox>("DomainBox").Text = ""; return; }
            if (group == null) group = FindGroupFor(g, dom);
            g[group].Add(dom);
            WriteUserGroups(g);
            expanded["u:" + group] = true;
            E<TextBox>("DomainBox").Text = "";
            RefreshSites();
            Toast("Добавлен " + dom + " → " + group);
            MarkChanged();
        }

        // после изменения списков zapret нужно перезапустить — показываем плашку
        void MarkChanged()
        {
            if (State != "on" || pendingApply) return;
            pendingApply = true;
            var b = El("ApplyBar");
            b.Visibility = Visibility.Visible;
            Anim.D(b, Anim.OP, 1, 300, 0);
            var tt = new TranslateTransform(); b.RenderTransform = tt;
            Anim.D(tt, Anim.TY, 0, 350, 14);
        }

        // ================= Поделиться / Вставить =================
        string shareMode;
        readonly List<CheckBox> shareBoxes = new List<CheckBox>();
        int parseSkipped;

        static string TextFromGroups(Groups g)
        {
            var lines = new List<string> { "# Zapret · список сайтов · by COFFIOK", "# Скопируй этот текст и нажми в Zapret «Вставить»" };
            foreach (var k in g.Keys) { lines.Add(""); lines.Add("# @group " + k); lines.AddRange(g[k]); }
            return string.Join("\r\n", lines) + "\r\n";
        }
        Groups ParseSites(string text)
        {
            var mine = ReadUserGroups();
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in mine.Keys) foreach (var d in mine[k]) have.Add(d);
            var outG = new Groups(); string cur = null; parseSkipped = 0;
            foreach (var raw in Regex.Split(text, "\r?\n"))
            {
                string s = raw.Trim();
                if (s.Length == 0) continue;
                var m = Regex.Match(s, @"^#\s*@group\s+(.+)$");
                if (m.Success) { cur = m.Groups[1].Value.Trim(); continue; }
                if (s.StartsWith("#") || s == ZapretCore.Placeholder) continue;
                foreach (var tok in Regex.Split(s, @"[\s,;]+"))
                {
                    string dom = NormalizeDomain(tok);
                    if (dom == null) continue;
                    if (have.Contains(dom)) { parseSkipped++; continue; }
                    string grp = cur ?? FindGroupFor(mine, dom);
                    if (!outG[grp].Contains(dom)) outG[grp].Add(dom);
                    have.Add(dom);
                }
            }
            return outG;
        }

        CheckBox ShareCheck(string text, double size)
        {
            return new CheckBox { Style = (Style)Win.FindResource("Check"), IsChecked = true, Content = new TextBlock { Text = text, FontSize = size } };
        }
        void OpenShare(string mode, Groups groups, bool open = true)
        {
            shareMode = mode;
            ShareView("list");
            var list = E<Panel>("ShareList"); list.Children.Clear(); shareBoxes.Clear();
            if (mode == "share")
            {
                Tb("ShareTitle").Text = Translate.T("Поделиться сайтами");
                Tb("ShareSub").Text = Translate.T("Отметь, чем поделиться, — получишь короткий код. Друг введёт его в своём Zapret.");
                Btn("ShareGo").Content = Translate.T("Получить код");
                var alt = Btn("ShareAlt"); alt.Content = Translate.T("Скопировать текстом"); alt.Visibility = Visibility.Visible; alt.IsEnabled = true;
            }
            else
            {
                Tb("ShareTitle").Text = Translate.T("Добавить сайты");
                Tb("ShareSub").Text = Translate.T("Вот что тебе прислали. Отметь, что добавить к себе.");
                Btn("ShareGo").Content = Translate.T("Добавить");
            }
            int i = 0;
            foreach (var k in groups.Keys)
            {
                var wrap = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                var gc = ShareCheck(k, 13.5); ((TextBlock)gc.Content).FontWeight = FontWeights.SemiBold;
                var dot = new Ellipse { Width = 7, Height = 7, Fill = Theme.B(GroupColor(k, false)), Margin = new Thickness(10, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                head.Children.Add(gc); head.Children.Add(dot); head.Children.Add(Txt(Plural(groups[k].Count), 11.5, "#8A90A0"));
                var sites = new WrapPanel { Margin = new Thickness(27, 6, 0, 0) };
                var kids = new List<CheckBox>();
                foreach (var d in groups[k])
                {
                    var sc = ShareCheck(d, 12.5); sc.Margin = new Thickness(0, 0, 16, 6); ((TextBlock)sc.Content).Foreground = Theme.B("#B8BDC9");
                    sc.Tag = new[] { k, d };
                    sc.Click += (s, e) => { int on = kids.Count(c => c.IsChecked == true); gc.IsChecked = on == 0 ? (bool?)false : on == kids.Count ? (bool?)true : null; UpdateShareCount(); };
                    sites.Children.Add(sc); kids.Add(sc); shareBoxes.Add(sc);
                }
                gc.Click += (s, e) => { bool v = gc.IsChecked != false; gc.IsChecked = v; foreach (var c in kids) c.IsChecked = v; UpdateShareCount(); };
                wrap.Children.Add(head); wrap.Children.Add(sites);
                wrap.Opacity = 0; var tt = new TranslateTransform(); wrap.RenderTransform = tt;
                Anim.D(wrap, Anim.OP, 1, 280, 0, begin: Math.Min(i, 10) * 45);
                Anim.D(tt, Anim.TY, 0, 320, 10, begin: Math.Min(i, 10) * 45);
                list.Children.Add(wrap); i++;
            }
            UpdateShareCount();
            if (open) OpenOverlay("Share");
        }
        void UpdateShareCount()
        {
            int n = shareBoxes.Count(c => c.IsChecked == true);
            Tb("ShareCount").Text = Translate.T("Выбрано: " + Plural(n) + " из " + shareBoxes.Count);
            Btn("ShareGo").IsEnabled = n > 0 && !shareBusy;
            if (shareMode == "share") Btn("ShareAlt").IsEnabled = n > 0;
            Tb("ShareAll").Text = Translate.T(n == shareBoxes.Count ? "Снять все" : "Выбрать все");
        }
        Groups ShareSelection()
        {
            var sel = new Groups();
            foreach (var c in shareBoxes) if (c.IsChecked == true) { var t = (string[])c.Tag; sel[t[0]].Add(t[1]); }
            return sel;
        }
        // «Ввести код»: если в буфере список сайтов — сразу показываем его, иначе окно ввода кода
        void PasteList(bool fromEnter)
        {
            string txt = null;
            try { txt = Clipboard.ContainsText() ? Clipboard.GetText() : null; } catch { }
            string code = NormCode(txt);
            if (!fromEnter && code != null) { OpenEnterCode(code); return; }
            if (!string.IsNullOrEmpty(txt) && code == null)
            {
                var parsed = ParseSites(txt);
                if (parsed.Count > 0) { OpenShare("import", parsed, fromEnter == false); return; }
                if (fromEnter)
                {
                    var msg = Tb("ShareEnterMsg"); msg.Foreground = Theme.B("#8A90A0");
                    msg.Text = Translate.T(parseSkipped > 0 ? "Все сайты из скопированного списка у тебя уже есть (" + parseSkipped + ")." : "В скопированном тексте не нашлось адресов сайтов.");
                    return;
                }
            }
            else if (fromEnter)
            {
                var msg = Tb("ShareEnterMsg"); msg.Foreground = Theme.B("#8A90A0");
                msg.Text = Translate.T("Сначала скопируй список сайтов, который тебе прислали.");
                return;
            }
            OpenEnterCode(null);
        }

        void WireShare()
        {
            Btn("ShareBtn").Click += (s, e) => Safe(() =>
            {
                var g = ReadUserGroups();
                if (g.Count == 0) { Toast("Пока нечем делиться — добавь сайты", true); return; }
                OpenShare("share", g);
            });
            Btn("PasteBtn").Click += (s, e) => Safe(() => PasteList(false));
            WireShareCode();
            Tb("ShareAll").MouseLeftButtonUp += (s, e) =>
            {
                bool all = shareBoxes.Count(c => c.IsChecked == true) != shareBoxes.Count;
                foreach (var c in shareBoxes) c.IsChecked = all;
                foreach (StackPanel w in E<Panel>("ShareList").Children) ((CheckBox)((StackPanel)w.Children[0]).Children[0]).IsChecked = all;
                UpdateShareCount();
            };
            Btn("ShareClose").Click += (s, e) => CloseOverlay("Share");
            El("ShareDim").MouseLeftButtonUp += (s, e) => CloseOverlay("Share");
            Btn("ShareGo").Click += (s, e) => Safe(() =>
            {
                if (shareMode == "code") { CloseOverlay("Share"); return; }
                if (shareMode == "enter") { ShareFindCode(); return; }
                var sel = ShareSelection(); int n = sel.Total;
                if (shareMode == "share") ShareMakeCode(sel);
                else
                {
                    var g = ReadUserGroups();
                    foreach (var k in sel.Keys) { foreach (var d in sel[k]) if (!g[k].Contains(d)) g[k].Add(d); expanded["u:" + k] = true; }
                    WriteUserGroups(g);
                    CloseOverlay("Share");
                    rowsShown = false; RefreshSites();
                    Toast("Добавлено: " + Plural(n));
                    MarkChanged();
                }
            });
        }
    }
}
