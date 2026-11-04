// Zapret (C#) — анимация буквы Z на кнопке включения (пиксельный стиль: всё движется кадрами)
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace ZapretCS
{
    partial class MainUi
    {
        bool zShown;

        // «змейка» при запуске всегда светлая, меняется только цвет самой буквы
        void SetZColor(string hex) { Anim.Color((Brush)Win.FindName("ZBrush"), hex, 200, true); }

        // пиксельный блик: полоса ступеньками пробегает по букве раз в несколько секунд
        void StartZShine()
        {
            if (Anim.Eco) return;
            var k = new DoubleAnimationUsingKeyFrames { Duration = new Duration(TimeSpan.FromMilliseconds(6500)), RepeatBehavior = RepeatBehavior.Forever };
            k.KeyFrames.Add(new DiscreteDoubleKeyFrame(-104, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            int t0 = 5400;
            for (int i = 0; i <= 26; i++) k.KeyFrames.Add(new DiscreteDoubleKeyFrame(-104 + i * 8, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t0 + i * 35))));
            k.BeginTime = TimeSpan.FromMilliseconds(-4600);   // первый блик почти сразу после включения
            A("ZShineT").BeginAnimation(Anim.TX, k);
        }
        void StopZShine() { A("ZShineT").BeginAnimation(Anim.TX, null); ((TranslateTransform)Win.FindName("ZShineT")).X = -104; }

        void StopZSnake() { var d = E<Path>("ZDraw"); d.BeginAnimation(Path.DataProperty, null); d.Data = null; Anim.Stop(d, Anim.OP); d.Opacity = 0; }

        void ResetZTransforms()
        {
            foreach (var n in new[] { "ZScale", "ZFlip" }) { Anim.Stop(A(n), Anim.SX); Anim.Stop(A(n), Anim.SY); ((ScaleTransform)A(n)).ScaleX = 1; ((ScaleTransform)A(n)).ScaleY = 1; }
            Anim.Stop(A("ZTilt"), Anim.Ang); ((RotateTransform)A("ZTilt")).Angle = 0;
            Anim.Stop(A("ZMove"), Anim.TX); Anim.Stop(A("ZMove"), Anim.TY); ((TranslateTransform)A("ZMove")).X = 0; ((TranslateTransform)A("ZMove")).Y = 0;
        }

        void ShowZ(bool spin)
        {
            if (!zShown)
            {
                zShown = true;
                // значок питания «рассыпается», на его месте появляется буква
                Px.Steps(A("IconWrap"), Anim.OP, 0, 0, 0.6, 60, 0.25, 120, 0);
                Px.Steps(A("PowerIconScale"), Anim.SX, 0, 0, 0.8, 60, 0.5); Px.Steps(A("PowerIconScale"), Anim.SY, 0, 0, 0.8, 60, 0.5);
                Anim.Stop(A("PowerIconRot"), Anim.Ang); ((RotateTransform)A("PowerIconRot")).Angle = 0;
                Anim.Stop(A("ZWrap"), Anim.OP); El("ZWrap").Opacity = 1;
                ResetZTransforms();
                Anim.Stop(A("ZFill"), Anim.OP); El("ZFill").Opacity = 0;
            }
            if (spin)
            {
                // запуск: буква еле видна, по ней бежит «змейка» из пикселей
                StopZShine(); FxClear();
                ResetZTransforms();
                Px.Steps(A("ZFill"), Anim.OP, 0, 0, 0.18);
                var d = E<Path>("ZDraw");
                Anim.Stop(d, Anim.OP); d.Opacity = 1;
                if (Anim.Eco) { d.BeginAnimation(Path.DataProperty, null); d.Data = null; }
                else Px.PlayFrames(d, zSnakeFrames, 40, true);
            }
            else PlayOn();
        }

        // ---- случайная анимация включения ----
        static readonly Random zRnd = new Random();
        int lastFx = -1;
        static readonly string[] FxNames = { "type", "tetris", "coin", "dissolve", "glitch", "burst", "crt", "snake",
            "lightning", "warp", "confetti", "sonar", "flip", "drop", "mosaic", "rain" };
        readonly List<int> fxBag = new List<int>();   // «мешок»: все анимации по разу в случайном порядке, потом заново

        void PlayOn()
        {
            StopZSnake();
            FxClear();
            ResetZTransforms();
            if (Anim.Eco) { Anim.Stop(A("ZFill"), Anim.OP); El("ZFill").Opacity = 1; return; }
            if (fxBag.Count == 0)
            {
                fxBag.AddRange(Enumerable.Range(0, FxNames.Length).OrderBy(x => zRnd.Next()));
                if (fxBag[0] == lastFx) { fxBag.Add(fxBag[0]); fxBag.RemoveAt(0); }
            }
            int fx = fxBag[0]; fxBag.RemoveAt(0);
            lastFx = fx;
            try { PlayFx(FxNames[fx]); }
            catch (Exception ex) { Log.Write("fx " + FxNames[fx] + ": " + ex.Message); FxClear(); Anim.Stop(A("ZFill"), Anim.OP); El("ZFill").Opacity = 1; }
            StartZShine();
        }

        void HideZ()
        {
            if (!zShown) return;
            zShown = false;
            StopZShine(); StopZSnake();
            FxClear();
            ResetZTransforms();
            // буква рассыпается на пиксели, на её место «впрыгивает» значок питания
            bool wasVisible = El("ZFill").Opacity > 0.5;
            Anim.Stop(A("ZFill"), Anim.OP); El("ZFill").Opacity = 0;
            if (wasVisible && !Anim.Eco)
            {
                var order = ZCells.OrderBy(c => zRnd.Next()).ToList();
                var frames = new List<Geometry>();
                for (int k = 6; k >= 1; k--) frames.Add(ZGeo(order.Take(order.Count * k / 7)));
                var p = FxPath(null, ZInk);
                Px.PlayFrames(p, frames, 45, false, 0, true);
                FxDone(400);
            }
            Px.Steps(A("ZWrap"), Anim.OP, 300, 0, 0);
            Anim.Stop(A("IconWrap"), Anim.OP); El("IconWrap").Opacity = 0;
            Px.Steps(A("IconWrap"), Anim.OP, 300, 0, 1);
            Px.Steps(A("PowerIconScale"), Anim.SX, 300, 0, 0.5, 60, 1.2, 120, 0.9, 180, 1);
            Px.Steps(A("PowerIconScale"), Anim.SY, 300, 0, 0.5, 60, 1.2, 120, 0.9, 180, 1);
        }
    }
}
