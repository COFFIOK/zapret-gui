// Zapret (C#) — мелкие улучшения «на ощупь»: поля ввода подсвечиваются рамкой, когда в них пишешь
using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ZapretCS
{
    partial class MainUi
    {
        void WirePolish()
        {
            Walk(Win);
        }

        // каждое поле ввода, лежащее в рамке-«коробке», при фокусе получает акцентную рамку
        void Walk(object node)
        {
            var tb = node as TextBox;
            if (tb != null) { HookFocus(tb); return; }
            var d = node as DependencyObject;
            if (d == null) return;
            foreach (var child in LogicalTreeHelper.GetChildren(d)) Walk(child);
        }

        void HookFocus(TextBox tb)
        {
            Border frame = null;
            DependencyObject p = LogicalTreeHelper.GetParent(tb);
            for (int i = 0; i < 3 && p != null; i++)
            {
                frame = p as Border;
                if (frame != null && frame.BorderThickness.Left >= 1) break;
                frame = null;
                p = LogicalTreeHelper.GetParent(p);
            }
            if (frame == null) return;
            Brush normal = null;
            tb.GotKeyboardFocus += (s, e) =>
            {
                if (normal == null) normal = frame.BorderBrush;
                frame.BorderBrush = (Brush)Win.FindResource("Accent");
            };
            tb.LostKeyboardFocus += (s, e) => { if (normal != null) frame.BorderBrush = normal; };
        }
    }
}
