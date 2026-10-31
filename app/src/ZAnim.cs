// Zapret (C#) — анимация буквы Z на кнопке включения
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace ZapretCS
{
    partial class MainUi
    {
        const double ZLen = 95.4;   // длина контура Z в толщинах линии (периметр 286 / толщина 3)
        bool zShown;

        void SetZColor(string hex) { Anim.Color((Brush)Win.FindName("ZBrush"), hex, 300); Anim.Color((Brush)Win.FindName("ZStroke"), hex, 300); }
        void SetZDash(double a, double b) { E<Shape>("ZDraw").StrokeDashArray = new DoubleCollection(new[] { a, b }); }

        void StartZShine()
        {
            if (Anim.Eco) return;
            // блик пробегает по букве раз в несколько секунд
            var k = new DoubleAnimationUsingKeyFrames { Duration = new Duration(TimeSpan.FromMilliseconds(6500)), RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(k, 40);
            k.KeyFrames.Add(new DiscreteDoubleKeyFrame(-60, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            k.KeyFrames.Add(new DiscreteDoubleKeyFrame(-60, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(5600))));
            k.KeyFrames.Add(new EasingDoubleKeyFrame(110, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(6300)), Anim.Sine()));
            k.BeginTime = TimeSpan.FromMilliseconds(-5000);   // первый блик почти сразу после включения
            A("ZShineT").BeginAnimation(Anim.TX, k);
        }
        void StopZShine() { A("ZShineT").BeginAnimation(Anim.TX, null); ((TranslateTransform)Win.FindName("ZShineT")).X = -60; }

        void ShowZ(bool spin)
        {
            var draw = E<Shape>("ZDraw");
            draw.Stroke = (Brush)Win.FindName("ZStroke");
            var off = Shape.StrokeDashOffsetProperty;
            if (!zShown)
            {
                zShown = true;
                Anim.D(A("IconWrap"), Anim.OP, 0, 200);
                Anim.D(A("PowerIconScale"), Anim.SX, 0.3, 260); Anim.D(A("PowerIconScale"), Anim.SY, 0.3, 260);
                Anim.D(A("PowerIconRot"), Anim.Ang, 120, 260);
                Anim.D(A("ZWrap"), Anim.OP, 1, 200, 0, begin: 100);
                Anim.D(A("ZScale"), Anim.SX, 1, 520, 0.6, begin: 100, ease: Anim.Back());
                Anim.D(A("ZScale"), Anim.SY, 1, 520, 0.6, begin: 100, ease: Anim.Back());
                Anim.Stop(A("ZFill"), Anim.OP); El("ZFill").Opacity = 0;
            }
            if (spin)
            {
                // запуск: по контуру Z бегает «змейка», сама буква еле видна
                StopZShine();
                Anim.D(A("ZFill"), Anim.OP, 0.18, 250);
                SetZDash(20, ZLen - 20);
                Anim.Stop(draw, Anim.OP); draw.Opacity = 1;
                Anim.D(draw, off, -ZLen, 1000, 0, forever: true, noEase: true);
            }
            else PlayOn();
        }

        // ---- случайная анимация включения ----
        static readonly Random zRnd = new Random();
        int lastFx = -1;
        static readonly string[] FxNames = { "draw", "flip", "drop", "twist", "glitch", "shock", "jelly",
            "assemble", "supernova", "lightning", "shards", "sonar", "spiral", "scanner", "portal", "confetti" };
        readonly List<int> fxBag = new List<int>();   // «мешок»: все анимации по разу в случайном порядке, потом заново

        // ключевые кадры: пары (мс, значение)
        void KF(string name, DependencyProperty p, int begin, params double[] tv)
        {
            var t = A(name); if (t == null) return;
            if (Anim.Eco) { t.BeginAnimation(p, null); ((DependencyObject)t).SetValue(p, tv[tv.Length - 1]); return; }
            var a = new DoubleAnimationUsingKeyFrames();
            if (begin > 0) a.BeginTime = TimeSpan.FromMilliseconds(begin);
            for (int i = 0; i + 1 < tv.Length; i += 2)
                a.KeyFrames.Add(new EasingDoubleKeyFrame(tv[i + 1], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(tv[i])), new CubicEase { EasingMode = EasingMode.EaseOut }));
            t.BeginAnimation(p, a);
        }

        void PlayOn()
        {
            var draw = E<Shape>("ZDraw");
            var off = Shape.StrokeDashOffsetProperty;
            Anim.Stop(draw, off);
            foreach (var n in new[] { "ZFlip" }) { Anim.Stop(A(n), Anim.SX); Anim.Stop(A(n), Anim.SY); }
            Anim.Stop(A("ZMove"), Anim.TX); Anim.Stop(A("ZMove"), Anim.TY); Anim.Stop(A("ZTilt"), Anim.Ang);
            if (fxBag.Count == 0)
            {
                fxBag.AddRange(Enumerable.Range(0, FxNames.Length).OrderBy(x => zRnd.Next()));
                if (fxBag[0] == lastFx) { fxBag.Add(fxBag[0]); fxBag.RemoveAt(0); }
            }
            int fx = fxBag[0]; fxBag.RemoveAt(0);
            lastFx = fx;
            if (Anim.Eco) fx = 0;
            FxClear(); El("ZWrap").OpacityMask = null;
            Anim.Stop(A("ZMove"), Anim.TY); ((TranslateTransform)A("ZMove")).Y = 0; ((TranslateTransform)A("ZMove")).X = 0;
            if (PlayFx(FxNames[fx])) { StartZShine(); return; }
            switch (FxNames[fx])
            {
                case "flip":    // буква делает оборот вокруг своей оси
                    Anim.D(A("ZFill"), Anim.OP, 1, 200);
                    Anim.D(draw, Anim.OP, 0, 150);
                    KF("ZFlip", Anim.SX, 0, 0, 1, 160, 0, 320, -1, 480, 0, 640, 1);
                    KF("ZScale", Anim.SX, 0, 0, 1, 320, 1.18, 760, 1); KF("ZScale", Anim.SY, 0, 0, 1, 320, 1.18, 760, 1);
                    break;
                case "drop":    // падает сверху и пружинит
                    Anim.D(A("ZFill"), Anim.OP, 1, 160);
                    Anim.D(draw, Anim.OP, 0, 150);
                    Anim.D(A("ZScale"), Anim.SX, 1, 200); Anim.D(A("ZScale"), Anim.SY, 1, 200);
                    Anim.D(A("ZMove"), Anim.TY, 0, 420, -70, ease: new QuadraticEase { EasingMode = EasingMode.EaseIn });
                    KF("ZFlip", Anim.SX, 420, 0, 1, 90, 1.25, 230, 0.92, 380, 1);
                    KF("ZFlip", Anim.SY, 420, 0, 1, 90, 0.75, 230, 1.08, 380, 1);
                    break;
                case "twist":   // влетает из глубины с закруткой
                    Anim.D(A("ZFill"), Anim.OP, 1, 250);
                    Anim.D(draw, Anim.OP, 0, 150);
                    Anim.D(A("ZScale"), Anim.SX, 1, 620, 2.3, ease: Anim.Back()); Anim.D(A("ZScale"), Anim.SY, 1, 620, 2.3, ease: Anim.Back());
                    Anim.D(A("ZTilt"), Anim.Ang, 0, 700, -220, ease: new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 });
                    Anim.D(A("ZWrap"), Anim.OP, 1, 300, 0);
                    break;
                case "glitch":  // цифровой сбой: дёргается и мерцает, потом собирается
                    KF("ZFill", Anim.OP, 0, 0, 0.2, 60, 1, 110, 0.35, 170, 1, 230, 0.55, 320, 1);
                    KF("ZMove", Anim.TX, 0, 0, -7, 60, 6, 120, -4, 180, 5, 240, -2, 320, 0);
                    KF("ZFlip", Anim.SX, 0, 0, 1.15, 120, 0.9, 240, 1.05, 320, 1);
                    Anim.D(draw, Anim.OP, 0, 120);
                    Anim.D(A("ZScale"), Anim.SX, 1, 300); Anim.D(A("ZScale"), Anim.SY, 1, 300);
                    break;
                case "shock":   // вспышка: буква выстреливает, кнопку озаряет свет
                    Anim.D(A("ZFill"), Anim.OP, 1, 120);
                    Anim.D(draw, Anim.OP, 0, 120);
                    KF("ZScale", Anim.SX, 0, 0, 0.2, 230, 1.32, 520, 1); KF("ZScale", Anim.SY, 0, 0, 0.2, 230, 1.32, 520, 1);
                    KF("PowerShadow", DropShadowEffect.BlurRadiusProperty, 0, 0, 10, 180, 95, 900, 40);
                    break;
                case "jelly":   // желе: покачивается, как мармелад
                    Anim.D(A("ZFill"), Anim.OP, 1, 200);
                    Anim.D(draw, Anim.OP, 0, 150);
                    Anim.D(A("ZScale"), Anim.SX, 1, 200); Anim.D(A("ZScale"), Anim.SY, 1, 200);
                    KF("ZFlip", Anim.SX, 0, 0, 1, 120, 1.28, 260, 0.78, 400, 1.14, 540, 0.94, 700, 1);
                    KF("ZFlip", Anim.SY, 0, 0, 1, 120, 0.76, 260, 1.24, 400, 0.88, 540, 1.05, 700, 1);
                    break;
                default:        // контур «дорисовывается», буква заливается и подпрыгивает
                    SetZDash(ZLen, ZLen);
                    Anim.Stop(draw, Anim.OP); draw.Opacity = 1;
                    Anim.D(draw, off, 0, 480, ZLen);
                    Anim.D(A("ZFill"), Anim.OP, 1, 260, null, begin: 380);
                    Anim.D(draw, Anim.OP, 0, 300, null, begin: 620);
                    Anim.D(A("ZScale"), Anim.SX, 1, 520, 1.16, begin: 380, ease: Anim.Back());
                    Anim.D(A("ZScale"), Anim.SY, 1, 520, 1.16, begin: 380, ease: Anim.Back());
                    break;
            }
            StartZShine();
        }

        void HideZ()
        {
            if (!zShown) return;
            zShown = false;
            StopZShine();
            FxClear(); El("ZWrap").OpacityMask = null;
            Anim.D(A("ZTilt"), Anim.Ang, 0, 200);
            // буква «стирается» по контуру и сжимается, на её место возвращается значок питания
            var draw = E<Shape>("ZDraw");
            Anim.D(A("ZFill"), Anim.OP, 0, 180);
            SetZDash(ZLen, ZLen);
            Anim.Stop(draw, Anim.OP); draw.Opacity = 1;
            Anim.D(draw, Shape.StrokeDashOffsetProperty, -ZLen, 380, 0);
            Anim.D(A("ZScale"), Anim.SX, 0.3, 300, null, begin: 220); Anim.D(A("ZScale"), Anim.SY, 0.3, 300, null, begin: 220);
            Anim.D(A("ZWrap"), Anim.OP, 0, 240, null, begin: 260);
            Anim.D(A("IconWrap"), Anim.OP, 1, 300, 0, begin: 380);
            Anim.D(A("PowerIconScale"), Anim.SX, 1, 520, 0.3, begin: 380, ease: Anim.Back());
            Anim.D(A("PowerIconScale"), Anim.SY, 1, 520, 0.3, begin: 380, ease: Anim.Back());
            Anim.D(A("PowerIconRot"), Anim.Ang, 0, 520, -120, begin: 380);
        }
    }
}
