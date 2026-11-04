// Zapret (C#) — пиксельные анимации включения: буква печатается, падает тетрисом, крутится монеткой,
// рассыпается, глючит, взрывается пикселями, включается как старый телевизор, бьёт молния…
// Всё рисуется клетками и движется кадрами — в стиле интерфейса.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using KV = System.Collections.Generic.KeyValuePair<int, int>;

namespace ZapretCS
{
    partial class MainUi
    {
        // слой эффектов 260×260, центр совпадает с центром кнопки; буква — с (86, 86), клетка 8 px
        const double FxC = 130, ZO = 86;
        const string ZInk = "#0F1115";   // цвет буквы на включённой (зелёной) кнопке
        Canvas Fx { get { return E<Canvas>("FxLayer"); } }
        static double Rnd(double a, double b) { return a + zRnd.NextDouble() * (b - a); }
        static IEasingFunction EIn(double p = 2) { return new PowerEase { EasingMode = EasingMode.EaseIn, Power = p }; }
        static IEasingFunction EOut(double p = 2) { return new PowerEase { EasingMode = EasingMode.EaseOut, Power = p }; }

        static Geometry ZGeo(IEnumerable<KV> cells) { return Px.Cells(cells, ZCell, ZO, ZO); }

        Path FxPath(Geometry g, string hex, double op = 1)
        {
            var p = new Path { Data = g, Fill = Theme.B(hex), Opacity = op, IsHitTestVisible = false };
            Fx.Children.Add(p);
            return p;
        }
        Rectangle FxBox(double cx, double cy, double size, string hex)
        {
            var r = new Rectangle { Width = size, Height = size, Fill = Theme.B(hex), Opacity = 0, IsHitTestVisible = false, RenderTransform = new TranslateTransform() };
            Canvas.SetLeft(r, Math.Round(cx - size / 2)); Canvas.SetTop(r, Math.Round(cy - size / 2));
            Fx.Children.Add(r);
            return r;
        }
        // убрать элементы эффекта, когда он доиграет
        void FxDone(int ms)
        {
            var c = Fx; var mine = c.Children.Cast<UIElement>().ToList();
            After(ms, () => { foreach (var e in mine) c.Children.Remove(e); });
        }
        void FxClear() { Fx.Children.Clear(); }

        void ZHidden() { Anim.Stop(A("ZFill"), Anim.OP); El("ZFill").Opacity = 0; }
        void ZOn(int ms) { Px.Steps(A("ZFill"), Anim.OP, ms, 0, 1); }
        void ZBump(int ms) { Px.Steps(A("ZMove"), Anim.TY, ms, 0, -4, 60, 0); }

