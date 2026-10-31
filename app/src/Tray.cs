// Zapret (C#) — значок в трее, меню по правой кнопке, сворачивание в трей
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WF = System.Windows.Forms;
using SD = System.Drawing;

namespace ZapretCS
{
    partial class MainUi
    {
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

        WF.NotifyIcon trayIcon;
        SD.Icon trayOn, trayOff;
        ContextMenu trayMenu;

        void BuildTray()
        {
            trayIcon = new WF.NotifyIcon { Text = "Zapret" };
            try
            {
                if (icoDec != null) { trayOn = MakeTrayIcon(true); trayOff = MakeTrayIcon(false); }
            }
            catch (Exception ex) { Log.Write("tray icon: " + ex.Message); }
            trayIcon.Icon = trayOff ?? SD.SystemIcons.Application;

            trayMenu = (ContextMenu)XamlReader.Parse(Theme.Apply(Res("Tray.xaml")));
            var logo = trayMenu.FindName("TrLogo") as Image;
            if (logo != null && Win.Icon != null) logo.Source = Win.Icon;
            ((MenuItem)trayMenu.FindName("TrTg")).Click += (s, e) => Safe(() =>
            {
                var tgl = E<CheckBox>("TgToggle");
                if (!tgl.IsEnabled) return;
                tgl.IsChecked = tgl.IsChecked != true;
                if (tgl.IsChecked == true) StartTg(); else StopTg();
            });
            trayMenu.Opened += (s, e) =>
            {
                try { var src = PresentationSource.FromVisual(trayMenu) as HwndSource; if (src != null) SetForegroundWindow(src.Handle); } catch { }
            };
            ((MenuItem)trayMenu.FindName("TrZ")).Click += (s, e) => Safe(() =>
            {
                if (State == "on") { StopZapret(); HideApply(); } else if (State == "off") StartZapret();
                UpdateTrayMenu(false);
            });
            ((MenuItem)trayMenu.FindName("TrExit")).Click += (s, e) => { trayMenu.IsOpen = false; Win.Close(); };

            trayIcon.MouseUp += (s, e) =>
            {
                if (e.Button == WF.MouseButtons.Right) Safe(() => { UpdateTrayMenu(true); Translate.Tree(trayMenu); trayMenu.IsOpen = false; trayMenu.IsOpen = true; });
                else if (e.Button == WF.MouseButtons.Left) Safe(() => { trayMenu.IsOpen = false; ShowFromTray(); });
            };
            trayIcon.Visible = true;
            UpdateTrayIcon();
        }

        // значок: цветной с зелёной точкой (работает) или серый (выключен)
        SD.Icon MakeTrayIcon(bool on)
        {
            int sz = WF.SystemInformation.SmallIconSize.Width;
            var frames = icoDec.Frames.OrderBy(f => f.PixelWidth).ToList();
            BitmapFrame fr = frames.FirstOrDefault(f => f.PixelWidth >= sz * 2) ?? frames.Last();
            var dv = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
            using (var dc = dv.RenderOpen())
            {
                dc.DrawImage(fr, new Rect(0, 0, sz, sz));
                if (on)
                {
                    double d = Math.Round(sz * 0.46), r = d / 2; var c = new Point(sz - r, sz - r);
                    dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(15, 17, 21)), null, c, r, r);
                    dc.DrawEllipse(new SolidColorBrush(Theme.C("#3DDC97")), null, c, r - 1.6, r - 1.6);
                }
            }
            var rtb = new RenderTargetBitmap(sz, sz, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            BitmapSource src = rtb;
            if (!on)
            {
                int stride = sz * 4; var px = new byte[stride * sz]; rtb.CopyPixels(px, stride, 0);
                for (int i = 0; i < px.Length; i += 4)
                {
                    byte l = (byte)Math.Min(255, (0.11 * px[i] + 0.59 * px[i + 1] + 0.3 * px[i + 2]) * 0.9);
                    px[i] = l; px[i + 1] = l; px[i + 2] = l;
                }
                src = BitmapSource.Create(sz, sz, 96, 96, PixelFormats.Pbgra32, null, px, stride);
            }
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(src));
            var ms = new MemoryStream(); enc.Save(ms); ms.Position = 0;
            var bmp = new SD.Bitmap(ms);
            return SD.Icon.FromHandle(bmp.GetHicon());
        }

        void UpdateTrayIcon()
        {
            if (trayIcon == null) return;
            bool on = State == "on";
            var ic = on ? trayOn : trayOff;
            if (ic != null) trayIcon.Icon = ic;
            trayIcon.Text = Translate.T(on ? "Zapret · работает" : "Zapret · выключен");
        }

        void UpdateTrayMenu(bool instant)
        {
            if (trayMenu == null) return;
            bool zOn = State == "on" || State == "starting";
            ((TextBlock)trayMenu.FindName("TrSub")).Text = Translate.T(State == "on" ? "Работает" : State == "starting" ? "Запускаю…" : State == "stopping" ? "Выключаю…" : "Выключен");
            ((Shape)trayMenu.FindName("TrDot")).Fill = Theme.B(State == "on" ? "#3DDC97" : "#8A90A0");
            ((MenuItem)trayMenu.FindName("TrZ")).IsEnabled = State == "on" || State == "off";
            var track = (Border)trayMenu.FindName("TrZTrack");
            var knob = (TranslateTransform)trayMenu.FindName("TrZKnob");
            if (!(track.Background is SolidColorBrush) || track.Background.IsFrozen) track.Background = Theme.B("#2E3340");
            int ms = instant ? 1 : 180;
            Anim.Color(track.Background, zOn ? "#3DDC97" : "#2E3340", ms);
            Anim.D(knob, Anim.TX, zOn ? 16 : 0, ms);
            // Telegram
            var tgl = E<CheckBox>("TgToggle"); bool tOn = tgl.IsChecked == true;
            ((MenuItem)trayMenu.FindName("TrTg")).IsEnabled = tgl.IsEnabled;
            var ttrack = (Border)trayMenu.FindName("TrTgTrack");
            if (!(ttrack.Background is SolidColorBrush) || ttrack.Background.IsFrozen) ttrack.Background = Theme.B("#2E3340");
            Anim.Color(ttrack.Background, tOn ? "#5ED3F3" : "#2E3340", ms);
            Anim.D((TranslateTransform)trayMenu.FindName("TrTgKnob"), Anim.TX, tOn ? 16 : 0, ms);
        }

        public void ShowFromTray()
        {
            Win.ShowInTaskbar = true;
            Win.Show();
            if (Win.WindowState == WindowState.Minimized) Win.WindowState = WindowState.Normal;
            Win.Activate(); Win.Topmost = true; Win.Topmost = false;
            try { SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(Win).Handle); } catch { }
            Win.Focus();
            ResumeGlow();
            Mon.Wake();
        }
        public void HideToTray()
        {
            PauseGlow();
            Win.Hide();
            if (!S.Bool("trayHintShown"))
            {
                S.Set("trayHintShown", true); S.Save();
                try { trayIcon.ShowBalloonTip(4000, "Zapret", Translate.T("Zapret работает в трее. Нажми на значок, чтобы открыть, правая кнопка — меню."), WF.ToolTipIcon.None); } catch { }
            }
        }
    }
}
