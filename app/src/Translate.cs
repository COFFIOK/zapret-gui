// Zapret (C#) — перевод интерфейса на английский (при русском языке ничего не делает)
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace ZapretCS
{
    static partial class Translate
    {
        public static bool En;
        static Dictionary<string, string> exact;
        static readonly Regex cyr = new Regex("[А-Яа-яЁё]");

        public static void Init(MainUi ui)
        {
            En = ui.S.Str("lang", "ru") == "en";
            if (!En) return;
            exact = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < Pairs.GetLength(0); i++) exact[Pairs[i, 0]] = Pairs[i, 1];
            ui.Win.Title = "Zapret";
            Tree(ui.Win);
            // всплывающие списки и подсказки появляются позже — проверяем раз в 2 с, только когда окно активно
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            t.Tick += (s, e) => { if (ui.Win.IsActive && ui.Win.WindowState != WindowState.Minimized) Tree(ui.Win); };
            t.Start();
        }

        public static string T(string s)
        {
            if (!En || string.IsNullOrEmpty(s) || !cyr.IsMatch(s)) return s;
            string v;
            if (exact.TryGetValue(s, out v)) return v;
            for (int i = 0; i < Rx.GetLength(0); i++)
            {
                var m = Regex.Match(s, Rx[i, 0]);
                if (m.Success) return Regex.Replace(s, Rx[i, 0], Rx[i, 1]);
            }
            return s;
        }

        // обход всего дерева окна: тексты, подписи кнопок, подсказки
        public static void Tree(DependencyObject root)
        {
            if (!En || root == null) return;
            var seen = new HashSet<DependencyObject>();
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            int budget = 4000;
            while (stack.Count > 0 && budget-- > 0)
            {
                var el = stack.Pop();
                if (!seen.Add(el)) continue;
                try
                {
                    var tb = el as TextBlock;
                    if (tb != null)
                    {
                        if (tb.Inlines.Count > 1)
                        {
                            foreach (var inl in tb.Inlines) { var run = inl as Run; if (run != null && cyr.IsMatch(run.Text)) run.Text = T(run.Text); }
                        }
                        else if (cyr.IsMatch(tb.Text ?? "")) { var n = T(tb.Text); if (n != tb.Text) tb.Text = n; }
                        continue;
                    }
                    var cc = el as ContentControl;
                    if (cc != null && cc.Content is string && cyr.IsMatch((string)cc.Content)) { var n = T((string)cc.Content); if (n != (string)cc.Content) cc.Content = n; }
                    var fe = el as FrameworkElement;
                    if (fe != null && fe.ToolTip is string && cyr.IsMatch((string)fe.ToolTip)) fe.ToolTip = T((string)fe.ToolTip);
                    var combo = el as ComboBox;
                    if (combo != null) foreach (var it in combo.Items) { var d = it as DependencyObject; if (d != null) stack.Push(d); }
                    if (el is Visual)
                    {
                        int cnt = VisualTreeHelper.GetChildrenCount(el);
                        for (int i = 0; i < cnt; i++) stack.Push(VisualTreeHelper.GetChild(el, i));
                    }
                }
                catch { }
            }
        }
    }
}
