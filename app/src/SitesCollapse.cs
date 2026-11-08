// Zapret (C#) — «Мои сайты» можно свернуть вправо в узкую вкладку и развернуть обратно
using System;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ZapretCS
{
    partial class MainUi
    {
        const double StripWidth = 52;
        bool sitesCollapsed, sitesAnimating;

        void WireSitesCollapse()
        {
            Btn("SitesCollapse").Click += (s, e) => Safe(() => SetSitesCollapsed(true, true));
            var strip = E<Border>("SitesStrip");
            strip.MouseLeftButtonUp += (s, e) => Safe(() => SetSitesCollapsed(false, true));
            // наведение на вкладку: рамка и стрелка подсвечиваются, стрелка «зовёт» влево
            strip.MouseEnter += (s, e) =>
            {
                strip.BorderBrush = (Brush)Win.FindResource("Accent");
                E<System.Windows.Shapes.Path>("StripChev").Fill = (Brush)Win.FindResource("Accent");
                Px.Steps(A("StripChevT"), TranslateTransform.XProperty, 0, 0, -1, 60, -2);
            };
            strip.MouseLeave += (s, e) =>
            {
                strip.BorderBrush = Theme.B("#262A33");
                E<System.Windows.Shapes.Path>("StripChev").Fill = Theme.B("#8A90A0");
                Px.Steps(A("StripChevT"), TranslateTransform.XProperty, 0, 0, -1, 60, 0);
            };

            // число сайтов и «нужен перезапуск» видны и на свёрнутой вкладке
            var count = E<TextBlock>("SitesCount");
            DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock)).AddValueChanged(count, (s, e) => UpdateStrip());
            DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(Border)).AddValueChanged(El("ApplyBar"), (s, e) => UpdateStrip());
            UpdateStrip();

            if (S.Bool("sitesCollapsed")) Win.Loaded += (s, e) => SetSitesCollapsed(true, false);
        }

        void UpdateStrip()
        {
            var m = Regex.Match(E<TextBlock>("SitesCount").Text ?? "", @"\d+");
            E<TextBlock>("StripCount").Text = m.Success ? m.Value : "";
            El("StripApplyDot").Visibility = El("ApplyBar").Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        }

        // широкая раскладка (сайты свёрнуты): кнопка включения слева, стратегия, Telegram и тест — справа от неё
        bool wideLayout;
        void SetWideLayout(bool wide, bool animate)
        {
            if (wide == wideLayout) return;
            wideLayout = wide;
            var left = E<Grid>("LeftCol");
            var center = E<StackPanel>("CenterStack");
            var side = E<StackPanel>("SideStack");
            var controls = E<StackPanel>("ControlsBlock");
            var test = E<StackPanel>("TestHost");
            var label = controls.Children[0] as FrameworkElement;
            Action move = () =>
            {
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
            };
            if (!animate || Anim.Eco) { move(); left.Opacity = 1; return; }
            // пиксельная смена раскладки: гаснет в 2 кадра, перестраивается, проявляется со сдвигом
            Px.Steps(left, UIElement.OpacityProperty, 0, 0, 0.5, 40, 0);
            After(85, () =>
            {
                move();
                Px.Steps(left, UIElement.OpacityProperty, 0, 0, 0.45, 45, 0.8, 90, 1);
                double dx = wide ? 14 : -14;
                Px.Steps(A("CenterStackT"), TranslateTransform.XProperty, 0, 0, dx, 45, dx / 2, 90, 0);
                Px.Steps(A("SideStackT"), TranslateTransform.XProperty, 0, 0, -dx, 45, -dx / 2, 90, 0);
            });
        }

        void SetSitesCollapsed(bool collapse, bool animate)
        {
            if (sitesAnimating || collapse == sitesCollapsed) return;
            sitesCollapsed = collapse;
            S.Set("sitesCollapsed", collapse); S.Save();

            var right = E<Grid>("RightCol");
            var area = E<Grid>("MainArea");
            var colL = (ColumnDefinition)Win.FindName("LeftColDef");
            var colR = (ColumnDefinition)Win.FindName("RightColDef");
            var applyRow = (RowDefinition)Win.FindName("ApplyRow");
            var card = El("SitesCard");
            var strip = El("SitesStrip");
            var ease = new QuinticEase { EasingMode = EasingMode.EaseInOut };
            bool anim = animate && !Anim.Eco && Win.IsVisible;
            UpdateStrip();

            if (collapse)
            {
                double w = right.ActualWidth;
                colL.Width = new GridLength(1, GridUnitType.Star);
                colR.Width = GridLength.Auto;
                applyRow.Height = new GridLength(0);
                if (!anim)
                {
                    right.BeginAnimation(FrameworkElement.WidthProperty, null); right.Width = StripWidth;
                    card.Visibility = Visibility.Collapsed;
                    strip.Visibility = Visibility.Visible; Anim.Stop(strip, Anim.OP); strip.Opacity = 1;
                    SetWideLayout(true, false);
                    return;
                }
                sitesAnimating = true;
                right.Width = w;
                // карточка гаснет и уезжает вправо, колонка сжимается до узкой вкладки
                Anim.D(card, Anim.OP, 0, 140);
                Anim.D(A("SitesCardT"), Anim.TX, 24, 200, 0);
                Anim.D(right, FrameworkElement.WidthProperty, StripWidth, 340, w, begin: 80, ease: ease);
                After(400, () =>
                {
                    card.Visibility = Visibility.Collapsed;
                    strip.Visibility = Visibility.Visible;
                    Px.Steps(strip, UIElement.OpacityProperty, 0, 0, 0.4, 50, 0.75, 100, 1);
                    Px.Steps(A("SitesStripT"), TranslateTransform.XProperty, 0, 0, 10, 50, 5, 100, 0);
                    SetWideLayout(true, true);
                    sitesAnimating = false;
                });
            }
            else
            {
                double target = Math.Max(StripWidth, area.ActualWidth - 330 - 22);
                Action finish = () =>
                {
                    right.BeginAnimation(FrameworkElement.WidthProperty, null);
                    right.Width = double.NaN;
                    colL.Width = new GridLength(330);
                    colR.Width = new GridLength(1, GridUnitType.Star);
                    applyRow.Height = GridLength.Auto;
                };
                if (!anim)
                {
                    SetWideLayout(false, false);
                    finish();
                    strip.Visibility = Visibility.Collapsed;
                    card.Visibility = Visibility.Visible; Anim.Stop(card, Anim.OP); card.Opacity = 1;
                    Anim.Stop(A("SitesCardT"), Anim.TX); ((TranslateTransform)A("SitesCardT")).X = 0;
                    return;
                }
                sitesAnimating = true;
                // кнопка и настройки собираются обратно в одну колонку, вкладка гаснет, колонка раскрывается, карточка приезжает справа
                SetWideLayout(false, true);
                Px.Steps(strip, UIElement.OpacityProperty, 0, 0, 0.5, 40, 0);
                After(140, () =>
                {
                    strip.Visibility = Visibility.Collapsed;
                    card.Visibility = Visibility.Visible; card.Opacity = 0;
                    Anim.D(right, FrameworkElement.WidthProperty, target, 340, StripWidth, ease: ease);
                    Anim.D(A("SitesCardT"), Anim.TX, 0, 360, 24, begin: 120, ease: Anim.Ease());
                    Anim.D(card, Anim.OP, 1, 260, 0, begin: 140);
                });
                After(500, () => { finish(); sitesAnimating = false; });
            }
        }
    }
}
