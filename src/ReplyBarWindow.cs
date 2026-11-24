using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace TchatOverlay
{
    /// <summary>Édition Pro : barre de saisie flottante (répondre / écrire dans le tchat).</summary>
    public class ReplyBarWindow : Window
    {
        private readonly Config _cfg;
        private readonly Border _frame;
        private readonly Grid _replyHeader;
        private readonly TextBlock _replyText;
        private readonly ComboBox _channelBox;
        private readonly TextBox _input;
        private readonly Button _sendButton;
        private readonly Button _modButton;
        private readonly TextBlock _status;
        private readonly TextBlock _counter;
        private readonly Thumb _resize;
        private readonly List<string> _history = new List<string>();
        private int _historyIndex = -1;
        private ChatMessage _replyTo;
        private IntPtr _hwnd;
        private IntPtr _previousForeground;
        private bool _editMode;
        private bool _sending;

        /// <summary>(chaîne, texte, message auquel on répond) → null si OK, sinon message d'erreur.</summary>
        public Func<string, string, ChatMessage, Task<string>> SendHandler;
        public Func<List<string>> ChannelsProvider;
        public Func<string, ContextMenu> ModMenuProvider;

        public ReplyBarWindow(Config cfg)
        {
            _cfg = cfg;
            Title = Edition.Name + " — Réponse";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.Manual;

            // En-tête « Réponse à @x »
            _replyText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 0, 4, 0) };
            var cancelReply = new Button { Content = "x", Width = 22, Padding = new Thickness(0), ToolTip = "Annuler la réponse", HorizontalAlignment = HorizontalAlignment.Right };
            cancelReply.Click += (s, e) => SetReplyTo(null);
            _replyHeader = new Grid { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 4) };
            _replyHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _replyHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(cancelReply, 1);
            _replyHeader.Children.Add(_replyText);
            _replyHeader.Children.Add(cancelReply);

            // Ligne de saisie
            _channelBox = new ComboBox { MinWidth = 90, Margin = new Thickness(0, 0, 6, 0), Visibility = Visibility.Collapsed, ToolTip = "Chaîne de destination" };
            _input = new TextBox { MaxLength = 500, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 2, 4, 2), BorderThickness = new Thickness(1) };
            _input.PreviewKeyDown += OnInputKeyDown;
            _input.TextChanged += (s, e) => _counter.Text = _input.Text.Length > 400 ? (500 - _input.Text.Length).ToString() : "";
            _sendButton = new Button { Content = "Envoyer", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
            _sendButton.Click += async (s, e) => await SendAsync();
            _modButton = new Button { Content = "Modération", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
            _modButton.Click += (s, e) => OpenModMenu();
            _counter = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Foreground = Brushes.Orange };

            var row = new DockPanel();
            DockPanel.SetDock(_channelBox, Dock.Left);
            DockPanel.SetDock(_modButton, Dock.Right);
            DockPanel.SetDock(_sendButton, Dock.Right);
            DockPanel.SetDock(_counter, Dock.Right);
            row.Children.Add(_channelBox);
            row.Children.Add(_modButton);
            row.Children.Add(_sendButton);
            row.Children.Add(_counter);
            row.Children.Add(_input);

            _status = new TextBlock { FontSize = 11, Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(2, 4, 0, 0) };

            var stack = new StackPanel();
            stack.Children.Add(_replyHeader);
            stack.Children.Add(row);
            stack.Children.Add(_status);

            _resize = new Thumb
            {
                Width = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                Cursor = Cursors.SizeWE,
                Opacity = 0.6,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 0, -6, 0)
            };
            _resize.DragDelta += (s, e) => Width = Math.Max(200, Width + e.HorizontalChange);

            var grid = new Grid();
            grid.Children.Add(stack);
            grid.Children.Add(_resize);

            _frame = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8), BorderThickness = new Thickness(2), Child = grid };
            _frame.MouseLeftButtonDown += (s, e) =>
            {
                if (_editMode && !(e.OriginalSource is TextBox)) { try { DragMove(); } catch { } }
            };
            Content = _frame;

            Deactivated += (s, e) =>
            {
                // Clic en dehors (retour au jeu) : on masque si rien n'est en cours de saisie
                if (!_editMode && !_sending && !_cfg.ReplyBarAlwaysVisible && _input.Text.Length == 0 && _replyTo == null) Hide();
            };

            ApplyConfig();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            int ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex | NativeMethods.WS_EX_TOOLWINDOW);
        }

        // ---------- Configuration ----------

        public void ApplyConfig()
        {
            Width = _cfg.ReplyBarWidth;
            if (_cfg.ReplyBarLeft.HasValue && _cfg.ReplyBarTop.HasValue)
            {
                Left = _cfg.ReplyBarLeft.Value;
                Top = _cfg.ReplyBarTop.Value;
            }
            else
            {
                var wa = SystemParameters.WorkArea;
                Left = wa.Left + (wa.Width - Width) / 2;
                Top = wa.Bottom - 120;
            }
            Opacity = _cfg.ReplyBarOpacity;

            var bg = OverlayWindow.ParseBrush(_cfg.ReplyBarBackground, new SolidColorBrush(Color.FromArgb(0xE6, 0x18, 0x18, 0x18)));
            var fg = OverlayWindow.ParseBrush(_cfg.ReplyBarTextColor, Brushes.White);
            _frame.Background = bg;
            _frame.BorderBrush = _editMode ? new SolidColorBrush(Color.FromRgb(0x91, 0x46, 0xFF)) : Brushes.Transparent;
            _input.FontSize = _cfg.ReplyBarFontSize;
            _input.Foreground = fg;
            _input.CaretBrush = fg;
            _input.Background = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
            _input.BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x91, 0x46, 0xFF));
            _replyText.Foreground = fg;
            _replyText.FontSize = Math.Max(10, _cfg.ReplyBarFontSize - 2);
            _modButton.Visibility = _cfg.ModeratorOptions ? Visibility.Visible : Visibility.Collapsed;
            RefreshChannels();

            if (_cfg.ReplyBarAlwaysVisible || _editMode) Show();
            else if (IsVisible && !IsActive) Hide();
        }

        public void RefreshChannels()
        {
            var list = ChannelsProvider?.Invoke() ?? new List<string>();
            string current = _channelBox.SelectedItem as string;
            _channelBox.ItemsSource = list;
            _channelBox.SelectedItem = list.Contains(current) ? current : (list.Count > 0 ? list[0] : null);
            _channelBox.Visibility = list.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        public void StorePosition()
        {
            _cfg.ReplyBarLeft = Math.Round(Left);
            _cfg.ReplyBarTop = Math.Round(Top);
            _cfg.ReplyBarWidth = Math.Round(ActualWidth > 0 ? ActualWidth : Width);
        }

        public void SetEditMode(bool on)
        {
            _editMode = on;
            _resize.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            _frame.BorderBrush = on ? new SolidColorBrush(Color.FromRgb(0x91, 0x46, 0xFF)) : Brushes.Transparent;
            if (on) Show();
            else
            {
                StorePosition();
                if (!_cfg.ReplyBarAlwaysVisible && _input.Text.Length == 0) Hide();
            }
        }

        public void ForceTopmost()
        {
            if (_hwnd == IntPtr.Zero || !IsVisible) return;
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        // ---------- Ouverture / fermeture ----------

        /// <summary>Ouvre la barre avec le focus clavier (raccourci ou clic sur un message).</summary>
        public void OpenForInput(ChatMessage replyTo)
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg != _hwnd || _hwnd == IntPtr.Zero) _previousForeground = fg;

            RefreshChannels();
            SetReplyTo(replyTo);
            _status.Visibility = Visibility.Collapsed;
            Show();
            Activate();
            if (_hwnd != IntPtr.Zero) NativeMethods.SetForegroundWindow(_hwnd);
            _input.Focus();
            Keyboard.Focus(_input);
            _input.CaretIndex = _input.Text.Length;
        }

        private void CloseInput()
        {
            SetReplyTo(null);
            if (!_cfg.ReplyBarAlwaysVisible && !_editMode) Hide();
            if (_cfg.ReturnFocusToGame && _previousForeground != IntPtr.Zero)
                NativeMethods.SetForegroundWindow(_previousForeground);
            _previousForeground = IntPtr.Zero;
        }

        private void SetReplyTo(ChatMessage m)
        {
            _replyTo = m;
            if (m == null)
            {
                _replyHeader.Visibility = Visibility.Collapsed;
                return;
            }
            string preview = m.Text.Length > 60 ? m.Text.Substring(0, 60) + "..." : m.Text;
            _replyText.Text = "Réponse à @" + m.DisplayName + " : " + preview;
            _replyHeader.Visibility = Visibility.Visible;
            if (!string.IsNullOrEmpty(m.Channel) && _channelBox.ItemsSource is List<string> list && list.Contains(m.Channel))
                _channelBox.SelectedItem = m.Channel;
        }

        // ---------- Saisie ----------

        private async void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await SendAsync();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _input.Text = "";
                CloseInput();
            }
            else if (e.Key == Key.Up && _history.Count > 0)
            {
                e.Handled = true;
                _historyIndex = _historyIndex <= 0 ? _history.Count - 1 : _historyIndex - 1;
                _input.Text = _history[_historyIndex];
                _input.CaretIndex = _input.Text.Length;
            }
        }

        private async Task SendAsync()
        {
            string text = _input.Text.Trim();
            if (text.Length == 0 || _sending || SendHandler == null) return;
            string channel = _replyTo?.Channel;
            if (string.IsNullOrEmpty(channel)) channel = _channelBox.SelectedItem as string;
            if (string.IsNullOrEmpty(channel))
            {
                ShowStatus("Aucune chaîne connectée.");
                return;
            }

            _sending = true;
            _sendButton.IsEnabled = false;
            string error;
            try { error = await SendHandler(channel, text, _replyTo); }
            catch (Exception ex) { error = ex.Message; }
            _sending = false;
            _sendButton.IsEnabled = true;

            if (error != null)
            {
                ShowStatus(error);
                _input.Focus();
                return;
            }
            _history.Remove(text);
            _history.Add(text);
            if (_history.Count > 30) _history.RemoveAt(0);
            _historyIndex = -1;
            _input.Text = "";
            _status.Visibility = Visibility.Collapsed;
            CloseInput();
        }

        private void ShowStatus(string text)
        {
            _status.Text = text;
            _status.Visibility = Visibility.Visible;
        }

        private void OpenModMenu()
        {
            string channel = _channelBox.SelectedItem as string ?? _replyTo?.Channel;
            if (string.IsNullOrEmpty(channel) || ModMenuProvider == null) return;
            var menu = ModMenuProvider(channel);
            if (menu == null) return;
            menu.PlacementTarget = _modButton;
            menu.Placement = PlacementMode.Top;
            menu.IsOpen = true;
        }
    }
}
