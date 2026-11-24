using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TchatOverlay
{
    public class OverlayWindow : Window
    {
        private class Entry
        {
            public ChatMessage Msg;
            public FrameworkElement Element;
            public DateTime Created;
            public bool Fading;
            public bool Started;   // mode horizontal : déjà entré dans le bandeau
            public double X;
        }

        private const int HK_EDIT = 1, HK_TOGGLE = 2, HK_REPLY = 3, HK_INTERACT = 4;

        private readonly Config _cfg;
        private readonly Grid _root;
        private readonly StackPanel _panel;
        private readonly Canvas _ticker;
        private bool _tickerHooked;
        private bool? _lastHorizontal;
        private TimeSpan _lastRender = TimeSpan.Zero;
        private readonly Border _editFrame;
        private readonly TextBlock _editLabel;
        private readonly TextBlock _statusLabel;
        private readonly List<Entry> _entries = new List<Entry>();
        private readonly DispatcherTimer _tick;
        private readonly DispatcherTimer _topmost;
        private IntPtr _hwnd;
        private bool _editMode;

        public event Action EditModeChanged;

        // Édition Pro
        public event Action ReplyHotkeyPressed;
        public event Action<ChatMessage> ReplyRequested;
        public event Action InteractiveModeChanged;
        public event Action TopmostReasserted;
        public Func<ChatMessage, ContextMenu> MessageMenuProvider;
        private bool _interactive;
        private Border _interactFrame;
        public bool IsInteractive => _interactive;
        public event Action<string> HotkeyError;
        public bool IsEditMode => _editMode;

        private static readonly Color[] DefaultPalette =
        {
            Color.FromRgb(0xFF,0x00,0x00), Color.FromRgb(0x00,0x00,0xFF), Color.FromRgb(0x00,0x80,0x00),
            Color.FromRgb(0xB2,0x22,0x22), Color.FromRgb(0xFF,0x7F,0x50), Color.FromRgb(0x9A,0xCD,0x32),
            Color.FromRgb(0xFF,0x45,0x00), Color.FromRgb(0x2E,0x8B,0x57), Color.FromRgb(0xDA,0xA5,0x20),
            Color.FromRgb(0xD2,0x69,0x1E), Color.FromRgb(0x5F,0x9E,0xA0), Color.FromRgb(0x1E,0x90,0xFF),
            Color.FromRgb(0xFF,0x69,0xB4), Color.FromRgb(0x8A,0x2B,0xE2), Color.FromRgb(0x00,0xFF,0x7F)
        };

        public OverlayWindow(Config cfg)
        {
            _cfg = cfg;
            Title = "Tchat Overlay";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;

            _root = new Grid { ClipToBounds = true };
            _panel = new StackPanel { Margin = new Thickness(4) };
            _root.Children.Add(_panel);
            _ticker = new Canvas { ClipToBounds = true, Visibility = Visibility.Collapsed };
            _root.Children.Add(_ticker);

            // Cadre visible uniquement en mode édition
            var editGrid = new Grid();
            _editLabel = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(8),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Top
            };
            _statusLabel = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                FontSize = 11,
                Margin = new Thickness(8, 8, 28, 8),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            var grip = new Thumb
            {
                Width = 20,
                Height = 20,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Cursor = Cursors.SizeNWSE,
                Background = Brushes.White,
                ToolTip = "Redimensionner"
            };
            grip.DragDelta += (s, e) =>
            {
                Width = Math.Max(150, Width + e.HorizontalChange);
                Height = Math.Max(80, Height + e.VerticalChange);
            };
            editGrid.Children.Add(_editLabel);
            editGrid.Children.Add(_statusLabel);
            editGrid.Children.Add(grip);

            _editFrame = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x91, 0x46, 0xFF)),
                BorderThickness = new Thickness(2),
                Background = new SolidColorBrush(Color.FromArgb(0x55, 0x20, 0x10, 0x40)),
                Visibility = Visibility.Collapsed,
                Child = editGrid
            };
            _root.Children.Add(_editFrame);

            _interactFrame = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xC8, 0xFF)),
                BorderThickness = new Thickness(2),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = "MODE INTERACTIF — clic = répondre, clic droit = actions",
                    Foreground = Brushes.White,
                    Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x00, 0x60, 0x80)),
                    FontSize = 11,
                    Padding = new Thickness(6, 2, 6, 2),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top
                }
            };
            _root.Children.Add(_interactFrame);
            Content = _root;

            MouseLeftButtonDown += (s, e) =>
            {
                if (_editMode && e.ButtonState == MouseButtonState.Pressed)
                {
                    try { DragMove(); } catch { }
                }
            };

            _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _tick.Tick += (s, e) => ExpireMessages();
            _tick.Start();

            _topmost = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _topmost.Tick += (s, e) => ForceTopmost();
            _topmost.Start();

            ApplyConfig(false);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            int ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_LAYERED;
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex);
            SetClickThrough(true);
            HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
            RegisterHotkeys();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_tickerHooked) CompositionTarget.Rendering -= OnRendering;
            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.UnregisterHotKey(_hwnd, HK_EDIT);
                NativeMethods.UnregisterHotKey(_hwnd, HK_TOGGLE);
                NativeMethods.UnregisterHotKey(_hwnd, HK_REPLY);
                NativeMethods.UnregisterHotKey(_hwnd, HK_INTERACT);
            }
            base.OnClosed(e);
        }

        // ---------- Configuration ----------

        public void ApplyConfig(bool reregisterHotkeys = true)
        {
            _cfg.Sanitize();
            Width = _cfg.Width;
            Height = _cfg.Height;

            if (_cfg.Left.HasValue && _cfg.Top.HasValue && IsOnScreen(_cfg.Left.Value, _cfg.Top.Value))
            {
                Left = _cfg.Left.Value;
                Top = _cfg.Top.Value;
            }
            else
            {
                // Écran principal, en bas à gauche
                var wa = SystemParameters.WorkArea;
                Left = wa.Left + 20;
                Top = Math.Max(wa.Top, wa.Bottom - Height - 120);
            }

            Opacity = _cfg.WindowOpacity;
            _root.Background = ParseBrush(_cfg.PanelBackground, Brushes.Transparent);
            _panel.VerticalAlignment = _cfg.NewestOnTop ? VerticalAlignment.Top : VerticalAlignment.Bottom;

            bool horiz = _cfg.HorizontalScroll;
            if (_lastHorizontal.HasValue && _lastHorizontal.Value != horiz) ClearAll();
            _lastHorizontal = horiz;
            _panel.Visibility = horiz ? Visibility.Collapsed : Visibility.Visible;
            _ticker.Visibility = horiz ? Visibility.Visible : Visibility.Collapsed;
            if (horiz && !_tickerHooked) { CompositionTarget.Rendering += OnRendering; _tickerHooked = true; _lastRender = TimeSpan.Zero; }
            if (!horiz && _tickerHooked) { CompositionTarget.Rendering -= OnRendering; _tickerHooked = false; }
            UpdateEditLabel();

            // Reconstruit les messages existants avec le nouveau style
            foreach (var en in _entries)
            {
                en.Element = BuildElement(en.Msg);
                if (en.Fading) en.Element.Opacity = 0;
            }
            RenderAll();

            if (reregisterHotkeys && _hwnd != IntPtr.Zero) RegisterHotkeys();
        }

        public void StorePosition()
        {
            _cfg.Left = Math.Round(Left);
            _cfg.Top = Math.Round(Top);
            _cfg.Width = Math.Round(ActualWidth > 0 ? ActualWidth : Width);
            _cfg.Height = Math.Round(ActualHeight > 0 ? ActualHeight : Height);
        }

        private static bool IsOnScreen(double x, double y)
        {
            return x >= SystemParameters.VirtualScreenLeft - 50 &&
                   y >= SystemParameters.VirtualScreenTop - 50 &&
                   x < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50 &&
                   y < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50;
        }

        // ---------- Mode édition / visibilité ----------

        public void SetEditMode(bool on)
        {
            if (_editMode == on) return;
            _editMode = on;
            _editFrame.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on && !IsVisible) Show();
            if (on && _interactive) SetInteractive(false);
            SetClickThrough(!on);
            if (!on) StorePosition();
            UpdateEditLabel();
            EditModeChanged?.Invoke();
        }

        public void ToggleVisible()
        {
            if (IsVisible) { if (_editMode) SetEditMode(false); Hide(); }
            else Show();
        }

        public void SetStatus(string status)
        {
            _statusLabel.Text = status;
        }

        /// <summary>Édition Pro : rend les messages cliquables (répondre / modération).</summary>
        public void SetInteractive(bool on)
        {
            if (!Edition.IsPro || _interactive == on) return;
            if (on && _editMode) SetEditMode(false);
            _interactive = on;
            _interactFrame.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on && !IsVisible) Show();
            SetClickThrough(!on);
            Cursor = on ? Cursors.Hand : null;
            InteractiveModeChanged?.Invoke();
        }

        private void UpdateEditLabel()
        {
            _editLabel.Text = "MODE ÉDITION\nGlisser pour déplacer, coin bas-droit pour redimensionner.\n"
                              + _cfg.HotkeyEditMode + " pour verrouiller.";
        }

        private void SetClickThrough(bool on)
        {
            if (_hwnd == IntPtr.Zero) return;
            int ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            if (on) ex |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE;
            else ex &= ~(NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE);
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex);
        }

        private void ForceTopmost()
        {
            if (_hwnd == IntPtr.Zero || !IsVisible) return;
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            TopmostReasserted?.Invoke();
        }

        // ---------- Raccourcis globaux ----------

        private void RegisterHotkeys()
        {
            NativeMethods.UnregisterHotKey(_hwnd, HK_EDIT);
            NativeMethods.UnregisterHotKey(_hwnd, HK_TOGGLE);
            NativeMethods.UnregisterHotKey(_hwnd, HK_REPLY);
            NativeMethods.UnregisterHotKey(_hwnd, HK_INTERACT);
            var errors = new List<string>();
            TryRegister(HK_EDIT, _cfg.HotkeyEditMode, errors);
            TryRegister(HK_TOGGLE, _cfg.HotkeyToggleVisible, errors);
            if (Edition.IsPro)
            {
                TryRegister(HK_REPLY, _cfg.HotkeyReply, errors);
                TryRegister(HK_INTERACT, _cfg.HotkeyInteractive, errors);
            }
            if (errors.Count > 0) HotkeyError?.Invoke(string.Join("\n", errors));
        }

        private void TryRegister(int id, string combo, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(combo)) return;
            if (!NativeMethods.TryParseHotkey(combo, out uint mods, out uint vk))
            {
                errors.Add("Raccourci invalide : " + combo);
                return;
            }
            if (!NativeMethods.RegisterHotKey(_hwnd, id, mods | NativeMethods.MOD_NOREPEAT, vk))
                errors.Add("Raccourci déjà utilisé par un autre programme : " + combo);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HK_EDIT) SetEditMode(!_editMode);
                else if (id == HK_TOGGLE) ToggleVisible();
                else if (id == HK_REPLY) ReplyHotkeyPressed?.Invoke();
                else if (id == HK_INTERACT) SetInteractive(!_interactive);
                handled = true;
            }
            return IntPtr.Zero;
        }

        // ---------- Messages ----------

        public void AddMessage(ChatMessage m)
        {
            if (m.IsSystem && !_cfg.ShowSystemMessages) return;
            if (!m.IsSystem)
            {
                if (_cfg.HideCommands && m.Text.StartsWith("!")) return;
                if (_cfg.IgnoredUsers.Any(u => string.Equals(u.Trim(), m.Login, StringComparison.OrdinalIgnoreCase))) return;
            }

            var en = new Entry { Msg = m, Created = DateTime.UtcNow, Element = BuildElement(m) };
            _entries.Add(en);

            if (_cfg.HorizontalScroll)
            {
                // File d'attente : MaxMessages = nombre max de messages en attente
                var pending = _entries.Where(x => !x.Started).ToList();
                for (int i = 0; i < pending.Count - _cfg.MaxMessages; i++) RemoveEntry(pending[i]);
                return;
            }

            if (_cfg.NewestOnTop) _panel.Children.Insert(0, en.Element);
            else _panel.Children.Add(en.Element);

            while (_entries.Count > _cfg.MaxMessages) RemoveEntry(_entries[0]);
        }

        public void DeleteMessage(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            foreach (var en in _entries.Where(x => x.Msg.Id == id).ToList()) RemoveEntry(en);
        }

        public void ClearChat(string channel, string login)
        {
            var targets = _entries.Where(x => !x.Msg.IsSystem &&
                                              (string.IsNullOrEmpty(channel) || x.Msg.Channel == channel) &&
                                              (login == null || x.Msg.Login == login)).ToList();
            foreach (var en in targets) RemoveEntry(en);
        }

        public void ClearAll()
        {
            _entries.Clear();
            _panel.Children.Clear();
            _ticker.Children.Clear();
        }

        private void RemoveEntry(Entry en)
        {
            if (!_entries.Remove(en)) return;
            _panel.Children.Remove(en.Element);
            _ticker.Children.Remove(en.Element);
        }

        private void RenderAll()
        {
            _panel.Children.Clear();
            _ticker.Children.Clear();
            if (_cfg.HorizontalScroll)
            {
                foreach (var en in _entries.Where(x => x.Started))
                {
                    _ticker.Children.Add(en.Element);
                    Canvas.SetLeft(en.Element, en.X);
                }
                return;
            }
            IEnumerable<Entry> order = _cfg.NewestOnTop ? Enumerable.Reverse(_entries) : _entries;
            foreach (var en in order) _panel.Children.Add(en.Element);
        }

        private void ExpireMessages()
        {
            if (_cfg.MessageLifetimeSeconds <= 0 || _editMode || _interactive || _cfg.HorizontalScroll) return;
            var now = DateTime.UtcNow;
            foreach (var en in _entries.ToList())
            {
                if (en.Fading || (now - en.Created).TotalSeconds < _cfg.MessageLifetimeSeconds) continue;
                en.Fading = true;
                var target = en;
                var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(600));
                anim.Completed += (s, e) => RemoveEntry(target);
                target.Element.BeginAnimation(OpacityProperty, anim);
            }
        }

        // ---------- Bandeau horizontal ----------

        private void OnRendering(object sender, EventArgs e)
        {
            var rt = e is RenderingEventArgs re ? re.RenderingTime : TimeSpan.Zero;
            if (_lastRender == TimeSpan.Zero) { _lastRender = rt; return; }
            double dt = (rt - _lastRender).TotalSeconds;
            if (dt <= 0) return;
            _lastRender = rt;
            if (dt > 0.1) dt = 0.1;
            if (!IsVisible) return;

            double w = _ticker.ActualWidth, h = _ticker.ActualHeight;
            double move = _cfg.ScrollSpeed * dt;
            Entry last = null;

            foreach (var en in _entries.ToList())
            {
                if (!en.Started) continue;
                en.X -= move;
                Canvas.SetLeft(en.Element, en.X);
                Canvas.SetTop(en.Element, Math.Max(0, (h - ElemHeight(en.Element)) / 2));
                if (en.X + ElemWidth(en.Element) < 0) RemoveEntry(en);
                else last = en;
            }

            var next = _entries.FirstOrDefault(x => !x.Started);
            if (next != null && (last == null || last.X + ElemWidth(last.Element) + _cfg.ScrollGap <= w))
            {
                next.Started = true;
                next.X = w;
                _ticker.Children.Add(next.Element);
                next.Element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(next.Element, w);
                Canvas.SetTop(next.Element, Math.Max(0, (h - ElemHeight(next.Element)) / 2));
            }
        }

        private static double ElemWidth(FrameworkElement el)
        {
            if (el.ActualWidth > 0) return el.ActualWidth;
            el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return el.DesiredSize.Width;
        }

        private static double ElemHeight(FrameworkElement el)
        {
            if (el.ActualHeight > 0) return el.ActualHeight;
            el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return el.DesiredSize.Height;
        }

        private FrameworkElement BuildElement(ChatMessage m)
        {
            var textBrush = ParseBrush(_cfg.TextColor, Brushes.White);
            var tb = new TextBlock
            {
                TextWrapping = _cfg.HorizontalScroll ? TextWrapping.NoWrap : TextWrapping.Wrap,
                FontFamily = SafeFont(_cfg.FontFamily),
                FontSize = _cfg.FontSize,
                Foreground = textBrush
            };
            if (_cfg.TextShadow)
                tb.Effect = new DropShadowEffect { ShadowDepth = 1, BlurRadius = 3, Color = Colors.Black, Opacity = 1 };

            if (m.IsSystem)
            {
                tb.Inlines.Add(new Run(m.Text) { FontStyle = FontStyles.Italic, Foreground = Brushes.LightGray });
            }
            else
            {
                var nameBrush = new SolidColorBrush(NameColor(m));
                nameBrush.Freeze();
                if (_cfg.MultiChat && _cfg.ShowChannelName && !string.IsNullOrEmpty(m.Channel))
                    tb.Inlines.Add(BuildChannelBadge(m.Channel));
                if (_cfg.ShowAvatars && !string.IsNullOrEmpty(m.UserId))
                    tb.Inlines.Add(BuildAvatar(m, nameBrush));
                tb.Inlines.Add(new Run(m.DisplayName) { Foreground = nameBrush, FontWeight = FontWeights.Bold });
                tb.Inlines.Add(new Run(m.IsAction ? " " : " : "));

                var bodyBrush = m.IsAction ? nameBrush : textBrush;
                foreach (var inline in BuildBody(m, bodyBrush))
                {
                    if (m.IsAction && inline is Run r) r.FontStyle = FontStyles.Italic;
                    tb.Inlines.Add(inline);
                }
            }

            var bubble = new Border
            {
                Background = ParseBrush(_cfg.MessageBackground, Brushes.Transparent),
                CornerRadius = new CornerRadius(_cfg.CornerRadius),
                Padding = new Thickness(7, 3, 7, 3),
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Transparent,
                Margin = _cfg.HorizontalScroll ? new Thickness(0) : new Thickness(0, 2, 0, 2),
                Child = tb
            };

            if (Edition.IsPro && !m.IsSystem)
            {
                var hover = new SolidColorBrush(Color.FromRgb(0x00, 0xC8, 0xFF));
                bubble.MouseEnter += (s, e) => { if (_interactive) bubble.BorderBrush = hover; };
                bubble.MouseLeave += (s, e) => bubble.BorderBrush = Brushes.Transparent;
                bubble.MouseLeftButtonUp += (s, e) =>
                {
                    if (!_interactive) return;
                    e.Handled = true;
                    ReplyRequested?.Invoke(m);
                };
                bubble.MouseRightButtonUp += (s, e) =>
                {
                    if (!_interactive || MessageMenuProvider == null) return;
                    e.Handled = true;
                    var menu = MessageMenuProvider(m);
                    if (menu == null) return;
                    menu.PlacementTarget = bubble;
                    menu.IsOpen = true;
                };
            }
            return bubble;
        }

        private Inline BuildChannelBadge(string channel)
        {
            int h = 0;
            foreach (char ch in channel) h = unchecked(h * 31 + ch);
            var c = DefaultPalette[Math.Abs(h % DefaultPalette.Length)];
            var bg = new SolidColorBrush(Color.FromArgb(0xCC, c.R, c.G, c.B));
            bg.Freeze();
            var badge = new Border
            {
                Background = bg,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 0, 4, 1),
                Margin = new Thickness(0, 0, 6, 0),
                Child = new TextBlock
                {
                    Text = channel,
                    FontSize = Math.Max(8, _cfg.FontSize * 0.7),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White
                }
            };
            return new InlineUIContainer(badge) { BaselineAlignment = BaselineAlignment.Center };
        }

        private Inline BuildAvatar(ChatMessage m, Brush placeholder)
        {
            double size = Math.Round(_cfg.FontSize * 1.5);
            var avatar = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(_cfg.RoundAvatars ? size / 2 : 3),
                Background = placeholder,
                Opacity = 0.5,
                Margin = new Thickness(0, 0, 6, 0),
                ClipToBounds = true
            };
            AvatarLoader.Get(m.UserId, bmp =>
            {
                var brush = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                brush.Freeze();
                avatar.Background = brush;
                avatar.Opacity = 1;
            });
            return new InlineUIContainer(avatar) { BaselineAlignment = BaselineAlignment.Center };
        }

        private IEnumerable<Inline> BuildBody(ChatMessage m, Brush brush)
        {
            var result = new List<Inline>();
            if (!_cfg.ShowEmotes || m.Emotes.Count == 0)
            {
                result.Add(new Run(m.Text) { Foreground = brush });
                return result;
            }

            // Les positions d'emotes Twitch sont en points de code Unicode
            var cps = new List<string>();
            for (int i = 0; i < m.Text.Length;)
            {
                if (char.IsSurrogatePair(m.Text, i)) { cps.Add(m.Text.Substring(i, 2)); i += 2; }
                else { cps.Add(m.Text[i].ToString()); i++; }
            }

            int pos = 0;
            foreach (var em in m.Emotes)
            {
                if (em.Start < pos || em.End >= cps.Count || em.End < em.Start) continue;
                if (em.Start > pos)
                    result.Add(new Run(string.Concat(cps.Skip(pos).Take(em.Start - pos))) { Foreground = brush });

                string code = string.Concat(cps.Skip(em.Start).Take(em.End - em.Start + 1));
                var img = new Image
                {
                    Height = _cfg.FontSize * 1.7,
                    MinWidth = _cfg.FontSize * 1.7,
                    Stretch = Stretch.Uniform,
                    ToolTip = code
                };
                EmoteLoader.Get(em.Id, _cfg.AnimatedEmotes, f => EmoteLoader.Apply(img, f));
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                result.Add(new InlineUIContainer(img) { BaselineAlignment = BaselineAlignment.Center });
                pos = em.End + 1;
            }
            if (pos < cps.Count)
                result.Add(new Run(string.Concat(cps.Skip(pos))) { Foreground = brush });
            return result;
        }

        private Color NameColor(ChatMessage m)
        {
            Color c;
            if (_cfg.UseUserColors && TryParseColor(m.Color, out var uc)) c = uc;
            else if (_cfg.UseUserColors)
            {
                int h = 0;
                foreach (char ch in m.Login) h = unchecked(h * 31 + ch);
                c = DefaultPalette[Math.Abs(h % DefaultPalette.Length)];
            }
            else if (!TryParseColor(_cfg.DefaultNameColor, out c)) c = Color.FromRgb(0x91, 0x46, 0xFF);

            if (_cfg.ReadableNames)
            {
                double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                if (_cfg.OverlayTheme == "Clair")
                {
                    // Fond clair : assombrir les pseudos trop pâles
                    if (lum > 0.6)
                    {
                        byte D(byte v) => (byte)(v * 0.55);
                        c = Color.FromRgb(D(c.R), D(c.G), D(c.B));
                    }
                }
                else if (lum < 0.4)
                {
                    byte L(byte v) => (byte)(v + (255 - v) * 0.45);
                    c = Color.FromRgb(L(c.R), L(c.G), L(c.B));
                }
            }
            return c;
        }

        // ---------- Utilitaires ----------

        public static bool TryParseColor(string s, out Color c)
        {
            c = Colors.Transparent;
            if (string.IsNullOrWhiteSpace(s)) return false;
            try
            {
                c = (Color)ColorConverter.ConvertFromString(s.Trim());
                return true;
            }
            catch { return false; }
        }

        public static Brush ParseBrush(string s, Brush fallback)
        {
            if (!TryParseColor(s, out var c)) return fallback;
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static FontFamily SafeFont(string name)
        {
            try { return new FontFamily(string.IsNullOrWhiteSpace(name) ? "Segoe UI" : name); }
            catch { return new FontFamily("Segoe UI"); }
        }
    }
}
