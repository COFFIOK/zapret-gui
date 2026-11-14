// Zapret (C#) — общие помощники: журнал, настройки, цвета темы, анимации
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ZapretCS
{
    static class Log
    {
        public static string FilePath;
        public static void Write(string msg)
        {
            try { File.AppendAllText(FilePath, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + msg + Environment.NewLine, Encoding.UTF8); }
            catch { }
        }

        // ошибка, которую можно пережить, но знать о ней нужно (видна в gui-error.log и в «журнале» из чата).
        // Одна и та же ошибка из одного места пишется не чаще раза в 10 минут — чтобы фоновые опросы не забили журнал.
        static readonly Dictionary<string, DateTime> lastErr = new Dictionary<string, DateTime>();
        public static void Err(string where, Exception ex)
        {
            if (ex == null) return;
            string msg = where + ": " + ex.GetType().Name + ": " + ex.Message;
            lock (lastErr)
            {
                DateTime t;
                if (lastErr.TryGetValue(msg, out t) && DateTime.UtcNow - t < TimeSpan.FromMinutes(10)) return;
                if (lastErr.Count > 200) lastErr.Clear();
                lastErr[msg] = DateTime.UtcNow;
            }
            Write(msg);
        }
    }

    // Настройки хранятся в том же gui-settings.json, что и у старого приложения
    class Settings
    {
        readonly string path;
        Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public Settings(string path)
        {
            this.path = path;
            try
            {
                if (File.Exists(path))
                {
                    var o = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
                    if (o != null) foreach (var kv in o) d[kv.Key] = kv.Value;
                }
            }
            catch (Exception ex) { Log.Write("settings: " + ex.Message); }
        }

        public string Str(string key, string def = null)
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null) { var s = Convert.ToString(v, CultureInfo.InvariantCulture); if (s.Length > 0) return s; }
            return def;
        }
        public bool Bool(string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return false;
            if (v is bool) return (bool)v;
            bool b; return bool.TryParse(Convert.ToString(v), out b) && b;
        }
        public void Set(string key, object value) { d[key] = value; }

        public void Save()
        {
            try
            {
                // как ConvertTo-Json в PowerShell: каждое значение с новой строки
                var js = new JavaScriptSerializer();
                var sb = new StringBuilder("{\r\n");
                int i = 0;
                foreach (var kv in d)
                {
                    sb.Append("    ").Append(js.Serialize(kv.Key)).Append(":  ").Append(js.Serialize(kv.Value));
                    sb.Append(++i < d.Count ? ",\r\n" : "\r\n");
                }
                sb.Append("}");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Write("save settings: " + ex.Message); }
        }
    }

    // Тёмная тема — исходные цвета разметки; для светлой и для акцента — замены
    static class Theme
    {
        public static bool Light;
        public static string Accent = "#3DDC97";
        static readonly Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static readonly string[,] LightPairs = {
            {"#121419","#F4F6F9"},{"#252933","#DDE1E8"},{"#1A1D24","#FFFFFF"},{"#22262F","#EEF1F5"},{"#2E3340","#D5DAE2"},
            {"#2A2E38","#E3E7EE"},{"#262A33","#E6EAF0"},{"#232731","#E1E5EC"},{"#171A21","#FFFFFF"},{"#171A20","#F0F2F6"},
            {"#1E2129","#F3F5F8"},{"#20232B","#F1F3F7"},{"#232833","#E8ECF2"},{"#3A3F4D","#C7CDD8"},
            {"#E6E8EE","#1C2028"},{"#8A90A0","#6B7280"},{"#5A6070","#9AA1AE"},{"#9AA3B5","#5B6372"},{"#B8BDC9","#4A5160"},{"#C9CED8","#4A5160"},
            {"#C0090A0D","#66101418"},{"#B0090A0D","#55101418"},
            {"#3A2328","#FDE7EA"},{"#FF8C99","#D9364B"},{"#C9606F","#C93A50"},
            {"#2B5664","#9BDDEF"},{"#CFEFF8","#2B5664"},{"#15171D","#ECEFF3"},{"#08090B","#C9CED8"},
            {"#13151B","#F7F8FA"},{"#22252D","#E3E7EE"},{"#3A3F4B","#C7CDD8"},{"#2A1A1D","#FDE7EA"},{"#5A2A30","#F2B8C0"},{"#FF8A8E","#D9364B"}
        };

        public static void Init(string theme, string accent)
        {
            Light = theme == "light";
            if (!string.IsNullOrEmpty(accent)) Accent = accent.ToUpperInvariant();
            map.Clear();
            if (Light) for (int i = 0; i < LightPairs.GetLength(0); i++) map[LightPairs[i, 0]] = LightPairs[i, 1];
            string a = Accent;
            string[] tints = { "#1B3329", "#18302A", "#1E2B26", "#1B2B25", "#1A2621" };
            if (Light)
            {
                foreach (var k in tints) map[k] = Mix(a, "#FFFFFF", 0.14);
                map["#2D5A47"] = Mix(a, "#FFFFFF", 0.45);
                map["#5A9C80"] = Mix(a, "#000000", 0.65);
                map["#7FE8B8"] = Mix(a, "#000000", 0.65);
                map["#BFEBD6"] = Mix(a, "#000000", 0.55);
            }
            else
            {
                foreach (var k in tints) map[k] = Mix(a, "#171A21", 0.16);
                map["#2D5A47"] = Mix(a, "#171A21", 0.38);
                map["#5A9C80"] = Mix(a, "#171A21", 0.6);
                map["#7FE8B8"] = Mix(a, "#FFFFFF", 0.6);
                map["#BFEBD6"] = Mix(a, "#FFFFFF", 0.3);
            }
            map["#2BB57A"] = Mix(a, "#000000", 0.8);
            map["#1B5E42"] = Mix(a, "#000000", 0.45);   // жёсткая тень акцентной кнопки
            map["#3DDC97"] = a;
        }

        static int[] Rgb(string h)
        {
            h = h.TrimStart('#'); if (h.Length == 8) h = h.Substring(2);
            return new[] { Convert.ToInt32(h.Substring(0, 2), 16), Convert.ToInt32(h.Substring(2, 2), 16), Convert.ToInt32(h.Substring(4, 2), 16) };
        }
        public static string Mix(string a, string b, double wa)
        {
            var x = Rgb(a); var y = Rgb(b); var sb = new StringBuilder("#");
            for (int i = 0; i < 3; i++) sb.Append(((int)Math.Round(x[i] * wa + y[i] * (1 - wa))).ToString("X2"));
            return sb.ToString();
        }
        public static string Map(string hex) { string v; return map.TryGetValue(hex, out v) ? v : hex; }
        public static Color C(string hex) { return (Color)ColorConverter.ConvertFromString(Map(hex)); }
        public static SolidColorBrush B(string hex) { return new SolidColorBrush(C(hex)); }
        public static string Apply(string xaml)
        {
            return Regex.Replace(xaml, "#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?\\b", m => Map(m.Value));
        }
    }

    // Короткие помощники для анимаций (как Anim-Double / Anim-Color в старой версии)
    static class Anim
    {
        public static readonly DependencyProperty OP = UIElement.OpacityProperty;
        public static readonly DependencyProperty SX = ScaleTransform.ScaleXProperty;
        public static readonly DependencyProperty SY = ScaleTransform.ScaleYProperty;
        public static readonly DependencyProperty Ang = RotateTransform.AngleProperty;
        public static readonly DependencyProperty TX = TranslateTransform.XProperty;
        public static readonly DependencyProperty TY = TranslateTransform.YProperty;

        public static IEasingFunction Ease() { return new CubicEase { EasingMode = EasingMode.EaseOut }; }
        public static IEasingFunction Back() { return new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 }; }
        public static IEasingFunction Sine() { return new SineEase { EasingMode = EasingMode.EaseInOut }; }

        // режим экономии: без анимаций — сразу конечное состояние, бесконечные анимации не запускаются
        public static bool Eco;

        public static void D(IAnimatable t, DependencyProperty p, double to, int ms = 250, double? from = null,
            bool forever = false, int begin = 0, bool noEase = false, bool autoReverse = false, IEasingFunction ease = null, bool low = false, bool px = false)
        {
            if (t == null) return;
            if (Eco)
            {
                t.BeginAnimation(p, null);
                var dobj = t as DependencyObject;
                if (!forever && dobj != null && !(dobj is Freezable && ((Freezable)dobj).IsFrozen)) dobj.SetValue(p, to);
                return;
            }
            var a = new DoubleAnimation { To = to, Duration = new Duration(TimeSpan.FromMilliseconds(ms)) };
            if (from.HasValue) a.From = from.Value;
            if (begin != 0) a.BeginTime = TimeSpan.FromMilliseconds(begin);
            if (forever) a.RepeatBehavior = RepeatBehavior.Forever;
            if (low) Timeline.SetDesiredFrameRate(a, 30);
            if (autoReverse) a.AutoReverse = true;
            // окна и панели двигаются плавно; px — покадрово, в пиксельном стиле (кнопка включения и т.п.)
            var e = ease ?? (noEase ? null : Ease());
            if (px) e = new PxSteps(e, Px.Frames(ms));
            if (e != null) a.EasingFunction = e;
            t.BeginAnimation(p, a);
        }
        public static void Color(Brush brush, string hex, int ms = 350, bool px = false)
        {
            var b = brush as SolidColorBrush;
            if (b == null || b.IsFrozen) return;
            if (Eco) { b.BeginAnimation(SolidColorBrush.ColorProperty, null); b.Color = Theme.C(hex); return; }
            var a = new ColorAnimation { To = Theme.C(hex), Duration = new Duration(TimeSpan.FromMilliseconds(ms)), EasingFunction = px ? (IEasingFunction)new PxSteps(null, Math.Min(4, Px.Frames(ms))) : Ease() };
            b.BeginAnimation(SolidColorBrush.ColorProperty, a);
        }
        public static void Stop(IAnimatable t, DependencyProperty p) { if (t != null) t.BeginAnimation(p, null); }
    }
}
