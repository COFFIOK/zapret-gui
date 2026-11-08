// Zapret Stats — вкладка «Чаты»: переписка с пользователями Zapret (слева список, справа диалог)
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ZapretStats
{
    partial class MainWin
    {
        Dictionary<string, object> lastThreads, lastThread;
        string selId;
        bool showClosed, replying, threadLoading;
        readonly Grid chatView = new Grid();
        readonly StackPanel threadList = new StackPanel();
        readonly Grid convo = new Grid();
        readonly StackPanel convoHead = new StackPanel(), msgList = new StackPanel();
        readonly ScrollViewer msgScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        readonly TextBox replyBox = new TextBox();
        readonly TextBlock replyHint = new TextBlock(), convoEmpty = new TextBlock(), listHead = new TextBlock();
        readonly DispatcherTimer threadTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        long shownLastN;
        byte[] pendingImg;                                     // картинка, которую прикрепили к ответу
        readonly Image pendingThumb = new Image { MaxHeight = 70, MaxWidth = 120, Stretch = Stretch.Uniform };
        readonly DockPanel pendingRow = new DockPanel { Margin = new Thickness(22, 10, 22, 0), Visibility = Visibility.Collapsed };
        static readonly Dictionary<string, byte[]> imgCache = new Dictionary<string, byte[]>();

        void BuildChatView()
        {
            chatView.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
            chatView.ColumnDefinitions.Add(new ColumnDefinition());

            // ---- слева: список чатов ----
            var left = new DockPanel { Background = C.Card };
            var lh = new DockPanel { Margin = new Thickness(18, 16, 14, 10) };
            var toggle = (TextBlock)LinkBtn("", b => { showClosed = !showClosed; b.Text = showClosed ? "скрыть закрытые" : "показать закрытые"; Refresh(true); });
            toggle.Text = "показать закрытые"; toggle.Margin = new Thickness(0); toggle.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(toggle, Dock.Right);
            lh.Children.Add(toggle);
            listHead.FontFamily = C.Pixel; listHead.FontSize = 14; listHead.Foreground = C.Muted; listHead.Text = "ЧАТЫ";
            lh.Children.Add(listHead);
            DockPanel.SetDock(lh, Dock.Top);
            left.Children.Add(lh);
            left.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = threadList });
            var leftWrap = new Border { BorderBrush = C.Line, BorderThickness = new Thickness(0, 0, 2, 0), Child = left };
            chatView.Children.Add(leftWrap);

            // ---- справа: переписка ----
            convo.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            convo.RowDefinitions.Add(new RowDefinition());
            convo.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var head = new Border { BorderBrush = C.Line, BorderThickness = new Thickness(0, 0, 0, 2), Padding = new Thickness(22, 14, 22, 14), Child = convoHead };
            convo.Children.Add(head);
            msgScroll.Content = msgList;
            msgScroll.Padding = new Thickness(22, 14, 16, 14);
            Grid.SetRow(msgScroll, 1); convo.Children.Add(msgScroll);

            var bottom = new StackPanel();
            var rm = (TextBlock)LinkBtn("убрать", b => SetPending(null));
            rm.VerticalAlignment = VerticalAlignment.Center; rm.Margin = new Thickness(12, 0, 0, 0);
            pendingRow.Children.Add(new Border { BorderBrush = C.Line, BorderThickness = new Thickness(2), Child = pendingThumb });
            pendingRow.Children.Add(rm);
            pendingRow.Children.Add(new TextBlock());
            bottom.Children.Add(pendingRow);
            var composer = new DockPanel { Margin = new Thickness(22, 12, 22, 16) };
            var send = PxButton("ОТВЕТИТЬ", true, SendReply);
            ((FrameworkElement)send).VerticalAlignment = VerticalAlignment.Bottom;
            DockPanel.SetDock(send, Dock.Right);
            composer.Children.Add(send);
            var attach = PxButton("ФОТО", false, PickImage);
            ((FrameworkElement)attach).VerticalAlignment = VerticalAlignment.Bottom;
            DockPanel.SetDock(attach, Dock.Right);
            composer.Children.Add(attach);
            var boxWrap = new Grid { Margin = new Thickness(0, 0, 4, 0) };
            replyHint.Text = "Ответ… Enter — отправить, Shift+Enter — новая строка"; replyHint.Foreground = C.Muted; replyHint.Opacity = 0.7;
            replyHint.Margin = new Thickness(12, 11, 12, 0); replyHint.IsHitTestVisible = false;
            replyBox.Background = C.Card; replyBox.Foreground = C.Text; replyBox.BorderBrush = C.Line; replyBox.BorderThickness = new Thickness(2);
            replyBox.CaretBrush = C.Accent; replyBox.Padding = new Thickness(8, 9, 8, 9); replyBox.FontSize = 14; replyBox.TextWrapping = TextWrapping.Wrap;
            replyBox.MinHeight = 44; replyBox.MaxHeight = 160; replyBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; replyBox.MaxLength = 4000;
            replyBox.TextChanged += (s, e) => replyHint.Visibility = replyBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            replyBox.PreviewKeyDown += (s, e) =>
            {
                // Ctrl+V с картинкой в буфере — прикрепить её
                if (e.Key == System.Windows.Input.Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && Clipboard.ContainsImage())
                {
                    try { SetPending(PrepImage(Clipboard.GetImage())); } catch (Exception ex) { status.Text = "не удалось взять картинку: " + ex.Message; }
                    e.Handled = true; return;
                }
                if (e.Key != System.Windows.Input.Key.Enter) return;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                {
                    int at = replyBox.CaretIndex;
                    replyBox.Text = replyBox.Text.Insert(at, Environment.NewLine);
                    replyBox.CaretIndex = at + Environment.NewLine.Length;
                }
                else SendReply();
                e.Handled = true;
            };
            boxWrap.Children.Add(replyBox);
            boxWrap.Children.Add(replyHint);
            composer.Children.Add(boxWrap);
            bottom.Children.Add(composer);
            Grid.SetRow(bottom, 2); convo.Children.Add(bottom);

            convoEmpty.Text = "Выбери чат слева"; convoEmpty.Foreground = C.Muted; convoEmpty.FontSize = 15;
            convoEmpty.HorizontalAlignment = HorizontalAlignment.Center; convoEmpty.VerticalAlignment = VerticalAlignment.Center;
            var right = new Grid();
            right.Children.Add(convo);
            right.Children.Add(convoEmpty);
            convo.Visibility = Visibility.Collapsed;
            Grid.SetColumn(right, 1);
            chatView.Children.Add(right);

            threadTimer.Tick += (s, e) => { if (tab == "fb" && selId != null && IsActive) LoadThread(selId, false); };
        }

        // ---- список чатов ----
        void RenderThreads()
        {
            threadList.Children.Clear();
            if (lastThreads == null) { threadList.Children.Add(Pad(new TextBlock { Text = "загрузка…", Foreground = C.Muted })); return; }
            var items = J.List(J.Get(lastThreads, "threads")).ToList();
            long unread = J.N(J.Get(lastThreads, "unread")), total = J.N(J.Get(lastThreads, "total"));
            listHead.Text = "ЧАТЫ · " + total + (unread > 0 ? " · НОВЫХ " + unread : "");
            if (items.Count == 0)
            {
                threadList.Children.Add(Pad(new TextBlock
                {
                    Text = total == 0 ? "пока никто не написал" : "открытых чатов нет — нажми «показать закрытые»",
                    Foreground = C.Muted, TextWrapping = TextWrapping.Wrap
                }));
                return;
            }
            foreach (var t in items) threadList.Children.Add(ThreadItem(t));
        }

        static UIElement Pad(UIElement e) { return new Border { Padding = new Thickness(18, 4, 18, 4), Child = e }; }

        UIElement ThreadItem(Dictionary<string, object> t)
        {
            string id = J.S(J.Get(t, "id"));
            long unread = J.N(J.Get(t, "dev_unread"));
            bool done = J.S(J.Get(t, "status")) == "done", sel = id == selId;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = ThreadName(t), FontWeight = unread > 0 ? FontWeights.Bold : FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = done ? C.Muted : C.Text });
            string preview = (J.N(J.Get(t, "last_dev")) == 1 ? "Вы: " : "") + J.S(J.Get(t, "last_text")).Replace("\n", " ");
            sp.Children.Add(new TextBlock { Text = preview, Foreground = unread > 0 ? C.Text : C.Muted, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
            sp.Children.Add(new TextBlock { Text = "v" + J.S(J.Get(t, "v")) + " · " + WinName(J.S(J.Get(t, "os"))) + (done ? " · закрыт" : "") + (J.Get(t, "banned") != null ? " · заблокирован" : ""), Foreground = C.Muted, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) });
            g.Children.Add(sp);
            var right = new StackPanel { Margin = new Thickness(10, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(new TextBlock { Text = Short(J.S(J.Get(t, "last"))), Foreground = C.Muted, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right });
            if (unread > 0)
                right.Children.Add(new Border { Background = C.Accent, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Right,
                    Child = new TextBlock { Text = unread.ToString(), Foreground = C.Bg, FontFamily = C.Pixel, FontSize = 12 } });
            Grid.SetColumn(right, 1); g.Children.Add(right);
            var item = new Border
            {
                Padding = new Thickness(18, 10, 14, 10), Child = g, Cursor = Cursors.Hand,
                Background = sel ? C.Hover : Brushes.Transparent, BorderBrush = sel ? C.Accent : Brushes.Transparent, BorderThickness = new Thickness(4, 0, 0, 0)
            };
            item.MouseEnter += (s, e) => { if (id != selId) item.Background = C.Hover; };
            item.MouseLeave += (s, e) => { if (id != selId) item.Background = Brushes.Transparent; };
            item.MouseLeftButtonUp += (s, e) => { selId = id; shownLastN = -1; RenderThreads(); LoadThread(id, true); threadTimer.Start(); };
            return item;
        }

        static string ThreadName(Dictionary<string, object> t)
        {
            string n = J.S(J.Get(t, "nick")), c = J.S(J.Get(t, "contact")), id = J.S(J.Get(t, "id"));
            if (n.Length > 0) return n;
            return c.Length > 0 ? c : "Пользователь #" + (id.Length >= 4 ? id.Substring(0, 4).ToUpperInvariant() : id);
        }

        static string Short(string iso)
        {
            DateTime t;
            if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) return "";
            t = t.ToLocalTime();
            if (t.Date == DateTime.Today) return t.ToString("HH:mm");
            if (t.Date == DateTime.Today.AddDays(-1)) return "вчера";
            return t.ToString("d MMM", CultureInfo.GetCultureInfo("ru-RU"));
        }

        // ---- переписка ----
        void LoadThread(string id, bool scrollDown)
        {
            if (threadLoading || string.IsNullOrEmpty(key)) return;
            threadLoading = true;
            string k = key;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Dictionary<string, object> d = null;
                try { d = Api.Thread(k, id); } catch { }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    threadLoading = false;
                    if (d == null || id != selId) return;
                    RenderThread(d);
                    // открытый чат считается прочитанным — обновим счётчик слева
                    var t = lastThreads == null ? null : J.List(J.Get(lastThreads, "threads")).FirstOrDefault(x => J.S(J.Get(x, "id")) == id);
                    if (t != null && J.N(J.Get(t, "dev_unread")) > 0)
                    {
                        lastThreads["unread"] = Math.Max(0, J.N(J.Get(lastThreads, "unread")) - J.N(J.Get(t, "dev_unread")));
                        t["dev_unread"] = 0;
                        lastFresh = J.N(J.Get(lastThreads, "unread"));
                        RenderThreads(); UpdateTabs();
                    }
                }));
            });
        }

        void RenderThread(Dictionary<string, object> d)
        {
            lastThread = d;
            var t = J.Get(d, "thread") as Dictionary<string, object>;
            var msgs = J.List(J.Get(d, "msgs")).ToList();
            if (t == null) return;
            convo.Visibility = Visibility.Visible; convoEmpty.Visibility = Visibility.Collapsed;
            string id = J.S(J.Get(t, "id"));
            bool done = J.S(J.Get(t, "status")) == "done";

            // шапка
            convoHead.Children.Clear();
            var top = new DockPanel();
            var btns = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(btns, Dock.Right);
            var ban = J.Get(t, "banned") as Dictionary<string, object>;
            Button banBtn = null;
            banBtn = PxButton(ban != null ? "РАЗБЛОКИРОВАТЬ" : "БЛОКИРОВКА", false, () =>
            {
                if (ban != null) { BanUser(id, 0); return; }
                var menu = new ContextMenu { PlacementTarget = banBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
                foreach (var o in new[] { new { T = "на 1 час", H = 1.0 }, new { T = "на 1 день", H = 24.0 }, new { T = "на 7 дней", H = 168.0 }, new { T = "на 30 дней", H = 720.0 }, new { T = "навсегда", H = -1.0 } })
                {
                    var h = o.H;
                    var mi = new MenuItem { Header = "Заблокировать " + o.T };
                    mi.Click += (s2, e2) => BanUser(id, h);
                    menu.Items.Add(mi);
                }
                menu.IsOpen = true;
            });
            btns.Children.Add(banBtn);
            btns.Children.Add(PxButton(done ? "ОТКРЫТЬ СНОВА" : "ЗАКРЫТЬ ЧАТ", false, () => SetThreadStatus(id, done ? "open" : "done")));
            btns.Children.Add(PxButton("УДАЛИТЬ", false, () =>
            {
                if (MessageBox.Show(this, "Удалить весь чат насовсем? Пользователь тоже перестанет его видеть.", "Zapret Stats", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    SetThreadStatus(id, "delete");
            }));
            top.Children.Add(btns);
            var name = new TextBlock { Text = ThreadName(t), FontFamily = C.Pixel, FontSize = 18, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            top.Children.Add(name);
            convoHead.Children.Add(top);
            convoHead.Children.Add(new TextBlock
            {
                Text = "Zapret GUI " + J.S(J.Get(t, "v")) + " · zapret " + Or(J.S(J.Get(t, "z")), "?") + " · " + WinName(J.S(J.Get(t, "os"))) +
                       " · " + (J.S(J.Get(t, "lang")) == "en" ? "английский" : "русский") + " · стратегия " + Or(J.S(J.Get(t, "strat")), "—") +
                       " · zapret " + (J.N(J.Get(t, "onz")) == 1 ? "включён" : "выключен") + " · пишет с " + Short(J.S(J.Get(t, "first"))),
                Foreground = C.Muted, FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap
            });
            if (done) convoHead.Children.Add(new TextBlock { Text = "чат закрыт — если человек напишет снова, он откроется сам", Foreground = C.Muted, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
            if (ban != null)
            {
                string until = J.S(J.Get(ban, "until"));
                convoHead.Children.Add(new TextBlock { Text = "заблокирован " + (until == "forever" ? "навсегда" : "до " + FullTime(until)) + " — не может писать в чат", Foreground = C.Red, FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0) });
            }

            // сообщения: перерисовываем, только если появились новые
            long lastN = msgs.Count > 0 ? J.N(J.Get(msgs[msgs.Count - 1], "n")) : 0;
            if (lastN == shownLastN && msgList.Children.Count > 0) return;
            bool atBottom = msgScroll.VerticalOffset >= msgScroll.ScrollableHeight - 30 || shownLastN < 0;
            shownLastN = lastN;
            msgList.Children.Clear();
            string lastDay = null;
            foreach (var m in msgs)
            {
                string day = Day(J.S(J.Get(m, "at")));
                if (day != lastDay)
                {
                    lastDay = day;
                    msgList.Children.Add(new TextBlock { Text = day, Foreground = C.Muted, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 10) });
                }
                msgList.Children.Add(Bubble(m));
            }
            if (atBottom) msgScroll.Dispatcher.BeginInvoke(new Action(msgScroll.ScrollToEnd), DispatcherPriority.Loaded);
        }

        UIElement Bubble(Dictionary<string, object> m)
        {
            bool dev = J.Get(m, "dev") is bool && (bool)J.Get(m, "dev");
            string text = J.S(J.Get(m, "text")), kind = J.S(J.Get(m, "kind")), log = J.S(J.Get(m, "log"));
            var sp = new StackPanel();
            if (!dev && kind.Length > 0)
            {
                Brush kc = kind == "bug" ? KindBug : kind == "idea" ? KindIdea : KindOther;
                var tag = Tag(kind == "bug" ? "ОШИБКА" : kind == "idea" ? "ИДЕЯ" : "ВОПРОС", kc, false);
                ((FrameworkElement)tag).HorizontalAlignment = HorizontalAlignment.Left;
                ((FrameworkElement)tag).Margin = new Thickness(0, 0, 0, 6);
                sp.Children.Add(tag);
            }
            string img = J.S(J.Get(m, "img"));
            if (img.Length > 0) sp.Children.Add(ImageView(img));
            if (text.Length > 0) sp.Children.Add(new TextBox
            {
                Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = C.Text, FontSize = 14, Padding = new Thickness(0), Margin = new Thickness(-2, img.Length > 0 ? 8 : 0, 0, 0)
            });
            var foot = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var time = new TextBlock { Text = TimeOnly(J.S(J.Get(m, "at"))), Foreground = C.Muted, FontSize = 11 };
            DockPanel.SetDock(time, Dock.Right);
            foot.Children.Add(time);
            var links = new StackPanel { Orientation = Orientation.Horizontal };
            var logBox = new TextBox
            {
                Text = log, IsReadOnly = true, FontFamily = C.Mono, FontSize = 11.5, Foreground = C.Muted, Background = C.Bg, BorderBrush = C.Line,
                BorderThickness = new Thickness(1), TextWrapping = TextWrapping.Wrap, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(6), Visibility = Visibility.Collapsed
            };
            if (log.Length > 0) links.Children.Add(LinkBtn("журнал ▾", b => { bool show = logBox.Visibility != Visibility.Visible; logBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed; b.Text = show ? "журнал ▴" : "журнал ▾"; }));
            links.Children.Add(LinkBtn("копировать", b => { try { Clipboard.SetText(text + (log.Length > 0 ? Environment.NewLine + Environment.NewLine + log : "")); b.Text = "скопировано"; } catch { } }));
            foot.Children.Add(links);
            sp.Children.Add(foot);
            sp.Children.Add(logBox);
            return new Border
            {
                Child = sp, MaxWidth = 560, Padding = new Thickness(14, 10, 14, 8), Margin = new Thickness(dev ? 80 : 0, 0, dev ? 0 : 80, 10),
                HorizontalAlignment = dev ? HorizontalAlignment.Right : HorizontalAlignment.Left, BorderThickness = new Thickness(2),
                Background = dev ? MyBubble : C.Card, BorderBrush = dev ? C.AccentDim : C.Line
            };
        }
        static readonly Brush MyBubble = MakeBrush("#183026");

        static string Day(string iso)
        {
            DateTime t;
            if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) return "";
            t = t.ToLocalTime();
            if (t.Date == DateTime.Today) return "сегодня";
            if (t.Date == DateTime.Today.AddDays(-1)) return "вчера";
            return t.ToString(t.Year == DateTime.Today.Year ? "d MMMM" : "d MMMM yyyy", CultureInfo.GetCultureInfo("ru-RU"));
        }
        static string TimeOnly(string iso)
        {
            DateTime t;
            return DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t) ? t.ToLocalTime().ToString("HH:mm") : "";
        }

        void SendReply()
        {
            string text = replyBox.Text.Trim();
            var img = pendingImg;
            if (replying || (text.Length == 0 && img == null) || selId == null || string.IsNullOrEmpty(key)) return;
            replying = true;
            string k = key, id = selId;
            status.Text = img != null ? "загружаю картинку…" : "отправляю…";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string err = null;
                try
                {
                    string ik = img != null ? Api.UploadImg(k, id, img) : null;
                    if (img != null && ik == null) throw new Exception("сервер не принял картинку");
                    Api.Reply(k, id, text, ik);
                }
                catch (Exception ex) { err = ex.Message; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    replying = false;
                    if (err != null) { status.Text = "не удалось отправить: " + err; return; }
                    status.Text = "ответ отправлен " + DateTime.Now.ToString("HH:mm:ss");
                    replyBox.Text = "";
                    SetPending(null);
                    shownLastN = -1;
                    LoadThread(id, true);
                    Refresh(false);
                }));
            });
        }

        void SetThreadStatus(string id, string st)
        {
            string k = key;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string err = null;
                try { Api.SetThread(k, id, st); } catch (Exception ex) { err = ex.Message; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (err != null) { status.Text = "не удалось: " + err; return; }
                    if (st == "delete") { selId = null; convo.Visibility = Visibility.Collapsed; convoEmpty.Visibility = Visibility.Visible; threadTimer.Stop(); }
                    else { shownLastN = -1; LoadThread(id, false); }
                    Refresh(true);
                }));
            });
        }

        // ---- картинки ----
        void PickImage()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Картинки|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Все файлы|*.*", Title = "Выбери картинку" };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var bi = new BitmapImage();
                using (var fs = File.OpenRead(dlg.FileName)) { bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = fs; bi.EndInit(); }
                SetPending(PrepImage(bi));
            }
            catch (Exception ex) { status.Text = "не удалось открыть картинку: " + ex.Message; }
        }

        void SetPending(byte[] jpg)
        {
            pendingImg = jpg;
            pendingRow.Visibility = jpg != null ? Visibility.Visible : Visibility.Collapsed;
            pendingThumb.Source = jpg != null ? Decode(jpg, 240) : null;
            if (jpg != null) replyBox.Focus();
        }

        // уменьшаем до 1600 px по большей стороне и сжимаем в JPEG (не больше ~1.4 МБ)
        public static byte[] PrepImage(BitmapSource src)
        {
            foreach (var t in new[] { new { Side = 1600, Q = 85 }, new { Side = 1280, Q = 75 }, new { Side = 1024, Q = 65 } })
            {
                BitmapSource b = src;
                double sc = Math.Min(1.0, t.Side / (double)Math.Max(src.PixelWidth, src.PixelHeight));
                if (sc < 1) b = new TransformedBitmap(src, new ScaleTransform(sc, sc));
                if (b.Format != PixelFormats.Bgr24 && b.Format != PixelFormats.Bgr32) b = new FormatConvertedBitmap(b, PixelFormats.Bgr24, null, 0);
                var enc = new JpegBitmapEncoder { QualityLevel = t.Q };
                enc.Frames.Add(BitmapFrame.Create(b));
                using (var ms = new MemoryStream()) { enc.Save(ms); if (ms.Length <= 1400 * 1024) return ms.ToArray(); }
            }
            throw new Exception("картинка слишком большая");
        }

        static BitmapImage Decode(byte[] data, int width)
        {
            var bi = new BitmapImage();
            bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = new MemoryStream(data);
            if (width > 0) bi.DecodePixelWidth = width;
            bi.EndInit(); bi.Freeze();
            return bi;
        }

        UIElement ImageView(string img)
        {
            var im = new Image { MaxWidth = 360, MaxHeight = 280, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand, ToolTip = "открыть" };
            var box = new Border { BorderBrush = C.Line, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 120, MinHeight = 60, Child = im };
            var wait = new TextBlock { Text = "картинка…", Foreground = C.Muted, FontSize = 12, Margin = new Thickness(10) };
            box.Child = wait;
            Action<byte[]> show = data =>
            {
                try { im.Source = Decode(data, 720); box.Child = im; box.MinHeight = 0; box.MinWidth = 0; }
                catch { wait.Text = "не удалось показать картинку"; }
            };
            im.MouseLeftButtonUp += (s, e) => OpenImage(img);
            byte[] cached;
            if (imgCache.TryGetValue(img, out cached)) show(cached);
            else
            {
                string k = key;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    byte[] data = null;
                    try { data = Api.GetImg(k, img); } catch { }
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (data == null) { wait.Text = "картинка удалена или недоступна"; return; }
                        imgCache[img] = data; show(data);
                    }));
                });
            }
            return box;
        }

        static void OpenImage(string img)
        {
            byte[] data;
            if (!imgCache.TryGetValue(img, out data)) return;
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "ZapretStats");
                Directory.CreateDirectory(dir);
                string f = Path.Combine(dir, img + (data.Length > 1 && data[0] == 0x89 ? ".png" : ".jpg"));
                if (!File.Exists(f)) File.WriteAllBytes(f, data);
                System.Diagnostics.Process.Start(f);
            }
            catch { }
        }

        void BanUser(string id, double hours)
        {
            string k = key;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string err = null;
                try { Api.Ban(k, id, hours); } catch (Exception ex) { err = ex.Message; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    status.Text = err != null ? "не удалось: " + err : hours == 0 ? "блокировка снята" : "пользователь заблокирован";
                    shownLastN = -1; LoadThread(id, false); Refresh(true);
                }));
            });
        }

        static string FullTime(string iso)
        {
            DateTime t;
            if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) return iso;
            return t.ToLocalTime().ToString("d MMMM, HH:mm", CultureInfo.GetCultureInfo("ru-RU"));
        }

        // снимок всего окна (для проверки вида)
        void ShotWindow()
        {
            var root = (FrameworkElement)Content;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(C.Bg, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            }
            rtb.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var f = System.IO.File.Create(ShotFile)) enc.Save(f);
            Close();
        }
    }
}
