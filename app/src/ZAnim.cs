// Zapret (C#) — анимация буквы Z на кнопке включения
using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
            else
            {
                // включился: контур «дорисовывается», буква заливается и подпрыгивает, пробегает блик
                SetZDash(ZLen, ZLen);
                Anim.Stop(draw, Anim.OP); draw.Opacity = 1;
                Anim.D(draw, off, 0, 480, ZLen);
                Anim.D(A("ZFill"), Anim.OP, 1, 260, null, begin: 380);
                Anim.D(draw, Anim.OP, 0, 300, null, begin: 620);
                Anim.D(A("ZScale"), Anim.SX, 1, 520, 1.16, begin: 380, ease: Anim.Back());
                Anim.D(A("ZScale"), Anim.SY, 1, 520, 1.16, begin: 380, ease: Anim.Back());
                StartZShine();
            }
        }

        void HideZ()
        {
            if (!zShown) return;
            zShown = false;
            StopZShine();
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
