// Zapret (C#) — самопроверка интерфейса: Zapret.exe -selftest <папка>
// Открывает окно «вхолостую» (без zapret, сети и служб), по очереди показывает каждый экран и каждый вариант
// выпадающих списков на русском и английском, ищет обрезанный текст и сохраняет скриншоты.
// Код выхода — число найденных проблем (0 — всё в порядке). Отчёт: <папка>\report.txt
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZapretCS
{
    partial class MainUi
    {
        public static int SelfTest(string outDir)
        {
            Directory.CreateDirectory(outDir);
            var st = new SelfTestRun(outDir);
            try { st.Run(); }
            catch (Exception ex) { st.Problem("запуск", "самопроверка упала: " + ex); }
            st.Save();
            return Math.Min(st.Count, 250);
        }

        // разметка окна — как в Run(), но без запуска логики приложения
        internal static Window BuildWindowForTest(string fontUri)
        {
            Theme.Init("dark", "#3DDC97");
            return (Window)XamlReader.Parse(Theme.Apply(Res("Main.xaml")).Replace("PXFONT", fontUri));
        }
    }

    class SelfTestRun
    {
        readonly string dir;
        readonly List<string> lines = new List<string>();
        readonly HashSet<string> seen = new HashSet<string>();
        public int Count;
        Window win;
        string lang = "ru";
        static readonly Regex fxName = new Regex("(Glow|Burst|Ok|OkS|Arc|Hover|Shine|Flash|Pulse|Ring)$");

        public SelfTestRun(string dir) { this.dir = dir; }

        public void Problem(string scene, string text)
        {
            string key = lang + "|" + text;
            if (!seen.Add(key)) return;   // одна и та же проблема на нескольких экранах — один раз
            Count++;
            lines.Add("[" + lang + "] " + scene + ": " + text);
        }

        public void Save()
        {
            var head = new List<string> { "Zapret GUI " + MainUi.AppVersion + " — самопроверка интерфейса " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Count == 0 ? "Проблем не найдено." : "Найдено проблем: " + Count, "" };
            File.WriteAllLines(Path.Combine(dir, "report.txt"), head.Concat(lines), new UTF8Encoding(true));
        }

        string FontUri()
        {
            // шрифт кладём в папку отчёта (самопроверке не нужны права администратора)
            string fdir = Path.Combine(dir, "font");
            Directory.CreateDirectory(fdir);
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ZPixel.ttf"))
            using (var f = File.Create(Path.Combine(fdir, "ZPixel.ttf"))) s.CopyTo(f);
            return System.Security.SecurityElement.Escape(new Uri(fdir.TrimEnd('\\') + "\\").AbsoluteUri + "#ZPixel");
        }

        public void Run()
        {
            string font = FontUri();
            foreach (var l in new[] { "ru", "en" })
            {
                lang = l;
                win = MainUi.BuildWindowForTest(font);
                win.ShowActivated = false; win.ShowInTaskbar = false;
                win.WindowStartupLocation = WindowStartupLocation.Manual;
                win.Left = SystemParameters.VirtualScreenLeft - 4000; win.Top = 0;
                win.Show();
                Settle(win);
                if (l == "en") Translate.InitForTest(win);
                Scenes();
                win.Close();
            }
        }

        // анимации появления в самопроверке не идут — ставим элементы в конечное положение
        void Settle(DependencyObject root)
        {
            foreach (var el in Walk(root))
            {
                var ue = el as UIElement;
                if (ue == null) continue;
                var tt = ue.RenderTransform as TranslateTransform;
                if (tt != null && !tt.IsFrozen) { tt.X = 0; tt.Y = 0; }
                var sc = ue.RenderTransform as ScaleTransform;
                if (sc != null && !sc.IsFrozen) { sc.ScaleX = 1; sc.ScaleY = 1; }
                var fe = ue as FrameworkElement;
                if (ue.Opacity == 0 && (fe == null || !fxName.IsMatch(fe.Name ?? ""))) ue.Opacity = 1;
            }
        }

        static IEnumerable<DependencyObject> Walk(DependencyObject root)
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var el = stack.Pop();
                yield return el;
                int n = el is Visual || el is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(el) : 0;
                if (n == 0) { foreach (var c in LogicalTreeHelper.GetChildren(el)) { var d = c as DependencyObject; if (d != null) stack.Push(d); } }
                else for (int i = 0; i < n; i++) stack.Push(VisualTreeHelper.GetChild(el, i));
            }
        }

        void Scenes()
        {
            Check("главный экран", win);
            var overlays = Walk(win).OfType<FrameworkElement>().Where(f => (f.Name ?? "").EndsWith("Overlay") && f.Visibility == Visibility.Collapsed).ToList();
            foreach (var ov in overlays)
            {
                ov.Visibility = Visibility.Visible;
                Settle(ov);
                Check(ov.Name, ov);
                // скрытые по умолчанию части окна (страницы настроек, варианты экранов) — по одной
                var parts = Walk(ov).OfType<FrameworkElement>().Where(f => f != ov && !string.IsNullOrEmpty(f.Name) && f.Visibility == Visibility.Collapsed && (f is Panel || f is Border)).ToList();
                foreach (var p in parts)
                {
                    var hidden = new List<FrameworkElement>();
                    string prefix = Regex.Replace(p.Name, @"\d+$", "");
                    if (prefix != p.Name)   // SetPage1… — соседние страницы того же набора прячем
                        foreach (var sib in parts.Concat(Walk(ov).OfType<FrameworkElement>()).Where(f => f != p && f.Name != null && Regex.IsMatch(f.Name, "^" + Regex.Escape(prefix) + @"\d+$") && f.Visibility == Visibility.Visible).Distinct().ToList())
                        { sib.Visibility = Visibility.Collapsed; hidden.Add(sib); }
                    p.Visibility = Visibility.Visible;
                    Settle(p);
                    Check(ov.Name + " → " + p.Name, ov);
                    p.Visibility = Visibility.Collapsed;
                    foreach (var h in hidden) h.Visibility = Visibility.Visible;
                }
                ov.Visibility = Visibility.Collapsed;
            }
        }

        void Check(string scene, FrameworkElement area)
        {
            win.UpdateLayout();
            Shot(scene);
            CheckTexts(scene, area);
            // выпадающие списки: каждый вариант должен помещаться в поле целиком
            foreach (var cb in Walk(area).OfType<ComboBox>().Where(c => c.IsVisible).ToList())
            {
                int keep = cb.SelectedIndex;
                for (int i = 0; i < cb.Items.Count; i++)
                {
                    cb.SelectedIndex = i;
                    cb.UpdateLayout(); win.UpdateLayout();
                    CheckTexts(scene + " · список " + (string.IsNullOrEmpty(cb.Name) ? "" : cb.Name), cb);
                }
                cb.SelectedIndex = keep;
            }
        }

        void CheckTexts(string scene, FrameworkElement area)
        {
            double dpi = 1;
            try { dpi = VisualTreeHelper.GetDpi(win).PixelsPerDip; } catch { }
            foreach (var tb in Walk(area).OfType<TextBlock>().ToList())
            {
                if (!tb.IsVisible || tb.ActualWidth <= 0 || string.IsNullOrWhiteSpace(tb.Text)) continue;
                if (tb.TextTrimming != TextTrimming.None) continue;   // «…» поставлено намеренно
                string text = tb.Text.Length > 60 ? tb.Text.Substring(0, 60) + "…" : tb.Text;
                // 1) строка без переноса шире своего места
                if (tb.TextWrapping == TextWrapping.NoWrap && tb.Inlines.Count <= 1)
                {
                    var ft = new FormattedText(tb.Text, CultureInfo.CurrentUICulture, tb.FlowDirection,
                        new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, Brushes.Black, dpi);
                    double need = ft.WidthIncludingTrailingWhitespace + tb.Padding.Left + tb.Padding.Right;
                    if (need > tb.ActualWidth + 1.5) { Problem(scene, "«" + text + "» не помещается: нужно " + (int)need + " px, есть " + (int)tb.ActualWidth + Where(tb)); continue; }
                }
                // 2) текст обрезан родительским элементом (поле списка, рамка фиксированного размера)
                var rect = new Rect(0, 0, tb.ActualWidth, tb.ActualHeight);
                DependencyObject cur = tb;
                while ((cur = VisualTreeHelper.GetParent(cur)) != null && cur != win)
                {
                    var fe = cur as FrameworkElement;
                    if (fe == null) continue;
                    Rect r;
                    try { r = tb.TransformToAncestor(fe).TransformBounds(rect); } catch { break; }
                    bool scroll = fe is ScrollContentPresenter;   // прокрутка по вертикали — это нормально
                    Geometry clip = LayoutInformation.GetLayoutClip(fe);
                    Rect box = clip != null ? clip.Bounds : (fe.ClipToBounds || scroll ? new Rect(0, 0, fe.ActualWidth, fe.ActualHeight) : Rect.Empty);
                    if (box.IsEmpty) continue;
                    bool cutX = r.Left < box.Left - 1.5 || r.Right > box.Right + 1.5;
                    bool cutY = !scroll && (r.Top < box.Top - 1.5 || r.Bottom > box.Bottom + 1.5);
                    if ((cutX || cutY) && r.IntersectsWith(box))
                    {
                        Problem(scene, "«" + text + "» обрезан " + (cutX ? "по ширине" : "по высоте") + " элементом " + Describe(fe) + Where(tb));
                        break;
                    }
                    if (scroll) break;
                }
            }
        }

        static string Describe(FrameworkElement fe)
        {
            return string.IsNullOrEmpty(fe.Name) ? fe.GetType().Name : fe.Name;
        }

        // ближайшие именованные родители — чтобы было понятно, где искать в Main.xaml
        static string Where(FrameworkElement fe)
        {
            var names = new List<string>();
            DependencyObject cur = fe;
            while (cur != null && names.Count < 3)
            {
                var f = cur as FrameworkElement;
                if (f != null && !string.IsNullOrEmpty(f.Name) && f.Name != "PART_Popup") names.Add(f.Name);
                cur = VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur);
            }
            return names.Count == 0 ? "" : " (в " + string.Join(" ← ", names) + ")";
        }

        void Shot(string scene)
        {
            try
            {
                var root = win.Content as FrameworkElement;
                if (root == null || root.ActualWidth < 1) return;
                var rtb = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(root);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                string name = lang + "_" + Regex.Replace(scene, @"[^A-Za-z0-9А-Яа-я]+", "_").Trim('_') + ".png";
                using (var f = File.Create(Path.Combine(dir, name))) enc.Save(f);
            }
            catch (Exception ex) { Problem(scene, "не удалось сделать скриншот: " + ex.Message); }
        }
    }
}