        // плавная кривая, но разбитая на кадры, значения кратны snap px
        static void Tween(IAnimatable t, DependencyProperty p, double from, double to, int ms, int begin, IEasingFunction ease = null, int frame = 50, double snap = 2)
        {
            if (t == null) return;
            if (Anim.Eco) { t.BeginAnimation(p, null); ((DependencyObject)t).SetValue(p, to); return; }
            int n = Math.Max(1, ms / frame);
            var a = new DoubleAnimationUsingKeyFrames();
            if (begin > 0) a.BeginTime = TimeSpan.FromMilliseconds(begin);
            for (int k = 0; k <= n; k++)
            {
                double q = (double)k / n; if (ease != null) q = ease.Ease(q);
                double v = from + (to - from) * q;
                if (snap > 0) v = Math.Round(v / snap) * snap;
                a.KeyFrames.Add(new DiscreteDoubleKeyFrame(v, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(k * frame))));
            }
            t.BeginAnimation(p, a);
        }
        // покадровая траектория элемента: точки (x, y) через каждые frame мс
        static void Path2(Rectangle r, int begin, int frame, IList<Point> pts)
        {
            var tt = (TranslateTransform)r.RenderTransform;
            var ax = new DoubleAnimationUsingKeyFrames(); var ay = new DoubleAnimationUsingKeyFrames();
            if (begin > 0) { ax.BeginTime = TimeSpan.FromMilliseconds(begin); ay.BeginTime = ax.BeginTime; }
            for (int k = 0; k < pts.Count; k++)
            {
                var kt = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(k * frame));
                ax.KeyFrames.Add(new DiscreteDoubleKeyFrame(Math.Round(pts[k].X / 2) * 2, kt));
                ay.KeyFrames.Add(new DiscreteDoubleKeyFrame(Math.Round(pts[k].Y / 2) * 2, kt));
            }
            tt.BeginAnimation(TranslateTransform.XProperty, ax); tt.BeginAnimation(TranslateTransform.YProperty, ay);
        }
        // пиксельная «искорка»-плюсик
        Path Sparkle(double cx, double cy, string hex, int begin)
        {
            var cells = new[] { Px.C(1, 0), Px.C(0, 1), Px.C(1, 1), Px.C(2, 1), Px.C(1, 2) };
            var p = FxPath(Px.Cells(cells, 4, Math.Round(cx) - 6, Math.Round(cy) - 6), hex, 0);
            Px.Steps(p, Anim.OP, begin, 0, 1, 60, 0.3, 120, 1, 180, 0.4, 240, 0);
            return p;
        }
        // квадратная рамка из клеток 5 px на слое 260×260 (h — половина стороны в клетках)
        static Geometry SquareRing(int h, int th = 1)
        {
            var cells = new List<KV>();
            for (int i = 0; i < 52; i++) for (int j = 0; j < 52; j++)
            {
                int d = Math.Max(Math.Abs(i - 26 + (i >= 26 ? 1 : 0)), Math.Abs(j - 26 + (j >= 26 ? 1 : 0)));
                if (d <= h && d > h - th) cells.Add(Px.C(i, j));
            }
            return Px.Cells(cells, 5);
        }
        // ромб из клеток вокруг центра (вспышка)
        static Geometry Diamond(int r, double cell)
        {
            var cells = new List<KV>();
            for (int i = -r; i <= r; i++) for (int j = -r; j <= r; j++) if (Math.Abs(i) + Math.Abs(j) <= r) cells.Add(Px.C(i, j));
            return Px.Cells(cells, cell, FxC - cell / 2, FxC - cell / 2);
        }

        // ---- 1. печать: буква набирается строка за строкой, справа бежит курсор ----
        void FxType()
        {
            ZHidden();
            var rev = new List<Geometry>(); var cur = new List<Geometry>();
            for (int r = 0; r < 11; r++)
            {
                int rr = r;
                rev.Add(ZGeo(ZCells.Where(c => c.Value <= rr)));
                int mx = ZCells.Where(c => c.Value == rr).Max(c => c.Key);
                cur.Add(ZGeo(new[] { Px.C(mx + 1, rr) }));
            }
            Px.PlayFrames(FxPath(null, ZInk), rev, 55, false, 0, true);
            Px.PlayFrames(FxPath(null, "#FFFFFF"), cur, 55, false, 0, true);
            ZOn(605); ZBump(605);
            FxDone(800);
        }

        // ---- 2. тетрис: строки буквы падают сверху и складываются ----
        void FxTetris()
        {
            ZHidden();
            for (int r = 10; r >= 0; r--)
            {
                int rr = r;
                var p = FxPath(ZGeo(ZCells.Where(c => c.Value == rr)), (r % 3 == 0) ? "#FFFFFF" : ZInk, 0);
                var tt = new TranslateTransform(); p.RenderTransform = tt;
                int begin = (10 - r) * 55;
                double dist = ZO + r * ZCell + 24;
                tt.Y = -dist;
                Px.Steps(p, Anim.OP, begin, 0, 1);
                Tween(tt, TranslateTransform.YProperty, -dist, 0, 225, begin, EIn(2), 45, 2);
            }
            ZOn(800);
            Px.Steps(A("ZMove"), Anim.TY, 800, 0, 3, 50, -1, 100, 0);
            FxDone(830);
        }

        // ---- 3. монетка: буква подпрыгивает и крутится, вокруг искрится ----
        void FxCoin()
        {
            ZOn(0);
            Px.Steps(A("ZMove"), Anim.TY, 0, 0, 0, 50, -10, 100, -18, 150, -22, 200, -24, 250, -22, 300, -18, 350, -10, 400, 0);
            Px.Steps(A("ZFlip"), Anim.SX, 0, 0, 1, 50, 0.6, 100, 0.15, 150, -0.6, 200, -1, 250, -0.6, 300, 0.15, 350, 0.6, 400, 1);
            for (int i = 0; i < 5; i++)
            {
                double a = Rnd(0, Math.PI * 2), d = Rnd(72, 104);
                Sparkle(FxC + Math.Cos(a) * d, FxC + Math.Sin(a) * d, i % 2 == 0 ? "#FFFFFF" : "#FFD166", 120 + i * 70);
            }
            FxDone(900);
        }

        // ---- 4. проявление: пиксели загораются в случайном порядке ----
        void FxDissolve()
        {
            ZHidden();
            var order = ZCells.OrderBy(c => zRnd.Next()).ToList();
            var frames = new List<Geometry>();
            for (int k = 1; k <= 8; k++) frames.Add(ZGeo(order.Take(order.Count * k / 8)));
            Px.PlayFrames(FxPath(null, "#FFFFFF"), frames, 50, false, 0, true);
            ZOn(400);
            var rim = FxPath(Px.Ring(260, 5, 86, 81), "#FFFFFF", 0);
            Px.Steps(rim, Anim.OP, 400, 0, 0.8, 60, 0.4, 120, 0);
            FxDone(600);
        }

        // ---- 5. глитч: цветные копии разъезжаются, полосы сдвигаются ----
        void FxGlitch()
        {
            ZOn(0);
            Px.Steps(A("ZFill"), Anim.OP, 0, 0, 1, 40, 0.3, 80, 1, 160, 0.5, 200, 1);
            var all = ZGeo(ZCells);
            var cy = FxPath(all, "#5ED3F3", 0.85); var cyT = new TranslateTransform(); cy.RenderTransform = cyT;
            var mg = FxPath(all, "#FF5E9C", 0.85); var mgT = new TranslateTransform(); mg.RenderTransform = mgT;
            Px.Steps(cyT, Anim.TX, 0, 0, -6, 40, 5, 80, -3, 120, 8, 160, -2, 200, 4, 240, 0);
            Px.Steps(mgT, Anim.TX, 0, 0, 6, 40, -4, 80, 4, 120, -8, 160, 3, 200, -3, 240, 0);
            Px.Steps(mgT, Anim.TY, 0, 0, 2, 80, -2, 160, 0);
            Px.Steps(cy, Anim.OP, 0, 0, 0.85, 260, 0); Px.Steps(mg, Anim.OP, 0, 0, 0.85, 260, 0);
            // сдвинутые полосы
            for (int b = 0; b < 2; b++)
            {
                int y0 = b == 0 ? 3 : 7;
                var band = FxPath(ZGeo(ZCells.Where(c => c.Value >= y0 && c.Value <= y0 + 1)), "#FFFFFF", 0);
                var bt = new TranslateTransform(); band.RenderTransform = bt;
                int t0 = 60 + b * 70;
                Px.Steps(band, Anim.OP, t0, 0, 1, 70, 0);
                Px.Steps(bt, Anim.TX, t0, 0, b == 0 ? 12 : -10, 35, b == 0 ? -6 : 8);
            }
            Px.Steps(A("ZMove"), Anim.TX, 0, 0, 3, 40, -3, 80, 2, 120, -1, 160, 0);
            FxDone(400);
        }

        // ---- 6. взрыв пикселей: вспышка в центре, квадратики разлетаются во все стороны ----
        void FxBurst()
        {
            ZHidden();
            var fl = new List<Geometry>();
            for (int r = 1; r <= 4; r++) fl.Add(Diamond(r, 8));
            Px.PlayFrames(FxPath(null, "#FFFFFF"), fl, 45, false, 0, true);
            double[] radii = { 16, 34, 52, 68, 82, 94, 104 };
            for (int i = 0; i < 16; i++)
            {
                double a = i * Math.PI / 8;
                var r = FxBox(FxC, FxC, i % 2 == 0 ? 8 : 6, i % 2 == 0 ? "#FFFFFF" : ZInk);
                var pts = radii.Select(d => new Point(Math.Cos(a) * d, Math.Sin(a) * d)).ToList();
                Path2(r, 60, 55, pts);
                Px.Steps(r, Anim.OP, 60, 0, 1, 220, 0.6, 330, 0.3, 385, 0);
            }
            ZOn(180);
            Px.Steps(A("ZScale"), Anim.SX, 180, 0, 0.5, 50, 1.25, 100, 0.9, 150, 1);
            Px.Steps(A("ZScale"), Anim.SY, 180, 0, 0.5, 50, 1.25, 100, 0.9, 150, 1);
            FxDone(600);
        }

        // ---- 7. старый телевизор: светится линия, раскрывается в букву ----
        void FxCrt()
        {
            ZHidden();
            var frames = new List<Geometry>();
            foreach (int w in new[] { 0, 2, 4, 5 })
            {
                var cells = new List<KV>(); for (int xx = 5 - w; xx <= 5 + w; xx++) cells.Add(Px.C(xx, 5));
                frames.Add(ZGeo(cells));
            }
            for (int d = 0; d <= 5; d++) { int dd = d; frames.Add(ZGeo(ZCells.Where(c => Math.Abs(c.Value - 5) <= dd))); }
            Px.PlayFrames(FxPath(null, "#FFFFFF"), frames, 50, false, 0, true);
            ZOn(500);
            Px.Steps(A("ZFill"), Anim.OP, 500, 0, 1, 50, 0.55, 100, 1, 160, 0.75, 210, 1);
            FxDone(750);
        }

        // ---- 8. змейка: светлая голова проползает по букве и оставляет след ----
        void FxSnake()
        {
            ZHidden();
            var trail = new List<Geometry>(); var head = new List<Geometry>();
            for (int k = 0; k <= 31; k++)
            {
                int kk = k;
                trail.Add(ZGeo(ZCells.Where(c => ZStrokePos(c) < kk)));
                head.Add(ZGeo(ZCells.Where(c => { double s = ZStrokePos(c); return s >= kk && s < kk + 3; })));
            }
            Px.PlayFrames(FxPath(null, ZInk), trail, 24, false, 0, true);
            Px.PlayFrames(FxPath(null, "#FFFFFF"), head, 24, false, 0, true);
            ZOn(768); ZBump(768);
            FxDone(900);
        }

        // ---- 9. молния: пиксельный разряд бьёт в кнопку ----
        Geometry BoltGeo(double x0, int fromRow, int toRow, double targetX, out double endX)
        {
            var cells = new List<KV>();
            double xx = x0;
            for (int row = fromRow; row <= toRow; row++)
            {
                cells.Add(Px.C((int)Math.Round(xx / 6), row));
                if (row % 2 == 0) cells.Add(Px.C((int)Math.Round(xx / 6) + (zRnd.Next(2) == 0 ? -1 : 1), row));
                double pull = (targetX - xx) / Math.Max(1, toRow - row);
                xx += pull + (zRnd.Next(3) - 1) * 6;
            }
            endX = xx;
            return Px.Cells(cells, 6);
        }
        void FxLightning()
        {
            ZHidden();
            double ex;
            var parts = new List<Geometry>();
            double sx = FxC + Rnd(-40, 40);
            parts.Add(BoltGeo(sx, 0, 7, FxC + Rnd(-20, 20), out ex));
            parts.Add(BoltGeo(ex, 8, 14, FxC + Rnd(-10, 10), out ex));
            parts.Add(BoltGeo(ex, 15, 21, FxC, out ex));
            var frames = new List<Geometry>();
            var acc = new GeometryGroup();
            foreach (var g in parts) { acc.Children.Add(g); var copy = acc.Clone(); copy.Freeze(); frames.Add(copy); }
            var bolt = FxPath(null, "#FFFFFF");
            Px.PlayFrames(bolt, frames, 40, false);
            Px.Steps(bolt, Anim.OP, 0, 0, 1, 160, 0.2, 200, 1, 240, 0.3, 290, 0);
            var flash = FxPath(Px.Ring(260, 5, 80, 0), "#FFFFFF", 0);
            Px.Steps(flash, Anim.OP, 160, 0, 0.7, 50, 0.35, 100, 0);
            ZOn(170);
            Px.Steps(A("ZMove"), Anim.TX, 170, 0, -4, 40, 4, 80, -2, 120, 0);
            FxDone(500);
        }

        // ---- 10. варп: квадратный туннель сжимается, буква вылетает навстречу ----
        void FxWarp()
        {
            ZHidden();
            var frames = new List<Geometry>();
            foreach (int h in new[] { 20, 17, 14, 11, 8, 5, 2 })
            {
                var g = new GeometryGroup();
                g.Children.Add(SquareRing(h));
                if (h + 6 <= 20) g.Children.Add(SquareRing(h + 6));
                g.Freeze(); frames.Add(g);
            }
            Px.PlayFrames(FxPath(null, "#FFFFFF", 0.9), frames, 55, false, 0, true);
            ZOn(385);
            Px.Steps(A("ZScale"), Anim.SX, 385, 0, 0.25, 50, 0.5, 100, 0.75, 150, 1.12, 200, 1);
            Px.Steps(A("ZScale"), Anim.SY, 385, 0, 0.25, 50, 0.5, 100, 0.75, 150, 1.12, 200, 1);
            FxDone(650);
        }

        // ---- 11. конфетти: буква выпрыгивает, разноцветные пиксели летят и падают ----
        void FxConfetti()
        {
            string[] cols = { "#FFFFFF", "#3DDC97", "#5ED3F3", "#FFD166", "#FF7AB6", "#B69CFF" };
            Px.Steps(A("ZScale"), Anim.SX, 0, 0, 0.5, 50, 1.2, 100, 0.95, 150, 1);
            Px.Steps(A("ZScale"), Anim.SY, 0, 0, 0.5, 50, 1.2, 100, 0.95, 150, 1);
            ZOn(0);
            for (int i = 0; i < 28; i++)
            {
                double vx = Rnd(-1.6, 1.6), vy = Rnd(-3.4, -1.8);
                var r = FxBox(FxC, FxC, zRnd.Next(2) == 0 ? 6 : 4, cols[i % cols.Length]);
                var pts = new List<Point>();
                for (int k = 0; k < 14; k++) { double t = k * 7; pts.Add(new Point(vx * t, vy * t + 0.045 * t * t)); }
                Path2(r, 40, 70, pts);
                Px.Steps(r, Anim.OP, 40, 0, 1, 700, 0.6, 840, 0.3, 910, 0);
            }
            FxDone(1100);
        }

        // ---- 12. сонар: квадратно-пиксельные волны, буква всплывает ----
        void FxSonar()
        {
            ZHidden();
            for (int w = 0; w < 3; w++)
            {
                var frames = new List<Geometry>();
                for (int k = 0; k < 9; k++) { double r = 14 + k * 14; frames.Add(Px.Ring(260, 5, r, r - 5)); }
                var p = FxPath(null, "#FFFFFF", 0);
                Px.PlayFrames(p, frames, 45, false, w * 150, true);
                Px.Steps(p, Anim.OP, w * 150, 0, 0.9, 135, 0.7, 270, 0.45, 360, 0.2);
            }
            Px.Steps(A("ZFill"), Anim.OP, 100, 0, 0.3, 100, 0.6, 200, 1);
            Px.Steps(A("ZMove"), Anim.TY, 100, 0, 16, 50, 12, 100, 8, 150, 4, 200, 0);
            FxDone(850);
        }

        // ---- 13. переворот: буква делает полный оборот рывками ----
        void FxFlip()
        {
            ZOn(0);
            Px.Steps(A("ZFlip"), Anim.SX, 0, 0, 1, 50, 0.66, 100, 0.33, 150, 0.1, 200, -0.33, 250, -0.66, 300, -1, 350, -0.66, 400, -0.33, 450, 0.1, 500, 0.33, 550, 0.66, 600, 1);
            Px.Steps(A("ZMove"), Anim.TY, 0, 0, 0, 100, -6, 300, -10, 500, -6, 600, 0);
            Sparkle(FxC - 58, FxC - 46, "#FFFFFF", 560);
            Sparkle(FxC + 60, FxC + 40, "#FFD166", 620);
            FxDone(900);
        }

        // ---- 14. падение: буква падает, сплющивается, летит пыль ----
        void FxDrop()
        {
            ZOn(0);
            Px.Steps(A("ZMove"), Anim.TY, 0, 0, -90, 50, -84, 100, -68, 150, -42, 200, -8, 220, 0);
            Px.Steps(A("ZFlip"), Anim.SX, 220, 0, 1.25, 70, 0.9, 140, 1);
            Px.Steps(A("ZFlip"), Anim.SY, 220, 0, 0.75, 70, 1.1, 140, 1);
            for (int i = 0; i < 6; i++)
            {
                double side = i < 3 ? -1 : 1, x0 = FxC + side * Rnd(14, 40);
                var r = FxBox(x0, ZO + 84, 6, "#8A90A0");
                var pts = new List<Point>();
                for (int k = 0; k < 5; k++) pts.Add(new Point(side * k * Rnd(5, 9), -k * Rnd(2, 5)));
                Path2(r, 220, 60, pts);
                Px.Steps(r, Anim.OP, 220, 0, 0.9, 180, 0.5, 300, 0);
            }
            FxDone(700);
        }

        // ---- 15. мозаика: сначала крупные блоки, потом всё мельче, пока не станет буквой ----
        void FxMosaic()
        {
            ZHidden();
            var mask = new bool[88, 88];
            foreach (var c in ZCells) for (int i = 0; i < 8; i++) for (int j = 0; j < 8; j++) mask[c.Key * 8 + i, c.Value * 8 + j] = true;
            var frames = new List<Geometry>();
            foreach (int b in new[] { 44, 22, 11 })
            {
                var blocks = new List<KV>(); int n = 88 / b;
                for (int bx = 0; bx < n; bx++) for (int by = 0; by < n; by++)
                {
                    int cnt = 0;
                    for (int i = 0; i < b; i++) for (int j = 0; j < b; j++) if (mask[bx * b + i, by * b + j]) cnt++;
                    if (cnt >= b * b * 0.42) blocks.Add(Px.C(bx, by));
                }
                frames.Add(Px.Cells(blocks, b, ZO, ZO));
            }
            Px.PlayFrames(FxPath(null, ZInk), frames, 110, false, 0, true);
            ZOn(330); ZBump(330);
            FxDone(500);
        }

        // ---- 16. пиксельный дождь: капли падают по столбцам и «зажигают» букву ----
        void FxRain()
        {
            ZHidden();
            var start = new int[11];
            for (int c = 0; c < 11; c++) start[c] = zRnd.Next(0, 6);
            var lit = new List<Geometry>(); var heads = new List<Geometry>();
            for (int f = 0; f < 19; f++)
            {
                var l = new List<KV>(); var h = new List<KV>();
                for (int c = 0; c < 11; c++)
                {
                    int hr = f - start[c];
                    foreach (var z in ZCells) if (z.Key == c && z.Value <= hr) l.Add(z);
                    if (hr >= 0 && hr <= 12) { if (hr <= 10) h.Add(Px.C(c, hr)); if (hr - 1 >= 0 && hr - 1 <= 10) h.Add(Px.C(c, hr - 1)); }
                }
                lit.Add(ZGeo(l)); heads.Add(ZGeo(h));
            }
            Px.PlayFrames(FxPath(null, ZInk), lit, 40, false, 0, true);
            Px.PlayFrames(FxPath(null, "#FFFFFF", 0.85), heads, 40, false, 0, true);
            ZOn(760);
            FxDone(850);
        }

        bool PlayFx(string name)
        {
            switch (name)
            {
                case "type": FxType(); return true;
                case "tetris": FxTetris(); return true;
                case "coin": FxCoin(); return true;
                case "dissolve": FxDissolve(); return true;
                case "glitch": FxGlitch(); return true;
                case "burst": FxBurst(); return true;
                case "crt": FxCrt(); return true;
                case "snake": FxSnake(); return true;
                case "lightning": FxLightning(); return true;
                case "warp": FxWarp(); return true;
                case "confetti": FxConfetti(); return true;
                case "sonar": FxSonar(); return true;
                case "flip": FxFlip(); return true;
                case "drop": FxDrop(); return true;
                case "mosaic": FxMosaic(); return true;
                case "rain": FxRain(); return true;
            }
            ZOn(0);
            return false;
        }
    }
}
