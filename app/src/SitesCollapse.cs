// Zapret (C#) — три вида главного окна:
//   0 «Полный»  — кнопка и настройки слева, «Мои сайты» справа;
//   1 «Ряд»     — «Мои сайты» свёрнуты в полоску справа, кнопка и настройки встают в ряд;
//   2 «Компакт» — окно сужается до узкого виджета, «Мои сайты» — строка внизу.
// Переходы плавные: блоки сами доезжают с прежнего места на новое (по очереди, с небольшой задержкой),
// уходящая панель тает снимком, окно в компакте сужается вокруг своего центра.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace ZapretCS
{
    partial class MainUi
    {
        const double StripWidth = 52;
        const int MoveMs = 520;
        int viewMode;            // 0 полный, 1 ряд, 2 компакт
        int modeBeforeCompact;
        bool modeBusy;
        double fullWinWidth;

        int SInt(string key, int def)
        {
            int v; return int.TryParse(S.Str(key, ""), out v) ? v : def;
        }

        void WireSitesCollapse()
        {
            fullWinWidth = Win.Width;
            Btn("SitesCollapse").Click += (s, e) => Safe(() => SetMode(1, true));
            Btn("CompactBtn").Click += (s, e) => Safe(() => SetMode(viewMode == 2 ? (modeBeforeCompact == 2 ? 0 : modeBeforeCompact) : 2, true));

            var strip = E<Border>("SitesStrip");
            strip.MouseLeftButtonUp += (s, e) => Safe(() => SetMode(0, true));
            HoverTab(strip, E<System.Windows.Shapes.Path>("StripChev"), A("StripChevT"), Anim.TX, -2);
            var foot = E<Border>("SitesFoot");
            foot.MouseLeftButtonUp += (s, e) => Safe(() => SetMode(0, true));
            HoverTab(foot, E<System.Windows.Shapes.Path>("FootChev"), A("FootChevT"), Anim.TX, 2);

            // число сайтов, цвета групп и «нужен перезапуск» видны и в свёрнутом виде
            var count = E<TextBlock>("SitesCount");
            DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock)).AddValueChanged(count, (s, e) => UpdateStrip());
            DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(Border)).AddValueChanged(El("ApplyBar"), (s, e) => UpdateStrip());
            UpdateStrip();

            int saved = SInt("viewMode", S.Bool("sitesCollapsed") ? 1 : 0);
            modeBeforeCompact = SInt("modeBeforeCompact", 0);
            if (saved > 0 && saved <= 2) Win.Loaded += (s, e) => SetMode(saved, false);
        }

        // наведение: рамка и стрелка подсвечиваются, стрелка чуть «зовёт» в свою сторону
        void HoverTab(Border b, System.Windows.Shapes.Path chev, IAnimatable chevT, DependencyProperty p, double shift)
        {
            b.MouseEnter += (s, e) =>
            {
                b.BorderBrush = (Brush)Win.FindResource("Accent");
                chev.Fill = (Brush)Win.FindResource("Accent");
                Anim.D(chevT, p, shift, 160, ease: Anim.Ease());
            };
            b.MouseLeave += (s, e) =>
            {
                b.BorderBrush = Theme.B("#262A33");
                chev.Fill = Theme.B("#8A90A0");
                Anim.D(chevT, p, 0, 200, ease: Anim.Ease());
            };
        }

        void UpdateStrip()
        {
            var m = Regex.Match(E<TextBlock>("SitesCount").Text ?? "", @"\d+");
            string n = m.Success ? m.Value : "";
            E<TextBlock>("StripCount").Text = n;
            E<TextBlock>("FootCount").Text = n;
            var apply = El("ApplyBar").Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
            El("StripApplyDot").Visibility = apply;
            El("FootApplyDot").Visibility = apply;

            // цветные метки групп — как в списке сайтов
            var colors = new List<string>();
            try
            {
                var ug = ReadUserGroups();
                foreach (var k in ug.Keys) if (ug[k].Count > 0) colors.Add(GroupColor(k, false));
                var bg = BuiltinGroups();
                foreach (var k in bg.Keys) if (bg[k].Count > 0) colors.Add(GroupColor(k, true));
            }
            catch { }
            var sd = E<StackPanel>("StripDots"); var fd = E<StackPanel>("FootDots");
            sd.Children.Clear(); fd.Children.Clear();
            for (int i = 0; i < colors.Count && i < 9; i++)
            {
                sd.Children.Add(new System.Windows.Shapes.Rectangle { Width = 10, Height = 10, Margin = new Thickness(0, 0, 0, 6), Fill = Theme.B(colors[i]), SnapsToDevicePixels = true });
                fd.Children.Add(new System.Windows.Shapes.Rectangle { Width = 8, Height = 8, Margin = new Thickness(0, 0, 5, 0), Fill = Theme.B(colors[i]), SnapsToDevicePixels = true });
            }
        }

        // для старых вызовов (превью и т.п.)
        void SetSitesCollapsed(bool collapse, bool animate) { SetMode(collapse ? 1 : 0, animate); }

        // ===== раскладки без анимации =====
        bool wideLayout;
        void SetWideLayout(bool wide)
        {
            if (wide == wideLayout) return;
            wideLayout = wide;
            var left = E<Grid>("LeftCol");
            var center = E<StackPanel>("CenterStack");
            var side = E<StackPanel>("SideStack");
            var controls = E<StackPanel>("ControlsBlock");
            var test = E<StackPanel>("TestHost");
            var label = controls.Children[0] as FrameworkElement;
            if (wide)
            {
                center.Children.Remove(controls); left.Children.Remove(test);
                side.Children.Add(controls); side.Children.Add(test);
                if (label != null) label.Margin = new Thickness(2, 0, 0, 6);
                test.Margin = new Thickness(0, 18, 0, 0);
                ((ColumnDefinition)Win.FindName("LcA")).Width = new GridLength(330);
                ((ColumnDefinition)Win.FindName("LcGap")).Width = new GridLength(56);
                ((ColumnDefinition)Win.FindName("LcB")).Width = new GridLength(330);
                left.Width = 330 + 56 + 330;
            }
            else
            {
                side.Children.Remove(controls); side.Children.Remove(test);
                center.Children.Add(controls); left.Children.Add(test);
                Grid.SetRow(test, 1); Grid.SetColumn(test, 0);
                if (label != null) label.Margin = new Thickness(2, 26, 0, 6);
                test.Margin = new Thickness(0);
                ((ColumnDefinition)Win.FindName("LcA")).Width = new GridLength(1, GridUnitType.Star);
                ((ColumnDefinition)Win.FindName("LcGap")).Width = new GridLength(0);
                ((ColumnDefinition)Win.FindName("LcB")).Width = new GridLength(0);
                left.Width = 330;
            }
        }

        void ApplyLayout(int mode)
        {
            var area = E<Grid>("MainArea");
            var right = E<Grid>("RightCol");
            var colL = (ColumnDefinition)Win.FindName("LeftColDef");
            var colR = (ColumnDefinition)Win.FindName("RightColDef");
            var gap = area.ColumnDefinitions[1];
            var applyRow = (RowDefinition)Win.FindName("ApplyRow");
            var card = El("SitesCard"); var strip = El("SitesStrip"); var foot = El("SitesFoot");
            foreach (var el in new[] { card, strip, foot }) { Anim.Stop(el, Anim.OP); el.Opacity = 1; }
            Anim.Stop(A("SitesCardT"), Anim.TX); ((TranslateTransform)A("SitesCardT")).X = 0;
            Anim.Stop(A("SitesStripT"), Anim.TX); ((TranslateTransform)A("SitesStripT")).X = 0;
            Anim.Stop(A("SitesFootT"), Anim.TY); ((TranslateTransform)A("SitesFootT")).Y = 0;

            right.Visibility = mode == 2 ? Visibility.Collapsed : Visibility.Visible;
            card.Visibility = mode == 0 ? Visibility.Visible : Visibility.Collapsed;
            strip.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;
            foot.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;
            if (mode == 0)
            {
                colL.Width = new GridLength(330); gap.Width = new GridLength(22);
                colR.Width = new GridLength(1, GridUnitType.Star); right.Width = double.NaN;
                applyRow.Height = GridLength.Auto;
            }
            else if (mode == 1)
            {
                colL.Width = new GridLength(1, GridUnitType.Star); gap.Width = new GridLength(22);
                colR.Width = GridLength.Auto; right.Width = StripWidth;
                applyRow.Height = new GridLength(0);
            }
            else
            {
                colL.Width = new GridLength(1, GridUnitType.Star); gap.Width = new GridLength(0);
                colR.Width = new GridLength(0);
            }
            SetWideLayout(mode == 1);
            El("TitleVer").Visibility = mode == 2 ? Visibility.Collapsed : Visibility.Visible;
            Btn("CompactBtn").ToolTip = Translate.T(mode == 2 ? "Обычный вид" : "Компактный вид");
            E<System.Windows.Shapes.Path>("CompactIcon").Data = (Geometry)Win.FindResource(mode == 2 ? "PxExpandWin" : "PxCompactWin");
        }

        // окно ровно под левую колонку: всё, что шире неё, убираем
        double CompactWidth()
        {
            var sm = E<Grid>("Shell").Margin;
            double extra = E<Grid>("MainArea").ActualWidth + sm.Left + sm.Right - 330;
            return extra <= 0 ? Win.Width : Math.Round(Win.Width - extra);
        }

        // ===== переход между видами =====
        void SetMode(int mode, bool animate)
        {
            if (modeBusy || mode == viewMode) return;
            int old = viewMode;
            if (mode == 2) modeBeforeCompact = old;
            bool anim = animate && !Anim.Eco && Win.IsVisible;
            var area = E<Grid>("MainArea");
            var layer = E<Canvas>("FlipLayer");
            UpdateStrip();
            if (widenedForOverlay) widenedForOverlay = false;

            // что двигаем: кнопка, подписи, стратегия, Telegram, тест
            var controls = E<StackPanel>("ControlsBlock");
            var els = new List<FrameworkElement> { (FrameworkElement)El("PowerBtn"), E<TextBlock>("StatusText"), E<TextBlock>("SubText") };
            foreach (UIElement c in controls.Children) { var fe = c as FrameworkElement; if (fe != null && fe.Visibility == Visibility.Visible) els.Add(fe); }
            els.Add(E<StackPanel>("TestHost"));
            var first = new List<Point>();
            if (anim) { area.UpdateLayout(); foreach (var el in els) { ResetFlip(el); first.Add(PosIn(el, area)); } }

            // уходящая панель тает снимком
            FrameworkElement leaving = (FrameworkElement)(old == 0 ? El("SitesCard") : old == 1 ? El("SitesStrip") : El("SitesFoot"));
            Image ghost = anim ? Ghost(leaving, layer, area) : null;

            double toW = old == 2 ? fullWinWidth : -1;
            ApplyLayout(mode);
            viewMode = mode;
            S.Set("viewMode", mode); S.Set("modeBeforeCompact", modeBeforeCompact); S.Set("sitesCollapsed", mode == 1); S.Save();

            if (mode == 2) { area.UpdateLayout(); toW = CompactWidth(); }
            if (!anim)
            {
                if (toW > 0) MoveWindow(toW, 0);
                return;
            }
            modeBusy = true;
            area.UpdateLayout();

            // FLIP: каждый блок стартует с прежнего места и плавно доезжает до нового, по очереди
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            for (int i = 0; i < els.Count; i++)
            {
                var el = els[i];
                var now = PosIn(el, area);
                double dx = first[i].X - now.X, dy = first[i].Y - now.Y;
                if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) continue;
                var t = FlipT(el);
                int delay = i * 28;
                t.X = dx; t.Y = dy;
                Anim.D(t, Anim.TX, 0, MoveMs, dx, begin: delay, ease: ease);
                Anim.D(t, Anim.TY, 0, MoveMs, dy, begin: delay, ease: ease);
            }

            if (ghost != null)
            {
                var gt = (TranslateTransform)ghost.RenderTransform;
                Anim.D(ghost, Anim.OP, 0, 260, 1, ease: new QuadraticEase { EasingMode = EasingMode.EaseIn });
                if (old == 2) Anim.D(gt, Anim.TY, 14, 300, 0, ease: Anim.Ease());
                else Anim.D(gt, Anim.TX, old == 0 ? 48 : 18, 340, 0, ease: Anim.Ease());
            }

            // входящая панель
            var outEase = new QuinticEase { EasingMode = EasingMode.EaseOut };
            if (mode == 0)
            {
                var card = El("SitesCard");
                card.Opacity = 0;
                int b = old == 2 ? 300 : 150;
                Anim.D(card, Anim.OP, 1, 320, 0, begin: b);
                Anim.D(A("SitesCardT"), Anim.TX, 0, 560, 48, begin: b - 60, ease: outEase);
            }
            else if (mode == 1)
            {
                var strip = El("SitesStrip"); strip.Opacity = 0;
                Anim.D(strip, Anim.OP, 1, 300, 0, begin: 200);
                Anim.D(A("SitesStripT"), Anim.TX, 0, 480, 18, begin: 160, ease: outEase);
            }
            else
            {
                var foot = El("SitesFoot"); foot.Opacity = 0;
                Anim.D(foot, Anim.OP, 1, 300, 0, begin: 300);
                Anim.D(A("SitesFootT"), Anim.TY, 0, 460, 14, begin: 260, ease: outEase);
            }

            if (toW > 0) MoveWindow(toW, MoveMs);
            After(MoveMs + 320, () =>
            {
                layer.Children.Clear();
                foreach (var el in els) ResetFlip(el);
                modeBusy = false;
            });
        }

        // ===== помощники =====
        readonly Dictionary<FrameworkElement, TranslateTransform> flipTs = new Dictionary<FrameworkElement, TranslateTransform>();
        TranslateTransform FlipT(FrameworkElement el)
        {
            TranslateTransform t;
            if (flipTs.TryGetValue(el, out t)) return t;
            t = new TranslateTransform();
            var cur = el.RenderTransform;
            if (cur == null || (cur.Value.IsIdentity && !(cur is TransformGroup))) el.RenderTransform = t;
            else { var g = new TransformGroup(); g.Children.Add(cur); g.Children.Add(t); el.RenderTransform = g; }
            flipTs[el] = t;
            return t;
        }
        void ResetFlip(FrameworkElement el)
        {
            TranslateTransform t;
            if (!flipTs.TryGetValue(el, out t)) return;
            t.BeginAnimation(TranslateTransform.XProperty, null); t.BeginAnimation(TranslateTransform.YProperty, null);
            t.X = 0; t.Y = 0;
        }
        static Point PosIn(FrameworkElement el, Visual root)
        {
            try { return el.TransformToAncestor(root).Transform(new Point(0, 0)); } catch { return new Point(0, 0); }
        }

        Image Ghost(FrameworkElement el, Canvas layer, Visual area)
        {
            try
            {
                if (el == null || el.Visibility != Visibility.Visible || el.ActualWidth < 2 || el.ActualHeight < 2) return null;
                var src = PresentationSource.FromVisual(Win);
                double sx = 1, sy = 1;
                if (src != null && src.CompositionTarget != null) { sx = src.CompositionTarget.TransformToDevice.M11; sy = src.CompositionTarget.TransformToDevice.M22; }
                var rtb = new RenderTargetBitmap((int)Math.Ceiling(el.ActualWidth * sx), (int)Math.Ceiling(el.ActualHeight * sy), 96 * sx, 96 * sy, PixelFormats.Pbgra32);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
                rtb.Render(dv);
                var p = PosIn(el, area);
                var img = new Image { Source = rtb, Width = el.ActualWidth, Height = el.ActualHeight, IsHitTestVisible = false, RenderTransform = new TranslateTransform() };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
                Canvas.SetLeft(img, p.X); Canvas.SetTop(img, p.Y);
                layer.Children.Add(img);
                return img;
            }
            catch { return null; }
        }

        // окно меняет ширину вокруг своего центра — содержимое по центру стоит на месте.
        // Само окно (прозрачное) менять каждый кадр дорого и рывками, поэтому анимируется только
        // «оболочка» внутри него, а размер окна меняется один раз: в конце при сужении, в начале при расширении.
        int winMoveId;
        void MoveWindow(double toW, int ms)
        {
            var shell = E<Grid>("Shell");
            int id = ++winMoveId;
            shell.BeginAnimation(FrameworkElement.MarginProperty, null);
            shell.Margin = new Thickness(0);
            double fromW = Win.Width, fromL = Win.Left;
            if (double.IsNaN(fromL)) { Win.Width = toW; return; }
            double half = (fromW - toW) / 2;
            double toL = Math.Round(fromL + half);
            if (Math.Abs(half) < 0.5) return;
            if (ms <= 0 || Anim.Eco || !Win.IsVisible) { Win.Width = toW; Win.Left = toL; return; }
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            if (half > 0)
            {
                // сужение: оболочка сжимается к центру, потом окно один раз подгоняется под неё
                var a = new ThicknessAnimation(new Thickness(0), new Thickness(half, 0, half, 0), new Duration(TimeSpan.FromMilliseconds(ms))) { EasingFunction = ease };
                a.Completed += (s, e) =>
                {
                    if (id != winMoveId) return;
                    shell.BeginAnimation(FrameworkElement.MarginProperty, null);
                    shell.Margin = new Thickness(0);
                    SetBounds(toL, toW);
                };
                shell.BeginAnimation(FrameworkElement.MarginProperty, a);
            }
            else
            {
                // расширение: окно сразу становится широким, а оболочка в нём раскрывается от центра
                double h = -half;
                shell.Margin = new Thickness(h, 0, h, 0);
                SetBounds(toL, toW);
                var a = new ThicknessAnimation(new Thickness(h, 0, h, 0), new Thickness(0), new Duration(TimeSpan.FromMilliseconds(ms))) { EasingFunction = ease };
                a.Completed += (s, e) =>
                {
                    if (id != winMoveId) return;
                    shell.BeginAnimation(FrameworkElement.MarginProperty, null);
                    shell.Margin = new Thickness(0);
                };
                shell.BeginAnimation(FrameworkElement.MarginProperty, a);
            }
        }

        // положение и ширина окна — одним вызовом, без промежуточного кадра
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        void SetBounds(double left, double width)
        {
            var hwnd = new WindowInteropHelper(Win).Handle;
            var src = PresentationSource.FromVisual(Win);
            if (hwnd != IntPtr.Zero && src != null && src.CompositionTarget != null)
            {
                var m = src.CompositionTarget.TransformToDevice;
                SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(left * m.M11), (int)Math.Round(Win.Top * m.M22),
                    (int)Math.Round(width * m.M11), (int)Math.Round(Win.ActualHeight * m.M22), 0x0014);
            }
            Win.Width = width; Win.Left = left;
        }

        // окна-панели шире компактного окна: на время открытия окно расширяется, потом снова сужается
        bool widenedForOverlay;
        void OverlayOpened(string p)
        {
            if (viewMode != 2 || widenedForOverlay) return;
            widenedForOverlay = true;
            MoveWindow(fullWinWidth, 360);
        }
        void OverlayClosed(int delay)
        {
            if (!widenedForOverlay) return;
            After(delay, () =>
            {
                if (!widenedForOverlay || viewMode != 2) { widenedForOverlay = false; return; }
                foreach (var n in new[] { "AuOverlay", "MigOverlay", "SettingsOverlay", "TestOverlay", "ShareOverlay", "FbOverlay", "UninstOverlay", "CloseOverlay", "QrOverlay", "CsOverlay" })
                {
                    var el = Win.FindName(n) as UIElement;
                    if (el != null && el.Visibility == Visibility.Visible) return;
                }
                widenedForOverlay = false;
                E<Grid>("MainArea").UpdateLayout();
                MoveWindow(CompactWidth(), 360);
            });
        }
    }
}
