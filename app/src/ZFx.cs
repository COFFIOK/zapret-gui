// Zapret (C#) — эффектные анимации включения: частицы, молнии, осколки, портал, сканер…
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace ZapretCS
{
    partial class MainUi
    {
        // слой эффектов 260×260, центр совпадает с центром кнопки
        const double FxC = 130, ZK = 78.0 / 80.0;
        static readonly double[] ZPoly = { 16, 14, 64, 14, 64, 26, 34, 54, 64, 54, 64, 66, 16, 66, 16, 54, 46, 26, 16, 26 };
        Canvas Fx { get { return E<Canvas>("FxLayer"); } }
        static Point ZP(double x, double y) { return new Point(FxC + (x - 40) * ZK, FxC + (y - 40) * ZK); }
        static double Rnd(double a, double b) { return a + zRnd.NextDouble() * (b - a); }
        static IEasingFunction EOut(double p = 5) { return new PowerEase { EasingMode = EasingMode.EaseOut, Power = p }; }
        static IEasingFunction EIn(double p = 3) { return new PowerEase { EasingMode = EasingMode.EaseIn, Power = p }; }

        // точки, равномерно расставленные по контуру буквы Z
        static List<Point> ZOutline(int n)
        {
            var pts = new List<Point>();
            for (int i = 0; i < ZPoly.Length; i += 2) pts.Add(ZP(ZPoly[i], ZPoly[i + 1]));
            pts.Add(pts[0]);
            double total = 0; for (int i = 1; i < pts.Count; i++) total += (pts[i] - pts[i - 1]).Length;
            var res = new List<Point>(); double step = total / n, acc = 0; int seg = 1; double segPos = 0;
            for (int k = 0; k < n; k++)
            {
                double target = k * step;
                while (seg < pts.Count && acc + (pts[seg] - pts[seg - 1]).Length < target) { acc += (pts[seg] - pts[seg - 1]).Length; seg++; }
                if (seg >= pts.Count) break;
                segPos = (target - acc) / (pts[seg] - pts[seg - 1]).Length;
                res.Add(pts[seg - 1] + (pts[seg] - pts[seg - 1]) * segPos);
            }
            return res;
        }

        T FxAdd<T>(T e, Point at) where T : FrameworkElement
        {
            Canvas.SetLeft(e, at.X - e.Width / 2); Canvas.SetTop(e, at.Y - e.Height / 2);
            Fx.Children.Add(e);
            return e;
        }
        void FxGlow(string hex, double blur = 10)
        {
            Fx.Effect = new DropShadowEffect { Color = Theme.C(hex), BlurRadius = blur, ShadowDepth = 0, Opacity = 0.9 };
        }
        // убрать элементы эффекта, когда он доиграет
        void FxDone(int ms)
        {
            var c = Fx; var mine = c.Children.Cast<UIElement>().ToList();
            After(ms, () => { foreach (var e in mine) c.Children.Remove(e); if (c.Children.Count == 0) c.Effect = null; });
        }
        void FxClear() { Fx.Children.Clear(); Fx.Effect = null; }

        // Z пока скрыта (будет «собрана» эффектом)
        void ZHidden()
        {
            Anim.Stop(A("ZFill"), Anim.OP); El("ZFill").Opacity = 0;
            var d = E<Shape>("ZDraw"); Anim.Stop(d, Anim.OP); d.Opacity = 0;
            Anim.Stop(A("ZScale"), Anim.SX); Anim.Stop(A("ZScale"), Anim.SY);
            ((ScaleTransform)A("ZScale")).ScaleX = 1; ((ScaleTransform)A("ZScale")).ScaleY = 1;
        }
        // Z появляется с толчком
        void ZPop(int begin, double from = 1.3, double amp = 0.6)
        {
            Anim.D(A("ZFill"), Anim.OP, 1, 140, 0, begin: begin);
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = amp };
            Anim.D(A("ZScale"), Anim.SX, 1, 560, from, begin: begin, ease: ease);
            Anim.D(A("ZScale"), Anim.SY, 1, 560, from, begin: begin, ease: ease);
        }
        Ellipse Flash(Point at, double size, string hex, int begin, int ms, double to = 6, double op = 0.9)
        {
            var e = new Ellipse { Width = size, Height = size, Fill = Theme.B(hex), Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5) };
            var st = new ScaleTransform(1, 1); e.RenderTransform = st;
            FxAdd(e, at);
            Anim.D(e, Anim.OP, 0, ms, op, begin: begin, ease: EIn(2));
            Anim.D(st, Anim.SX, to, ms, 1, begin: begin, ease: EOut(3)); Anim.D(st, Anim.SY, to, ms, 1, begin: begin, ease: EOut(3));
            return e;
        }

        // ---- 1. частицы слетаются и складываются в букву ----
        void FxAssemble()
        {
            FxClear(); ZHidden(); FxGlow("#FFFFFF", 6);
            var targets = ZOutline(54);
            int i = 0;
            foreach (var p in targets)
            {
                double ang = Rnd(0, Math.PI * 2), dist = Rnd(95, 135), size = Rnd(3.2, 5.2);
                var d = new Ellipse { Width = size, Height = size, Fill = Theme.B(i % 5 == 0 ? Theme.Accent : "#FFFFFF") };
                var tt = new TranslateTransform(Math.Cos(ang) * dist, Math.Sin(ang) * dist); d.RenderTransform = tt;
                FxAdd(d, p);
                int b = i * 7, ms = (int)Rnd(520, 700);
                Anim.D(d, Anim.OP, 1, 200, 0, begin: b);
                Anim.D(tt, Anim.TX, 0, ms, tt.X, begin: b, ease: EOut(4));
                Anim.D(tt, Anim.TY, 0, ms, tt.Y, begin: b, ease: EOut(4));
                Anim.D(d, Anim.OP, 0, 260, null, begin: 980);
                i++;
            }
            ZPop(900, 1.08, 0.4);
            Flash(new Point(FxC, FxC), 60, "#FFFFFF", 880, 420, 2.4, 0.35);
            FxDone(1400);
        }

        // ---- 2. сверхновая: вспышка, искры во все стороны ----
        void FxSupernova()
        {
            FxClear(); ZHidden(); FxGlow(Theme.Accent, 12);
            Flash(new Point(FxC, FxC), 24, "#FFFFFF", 0, 520, 7, 0.95);
            for (int i = 0; i < 30; i++)
            {
                double ang = i * Math.PI * 2 / 30 + Rnd(-0.1, 0.1), dist = Rnd(85, 128);
                var r = new Rectangle { Width = Rnd(2, 3.2), Height = Rnd(10, 18), RadiusX = 1.5, RadiusY = 1.5, Fill = Theme.B(i % 3 == 0 ? "#FFFFFF" : Theme.Accent), Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5) };
                var tg = new TransformGroup(); tg.Children.Add(new RotateTransform(ang * 180 / Math.PI + 90)); var tt = new TranslateTransform(); tg.Children.Add(tt);
                r.RenderTransform = tg;
                FxAdd(r, new Point(FxC, FxC));
                int ms = (int)Rnd(560, 820);
                Anim.D(r, Anim.OP, 1, 60, 0, begin: 40);
                Anim.D(tt, Anim.TX, Math.Cos(ang) * dist, ms, Math.Cos(ang) * 18, begin: 40, ease: EOut(4));
                Anim.D(tt, Anim.TY, Math.Sin(ang) * dist, ms, Math.Sin(ang) * 18, begin: 40, ease: EOut(4));
                Anim.D(r, Anim.OP, 0, 380, null, begin: 40 + ms - 300);
            }
            ZPop(120, 0.2, 0.9);
            KF("ZTilt", Anim.Ang, 120, 0, 0, 90, -8, 200, 6, 320, -3, 440, 0);
            FxDone(1300);
        }

        // ---- 3. молнии бьют в кнопку ----
        Polyline Bolt(Point from, Point to, double jag)
        {
            var pl = new Polyline { Stroke = Theme.B("#FFFFFF"), StrokeThickness = 2.6, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Opacity = 0 };
            var v = to - from; var n = new Vector(-v.Y, v.X); n.Normalize();
            pl.Points.Add(from);
            int segs = 9;
            for (int k = 1; k < segs; k++) pl.Points.Add(from + v * ((double)k / segs) + n * Rnd(-jag, jag) * (1 - (double)k / segs * 0.5));
            pl.Points.Add(to);
            Fx.Children.Add(pl);
            return pl;
        }
        void FxLightning()
        {
            FxClear(); ZHidden(); FxGlow("#BFF6FF", 14);
            int[] begins = { 0, 110, 210 };
            for (int i = 0; i < 3; i++)
            {
                double ang = -Math.PI / 2 + Rnd(-0.75, 0.75);
                var from = new Point(FxC + Math.Cos(ang) * 135, FxC + Math.Sin(ang) * 135);
                var bolt = Bolt(from, new Point(FxC + Rnd(-10, 10), FxC + Rnd(-10, 10)), 14);
                KF2(bolt, Anim.OP, begins[i], 0, 1, 50, 0.15, 90, 1, 150, 0.3, 200, 1, 420, 0);
                // ответвление
                var mid = bolt.Points[4];
                var br = Bolt(mid, mid + new Vector(Rnd(-30, 30), Rnd(10, 30)), 6); br.StrokeThickness = 1.6;
                KF2(br, Anim.OP, begins[i] + 40, 0, 0.9, 60, 0.1, 110, 0.8, 300, 0);
            }
            Flash(new Point(FxC, FxC), 150, "#FFFFFF", 240, 380, 1.15, 0.75);
            ZPop(260, 1.4, 0.8);
            KF("ZMove", Anim.TX, 260, 0, -4, 50, 4, 100, -3, 150, 2, 200, 0);
            FxDone(1100);
        }
        // ключевые кадры для созданного в коде элемента
        static void KF2(IAnimatable t, DependencyProperty p, int begin, params double[] tv)
        {
            var a = new DoubleAnimationUsingKeyFrames();
            if (begin > 0) a.BeginTime = TimeSpan.FromMilliseconds(begin);
            for (int i = 0; i + 1 < tv.Length; i += 2) a.KeyFrames.Add(new LinearDoubleKeyFrame(tv[i + 1], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(tv[i]))));
            t.BeginAnimation(p, a);
        }

        // ---- 4. осколки слетаются и склеиваются ----
        void FxShards()
        {
            FxClear(); ZHidden(); FxGlow(Theme.Accent, 8);
            var targets = ZOutline(16);
            for (int i = 0; i < targets.Count; i++)
            {
                double s = Rnd(9, 17);
                var poly = new Polygon { Width = s, Height = s, Fill = Theme.B(i % 2 == 0 ? "#FFFFFF" : Theme.Accent), Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5) };
                poly.Points.Add(new Point(Rnd(0, s * 0.4), 0)); poly.Points.Add(new Point(s, Rnd(s * 0.3, s * 0.7))); poly.Points.Add(new Point(Rnd(0, s * 0.5), s));
                double ang = Rnd(0, Math.PI * 2), dist = Rnd(100, 140);
                var tg = new TransformGroup(); var sc = new ScaleTransform(1, 1); var rot = new RotateTransform(Rnd(-360, 360)); var tt = new TranslateTransform(Math.Cos(ang) * dist, Math.Sin(ang) * dist);
                tg.Children.Add(sc); tg.Children.Add(rot); tg.Children.Add(tt); poly.RenderTransform = tg;
                FxAdd(poly, targets[i]);
                int b = i * 18;
                Anim.D(poly, Anim.OP, 1, 160, 0, begin: b);
                Anim.D(tt, Anim.TX, 0, 620, tt.X, begin: b, ease: EOut(3)); Anim.D(tt, Anim.TY, 0, 620, tt.Y, begin: b, ease: EOut(3));
                Anim.D(rot, Anim.Ang, 0, 620, rot.Angle, begin: b, ease: EOut(3));
                Anim.D(sc, Anim.SX, 0.3, 620, 1, begin: b, ease: EIn(2)); Anim.D(sc, Anim.SY, 0.3, 620, 1, begin: b, ease: EIn(2));
                Anim.D(poly, Anim.OP, 0, 200, null, begin: b + 560);
            }
            ZPop(820, 1.25, 0.7);
            Flash(new Point(FxC, FxC), 80, "#FFFFFF", 800, 400, 2, 0.4);
            FxDone(1400);
        }

        // ---- 5. сонар: волны расходятся, буква всплывает ----
        void FxSonar()
        {
            FxClear(); ZHidden(); FxGlow(Theme.Accent, 8);
            for (int i = 0; i < 4; i++)
            {
                var ring = new Ellipse { Width = 40, Height = 40, Stroke = Theme.B(i % 2 == 0 ? "#FFFFFF" : Theme.Accent), StrokeThickness = 2.4, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5) };
                var st = new ScaleTransform(1, 1); ring.RenderTransform = st;
                FxAdd(ring, new Point(FxC, FxC));
                int b = i * 170;
                Anim.D(ring, Anim.OP, 0, 900, 0.95, begin: b, ease: EIn(1.6));
                Anim.D(st, Anim.SX, 6.2, 900, 1, begin: b, ease: EOut(2.5)); Anim.D(st, Anim.SY, 6.2, 900, 1, begin: b, ease: EOut(2.5));
            }
            Anim.D(A("ZFill"), Anim.OP, 1, 500, 0, begin: 120);
            Anim.D(A("ZMove"), Anim.TY, 0, 700, 22, begin: 120, ease: new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 });
            FxDone(1700);
        }

        // ---- 6. три кометы закручиваются в центр ----
        void FxSpiral()
        {
            FxClear(); ZHidden(); FxGlow("#FFFFFF", 10);
            var hub = new Canvas { Width = 0, Height = 0 };
            var tg = new TransformGroup(); var sc = new ScaleTransform(1, 1); var rot = new RotateTransform(0); tg.Children.Add(sc); tg.Children.Add(rot);
            hub.RenderTransform = tg;
            Canvas.SetLeft(hub, FxC); Canvas.SetTop(hub, FxC);
            Fx.Children.Add(hub);
            string[] cols = { "#FFFFFF", Theme.Accent, "#5ED3F3" };
            for (int i = 0; i < 3; i++)
            {
                double a = i * Math.PI * 2 / 3;
                for (int t = 0; t < 6; t++)   // хвост кометы
                {
                    double back = a - t * 0.09, size = 9 - t * 1.2;
                    var d = new Ellipse { Width = size, Height = size, Fill = Theme.B(cols[i]), Opacity = 1 - t * 0.15 };
                    Canvas.SetLeft(d, Math.Cos(back) * 105 - size / 2); Canvas.SetTop(d, Math.Sin(back) * 105 - size / 2);
                    hub.Children.Add(d);
                }
            }
            hub.Opacity = 0;
            Anim.D(hub, Anim.OP, 1, 150, 0);
            Anim.D(rot, Anim.Ang, 760, 820, 0, ease: EIn(2));
            Anim.D(sc, Anim.SX, 0.05, 820, 1, ease: EIn(2.4)); Anim.D(sc, Anim.SY, 0.05, 820, 1, ease: EIn(2.4));
            Anim.D(hub, Anim.OP, 0, 120, null, begin: 780);
            Flash(new Point(FxC, FxC), 40, "#FFFFFF", 790, 420, 4, 0.9);
            ZPop(800, 0.3, 1.0);
            FxDone(1400);
        }

        // ---- 7. сканер: луч проходит сверху вниз и «печатает» букву ----
        void FxScanner()
        {
            FxClear(); ZHidden(); FxGlow(Theme.Accent, 14);
            var wrap = El("ZWrap");
            var stop1 = new GradientStop(Colors.Black, 0); var stop2 = new GradientStop(Colors.Transparent, 0);
            var mask = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
            mask.GradientStops.Add(stop1); mask.GradientStops.Add(stop2);
            wrap.OpacityMask = mask;
            Anim.Stop(A("ZFill"), Anim.OP); El("ZFill").Opacity = 1;
            Anim.D(stop1, GradientStop.OffsetProperty, 1, 760, 0, begin: 80, ease: Anim.Sine());
            Anim.D(stop2, GradientStop.OffsetProperty, 1, 760, 0, begin: 80, ease: Anim.Sine());
            var top = ZP(40, 14).Y - 6; var bottom = ZP(40, 66).Y + 6;
            var beam = new Rectangle { Width = 110, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Opacity = 0 };
            var lg = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            lg.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0));
            lg.GradientStops.Add(new GradientStop(Colors.White, 0.5));
            lg.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1));
            beam.Fill = lg;
            var tt = new TranslateTransform(); beam.RenderTransform = tt;
            FxAdd(beam, new Point(FxC, top));
            Anim.D(beam, Anim.OP, 1, 120, 0);
            Anim.D(tt, Anim.TY, bottom - top, 840, 0, ease: Anim.Sine());
            Anim.D(beam, Anim.OP, 0, 180, null, begin: 840);
            After(900, () => { wrap.OpacityMask = null; });
            KF("ZScale", Anim.SX, 860, 0, 1, 140, 1.12, 360, 1); KF("ZScale", Anim.SY, 860, 0, 1, 140, 1.12, 360, 1);
            FxDone(1200);
        }

        // ---- 8. портал: кольцо раскрывается, буква выходит из него ----
        void FxPortal()
        {
            FxClear(); ZHidden(); FxGlow("#FFFFFF", 10);
            var ring = new Ellipse { Width = 120, Height = 120, Stroke = Theme.B("#FFFFFF"), StrokeThickness = 3, StrokeDashArray = new DoubleCollection { 5, 3 }, StrokeDashCap = PenLineCap.Round, RenderTransformOrigin = new Point(0.5, 0.5) };
            var tg = new TransformGroup(); var sc = new ScaleTransform(0, 0); var rot = new RotateTransform(); tg.Children.Add(sc); tg.Children.Add(rot); ring.RenderTransform = tg;
            FxAdd(ring, new Point(FxC, FxC));
            var ring2 = new Ellipse { Width = 96, Height = 96, Stroke = Theme.B(Theme.Accent), StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 2, 4 }, RenderTransformOrigin = new Point(0.5, 0.5) };
            var tg2 = new TransformGroup(); var sc2 = new ScaleTransform(0, 0); var rot2 = new RotateTransform(); tg2.Children.Add(sc2); tg2.Children.Add(rot2); ring2.RenderTransform = tg2;
            FxAdd(ring2, new Point(FxC, FxC));
            Anim.D(sc, Anim.SX, 1, 380, 0, ease: Anim.Back()); Anim.D(sc, Anim.SY, 1, 380, 0, ease: Anim.Back());
            Anim.D(sc2, Anim.SX, 1, 380, 0, begin: 60, ease: Anim.Back()); Anim.D(sc2, Anim.SY, 1, 380, 0, begin: 60, ease: Anim.Back());
            Anim.D(rot, Anim.Ang, 300, 1100, 0, ease: EOut(2)); Anim.D(rot2, Anim.Ang, -360, 1100, 0, ease: EOut(2));
            Anim.D(A("ZFill"), Anim.OP, 1, 200, 0, begin: 300);
            Anim.D(A("ZScale"), Anim.SX, 1, 600, 0, begin: 300, ease: new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.7 });
            Anim.D(A("ZScale"), Anim.SY, 1, 600, 0, begin: 300, ease: new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.7 });
            Anim.D(A("ZTilt"), Anim.Ang, 0, 700, 180, begin: 300, ease: EOut(3));
            Anim.D(sc, Anim.SX, 1.9, 420, null, begin: 760, ease: EIn(2)); Anim.D(sc, Anim.SY, 1.9, 420, null, begin: 760, ease: EIn(2));
            Anim.D(ring, Anim.OP, 0, 400, 1, begin: 760);
            Anim.D(sc2, Anim.SX, 0, 300, null, begin: 760, ease: EIn(2)); Anim.D(sc2, Anim.SY, 0, 300, null, begin: 760, ease: EIn(2));
            FxDone(1300);
        }

        // ---- 9. конфетти: буква выпрыгивает, во все стороны летят разноцветные кусочки ----
        void FxConfetti()
        {
            FxClear(); ZHidden();
            string[] cols = { "#FFFFFF", Theme.Accent, "#5ED3F3", "#FFD166", "#FF7AB6", "#B69CFF" };
            for (int i = 0; i < 36; i++)
            {
                double ang = Rnd(-Math.PI, 0), v = Rnd(70, 130);
                var r = new Rectangle { Width = Rnd(4, 7), Height = Rnd(6, 10), RadiusX = 1, RadiusY = 1, Fill = Theme.B(cols[i % cols.Length]), RenderTransformOrigin = new Point(0.5, 0.5), Opacity = 0 };
                var tg = new TransformGroup(); var rot = new RotateTransform(Rnd(0, 360)); var tt = new TranslateTransform(); tg.Children.Add(rot); tg.Children.Add(tt); r.RenderTransform = tg;
                FxAdd(r, new Point(FxC, FxC + 10));
                int ms = (int)Rnd(900, 1250);
                Anim.D(r, Anim.OP, 1, 80, 0, begin: 60);
                Anim.D(tt, Anim.TX, Math.Cos(ang) * v * 1.25, ms, 0, begin: 60, ease: EOut(2));
                // вверх, потом падение
                KF2(tt, Anim.TY, 60, 0, 0, ms * 0.35, Math.Sin(ang) * v, ms, Math.Sin(ang) * v + 110);
                Anim.D(rot, Anim.Ang, rot.Angle + Rnd(-540, 540), ms, null, begin: 60, noEase: true);
                Anim.D(r, Anim.OP, 0, 300, null, begin: 60 + ms - 300);
            }
            ZPop(40, 0.3, 1.1);
            FxDone(1500);
        }

        // все эффектные анимации (используются в PlayOn вместе с простыми)
        bool PlayFx(string name)
        {
            switch (name)
            {
                case "assemble": FxAssemble(); return true;
                case "supernova": FxSupernova(); return true;
                case "lightning": FxLightning(); return true;
                case "shards": FxShards(); return true;
                case "sonar": FxSonar(); return true;
                case "spiral": FxSpiral(); return true;
                case "scanner": FxScanner(); return true;
                case "portal": FxPortal(); return true;
                case "confetti": FxConfetti(); return true;
            }
            return false;
        }
    }
}
