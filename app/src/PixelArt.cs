// Zapret (C#) — пиксельный стиль: шрифт, «пиксельные» кольца и диски, ступенчатые углы,
// покадровые (рывками) анимации
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace ZapretCS
{
    // значение анимации меняется не плавно, а кадрами — как в старых играх
    sealed class PxSteps : IEasingFunction
    {
        readonly IEasingFunction inner; readonly int n;
        public PxSteps(IEasingFunction inner, int frames) { this.inner = inner; n = Math.Max(1, frames); }
        public double Ease(double t)
        {
            double q = t <= 0 ? 0 : t >= 1 ? 1 : Math.Ceiling(t * n) / n;
            return inner == null ? q : inner.Ease(q);
        }
    }

    static class Px
    {
        public const int FrameMs = 50;   // 20 кадров в секунду для интерфейса
        public static int Frames(int ms) { return Math.Max(1, (int)Math.Round(ms / (double)FrameMs)); }

        // ---- шрифт ZPixel (Pixelify Sans, лицензия OFL) ----
        // WPF не умеет брать шрифт прямо из ресурса программы, поэтому кладём файл в папку,
        // куда может писать только администратор (Zapret работает с правами администратора)
        public static string PrepareFont(string appDir)
        {
            try
            {
                byte[] data;
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ZPixel.ttf"))
                using (var ms = new MemoryStream()) { s.CopyTo(ms); data = ms.ToArray(); }
                string hash;
                using (var sha = SHA256.Create()) hash = string.Concat(sha.ComputeHash(data).Take(5).Select(b => b.ToString("x2")));
                string dir = System.IO.Path.Combine(appDir, "font");
                Sec.ProtectedDir(dir);
                string file = System.IO.Path.Combine(dir, "ZPixel-" + hash + ".ttf");
                if (!File.Exists(file) || new FileInfo(file).Length != data.Length || Sec.Sha256(file).Substring(0, 10) != hash)
                    File.WriteAllBytes(file, data);
                foreach (var old in Directory.GetFiles(dir, "ZPixel-*.ttf"))
                    if (!string.Equals(old, file, StringComparison.OrdinalIgnoreCase)) try { File.Delete(old); } catch { }
                string uri = new Uri(dir.TrimEnd('\\') + "\\").AbsoluteUri;
                return System.Security.SecurityElement.Escape(uri + "#ZPixel");
            }
            catch (Exception ex) { Log.Write("font: " + ex.Message); return "Segoe UI"; }
        }

        // ---- геометрия из клеток ----
        // клетки одной строки склеиваются в один прямоугольник, чтобы фигура была лёгкой
        public static Geometry Cells(IEnumerable<KeyValuePair<int, int>> cells, double cell, double ox = 0, double oy = 0)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                foreach (var row in cells.GroupBy(p => p.Value).OrderBy(r => r.Key))
                {
                    var xs = row.Select(p => p.Key).Distinct().OrderBy(v => v).ToList();
                    for (int i = 0; i < xs.Count; )
                    {
                        int j = i; while (j + 1 < xs.Count && xs[j + 1] == xs[j] + 1) j++;
                        double x0 = ox + xs[i] * cell, x1 = ox + (xs[j] + 1) * cell, y0 = oy + row.Key * cell, y1 = y0 + cell;
                        c.BeginFigure(new Point(x0, y0), true, true);
                        c.PolyLineTo(new[] { new Point(x1, y0), new Point(x1, y1), new Point(x0, y1) }, false, false);
                        i = j + 1;
                    }
                }
            }
            g.Freeze();
            return g;
        }
        public static KeyValuePair<int, int> C(int x, int y) { return new KeyValuePair<int, int>(x, y); }

        // клетки кольца (или диска при ri = 0) на квадрате size×size
        public static List<KeyValuePair<int, int>> RingCells(double size, double cell, double ro, double ri, double fromDeg = 0, double spanDeg = 360)
        {
            var res = new List<KeyValuePair<int, int>>();
            double c = size / 2; int n = (int)Math.Round(size / cell);
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    double dx = i * cell + cell / 2 - c, dy = j * cell + cell / 2 - c;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < ri || d > ro) continue;
                    if (spanDeg < 360)
                    {
                        double a = (Math.Atan2(dy, dx) * 180 / Math.PI + 450) % 360;   // 0° — сверху, по часовой
                        double rel = (a - fromDeg + 720) % 360;
                        if (rel > spanDeg) continue;
                    }
                    res.Add(C(i, j));
                }
            return res;
        }
        public static Geometry Ring(double size, double cell, double ro, double ri, double fromDeg = 0, double spanDeg = 360)
        {
            return Cells(RingCells(size, cell, ro, ri, fromDeg, spanDeg), cell);
        }

        // ---- ступенчатые углы у прямоугольных панелей ----
        public static void StepCorners(FrameworkElement el, double s)
        {
            if (el == null) return;
            SizeChangedEventHandler h = (o, e) =>
            {
                double w = el.ActualWidth, hh = el.ActualHeight;
                if (w < 5 * s || hh < 5 * s) { el.Clip = null; return; }
                var pts = new[] {
                    new Point(0, 2*s), new Point(s, 2*s), new Point(s, s), new Point(2*s, s), new Point(2*s, 0),
                    new Point(w-2*s, 0), new Point(w-2*s, s), new Point(w-s, s), new Point(w-s, 2*s), new Point(w, 2*s),
                    new Point(w, hh-2*s), new Point(w-s, hh-2*s), new Point(w-s, hh-s), new Point(w-2*s, hh-s), new Point(w-2*s, hh),
                    new Point(2*s, hh), new Point(2*s, hh-s), new Point(s, hh-s), new Point(s, hh-2*s), new Point(0, hh-2*s) };
                var g = new StreamGeometry();
                using (var c = g.Open()) { c.BeginFigure(pts[0], true, true); c.PolyLineTo(pts.Skip(1).ToList(), false, false); }
                g.Freeze();
                el.Clip = g;
            };
            el.SizeChanged += h;
            if (el.ActualWidth > 0) h(null, null);
        }

        // ---- покадровая смена фигуры (Path.Data) ----
        public static void PlayFrames(Path p, IList<Geometry> frames, int frameMs, bool forever, int begin = 0, bool clearAtEnd = false)
        {
            if (p == null) return;
            p.BeginAnimation(Path.DataProperty, null);
            if (Anim.Eco || frames.Count == 0) { if (frames.Count > 0) p.Data = clearAtEnd ? null : frames[frames.Count - 1]; return; }
            var a = new ObjectAnimationUsingKeyFrames();
            if (begin > 0) a.BeginTime = TimeSpan.FromMilliseconds(begin);
            for (int i = 0; i < frames.Count; i++)
                a.KeyFrames.Add(new DiscreteObjectKeyFrame(frames[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(i * frameMs))));
            if (clearAtEnd) a.KeyFrames.Add(new DiscreteObjectKeyFrame(null, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(frames.Count * frameMs))));
            a.Duration = new Duration(TimeSpan.FromMilliseconds((frames.Count + (clearAtEnd ? 1 : 0)) * frameMs));
            if (forever) a.RepeatBehavior = RepeatBehavior.Forever;
            p.BeginAnimation(Path.DataProperty, a);
        }

        // ключевые кадры без плавности: пары (мс, значение)
        public static void Steps(IAnimatable t, DependencyProperty p, int begin, params double[] tv)
        {
            if (t == null) return;
            if (Anim.Eco) { t.BeginAnimation(p, null); ((DependencyObject)t).SetValue(p, tv[tv.Length - 1]); return; }
            var a = new DoubleAnimationUsingKeyFrames();
            if (begin > 0) a.BeginTime = TimeSpan.FromMilliseconds(begin);
            for (int i = 0; i + 1 < tv.Length; i += 2)
                a.KeyFrames.Add(new DiscreteDoubleKeyFrame(tv[i + 1], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(tv[i]))));
            t.BeginAnimation(p, a);
        }
    }

    partial class MainUi
    {
        // буква Z на сетке 11×11 (клетка 8 px), такая же, как в разметке
        static readonly string[] ZRows = {
            "###########", "###########", ".......###.", "......###..", ".....###...", "....###....",
            "...###.....", "..###......", ".###.......", "###########", "###########" };
        static List<KeyValuePair<int, int>> zCells;
        static List<KeyValuePair<int, int>> ZCells
        {
            get
            {
                if (zCells == null)
                {
                    zCells = new List<KeyValuePair<int, int>>();
                    for (int y = 0; y < ZRows.Length; y++) for (int x = 0; x < ZRows[y].Length; x++) if (ZRows[y][x] == '#') zCells.Add(Px.C(x, y));
                }
                return zCells;
            }
        }
        // порядок «письма» буквы: верхняя черта слева направо, диагональ вниз, нижняя черта
        static double ZStrokePos(KeyValuePair<int, int> c)
        {
            int x = c.Key, y = c.Value;
            if (y <= 1) return x;
            if (y >= 9) return 19 + x;
            return 11 + (y - 2) + 1;
        }
        const double ZCell = 8, ZOff = 56;   // клетка буквы и её отступ внутри кнопки 200×200

        List<Geometry> spinFrames, glowFrames, auFrames, zSnakeFrames;

        void InitPixelArt()
        {
            // кнопка включения: внешнее кольцо, тёмное кольцо, диск, светлый ободок
            E<Path>("PxOut").Data = Px.Ring(200, 5, 99, 95);
            E<Path>("PxDark").Data = Px.Ring(200, 5, 91, 87);
            E<Path>("PxDisc").Data = Px.Ring(200, 5, 80, 0);
            E<Path>("PxRim").Data = Px.Ring(200, 5, 80, 75);

            // кадры «бегущей дуги» при запуске: 16 кадров по кругу
            spinFrames = new List<Geometry>();
            for (int k = 0; k < 16; k++) spinFrames.Add(Px.Ring(200, 5, 92, 86, k * 22.5, 90));
            // кадры волны вокруг кнопки (слой 260×260)
            glowFrames = new List<Geometry>();
            for (int k = 0; k < 6; k++) { double r = 104 + k * 5; glowFrames.Add(Px.Ring(260, 5, r, r - 5)); }
            // «змейка» по букве Z
            zSnakeFrames = new List<Geometry>();
            for (int k = -4; k < 30; k += 1)
            {
                int kk = k;
                zSnakeFrames.Add(Px.Cells(ZCells.Where(c => { double s = ZStrokePos(c); return s >= kk && s < kk + 5; }), ZCell));
            }
            // блик: диагональная полоса из клеток
            var band = new List<KeyValuePair<int, int>>();
            for (int y = 0; y < 11; y++) { band.Add(Px.C(11 - y, y)); band.Add(Px.C(12 - y, y)); }
            E<Path>("ZShine").Data = Px.Cells(band, ZCell);

            // Telegram: пиксельный диск и кольцо
            E<Path>("TgDot").Data = Px.Ring(56, 4, 26, 0);
            E<Path>("TgGlow").Data = Px.Ring(80, 4, 40, 36);
            ((FrameworkElement)E<Path>("TgGlow")).RenderTransformOrigin = new Point(0.5, 0.5);
            var tgs = new ScaleTransform(1, 1); E<Path>("TgGlow").RenderTransform = tgs; Win.RegisterName("TgGlowS", tgs);

            // окно обновления приложения
            E<Path>("AuRing").Data = Px.Ring(96, 4, 48, 42);
            E<Path>("AuOkDisc").Data = Px.Ring(96, 4, 48, 0);
            E<Path>("AuBurst").Data = Px.Ring(96, 4, 48, 44);
            auFrames = new List<Geometry>();
            for (int k = 0; k < 12; k++) auFrames.Add(Px.Ring(96, 4, 48, 42, k * 30, 80));
            E<Path>("AuArc").Data = auFrames[0];

            // ступенчатые углы
            Px.StepCorners(El("ShellBody"), 4);
            Px.StepCorners(El("ShellShadow"), 4);
            Px.StepCorners(El("SitesCard"), 4);
            Px.StepCorners(El("TgCard"), 4);
        }

        void StartSpinner()
        {
            var sp = E<Path>("Spinner");
            Anim.Stop(sp, Anim.OP); sp.Opacity = 1;
            if (Anim.Eco) { sp.BeginAnimation(Path.DataProperty, null); sp.Data = spinFrames[0]; return; }
            Px.PlayFrames(sp, spinFrames, 55, true);
        }
        void StopSpinner()
        {
            var sp = E<Path>("Spinner");
            sp.BeginAnimation(Path.DataProperty, null); sp.Data = null;
            Anim.Stop(sp, Anim.OP); sp.Opacity = 0;
        }
        void StartAuSpinner() { var p = E<Path>("AuArc"); if (Anim.Eco) { p.BeginAnimation(Path.DataProperty, null); p.Data = auFrames[0]; } else Px.PlayFrames(p, auFrames, 70, true); }
        void StopAuSpinner() { E<Path>("AuArc").BeginAnimation(Path.DataProperty, null); }

        // цвета кнопки включения
        void SetPowerColors(bool on, int ms = 300)
        {
            Anim.Color((Brush)Win.FindName("PowerFill"), on ? "#3DDC97" : "#262A33", ms, true);
            Anim.Color((Brush)Win.FindName("PxOutB"), on ? "#3DDC97" : "#2E3340", ms, true);
            Anim.Color((Brush)Win.FindName("PxDarkB"), on ? "#1B3329" : "#1E2129", ms, true);
            Anim.Color((Brush)Win.FindName("PxRimB"), on ? "#7FE8B8" : "#2E3340", ms, true);
        }
    }
}
