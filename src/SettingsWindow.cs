using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TchatOverlay
{
    public class SettingsWindow : Window
    {
        private readonly Config _cfg;
        private readonly Config _backup;
        private readonly Action _apply;
        private readonly Action _onAccountChanged;
        private TextBox _clientIdBox, _codeBox;
        private TextBlock _accountText;
        private Button _loginButton;
        private CancellationTokenSource _loginCts;
        private bool _closed;
        private readonly StackPanel _sections = new StackPanel { Margin = new Thickness(10) };
        private readonly List<Expander> _expanders = new List<Expander>();
        private readonly List<Action> _readers = new List<Action>();
        private Grid _grid;
        private TextBox _textColorBox, _msgBgBox, _nameColorBox;
        private CheckBox _shadowBox;
        private bool _building = true;
        private int _row;

        public SettingsWindow(Config cfg, Action apply, Action onAccountChanged)
        {
            _cfg = cfg;
            _backup = cfg.Clone();
            _apply = apply;
            _onAccountChanged = onAccountChanged;
            Closed += (s, e) => { _closed = true; _loginCts?.Cancel(); };

            Title = Edition.Name + " — Paramètres";
            try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/TchatOverlay.ico")); } catch { }
            Width = 520;
            Height = 720;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            ApplyUiTheme(_cfg.UiTheme);

            Section("Connexion");
            AccountSection();
            Text("Chaîne Twitch", () => _cfg.Channel, v => _cfg.Channel = TwitchIrcClient.NormalizeChannel(v));
            Note("Vide = chaîne du compte connecté. Sans compte : lecture anonyme (pas d'avatars).");

            Section("Multi-tchat");
            Check("Activer le multi-tchat", () => _cfg.MultiChat, v => _cfg.MultiChat = v);
            Text("Chaînes supplémentaires (virgules)", () => string.Join(", ", _cfg.ExtraChannels),
                 v => _cfg.ExtraChannels = v.Split(',').Select(TwitchIrcClient.NormalizeChannel).Where(c => c.Length > 0).Distinct().ToList());
            Check("Afficher le nom de la chaîne", () => _cfg.ShowChannelName, v => _cfg.ShowChannelName = v);
            Note("Ex. : ami1, ami2, twitch.tv/ami3. Chaque message porte une étiquette colorée avec le nom de sa chaîne.");

            Section("Interface");
            Choice("Thème de cette fenêtre", new[] { "Système", "Sombre", "Clair" }, () => _cfg.UiTheme,
                   v => _cfg.UiTheme = v, v => ApplyUiTheme(v));

            Section("Position et taille (pixels)");
            Number("Gauche (X)", () => _cfg.Left ?? 0, v => _cfg.Left = v);
            Number("Haut (Y)", () => _cfg.Top ?? 0, v => _cfg.Top = v);
            Number("Largeur", () => _cfg.Width, v => _cfg.Width = v);
            Number("Hauteur", () => _cfg.Height, v => _cfg.Height = v);

            Section("Apparence");
            var fonts = Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(s => s).ToList();
            Combo("Police", fonts, () => _cfg.FontFamily, v => _cfg.FontFamily = v);
            Number("Taille du texte", () => _cfg.FontSize, v => _cfg.FontSize = v);
            Choice("Thème du tchat", new[] { "Sombre", "Clair" }, () => _cfg.OverlayTheme,
                   v => _cfg.OverlayTheme = v, v => ApplyOverlayPreset(v));
            Note("Changer le thème du tchat remplace les couleurs ci-dessous par un préréglage (modifiables ensuite).");
            _textColorBox = ColorField("Couleur du texte", () => _cfg.TextColor, v => _cfg.TextColor = v);
            _msgBgBox = ColorField("Fond des messages", () => _cfg.MessageBackground, v => _cfg.MessageBackground = v);
            ColorField("Fond du cadre", () => _cfg.PanelBackground, v => _cfg.PanelBackground = v);
            Number("Arrondi des bulles", () => _cfg.CornerRadius, v => _cfg.CornerRadius = v);
            SliderField("Opacité générale", 0.1, 1.0, () => _cfg.WindowOpacity, v => _cfg.WindowOpacity = v);
            _shadowBox = Check("Ombre portée du texte", () => _cfg.TextShadow, v => _cfg.TextShadow = v);
            Check("Couleurs des pseudos Twitch", () => _cfg.UseUserColors, v => _cfg.UseUserColors = v);
            Check("Éclaircir les pseudos sombres", () => _cfg.ReadableNames, v => _cfg.ReadableNames = v);
            _nameColorBox = ColorField("Couleur pseudo par défaut", () => _cfg.DefaultNameColor, v => _cfg.DefaultNameColor = v);
            Check("Afficher les emotes", () => _cfg.ShowEmotes, v => _cfg.ShowEmotes = v);
            Check("Emotes animées", () => _cfg.AnimatedEmotes, v => _cfg.AnimatedEmotes = v);
            Check("Afficher les avatars", () => _cfg.ShowAvatars, v => _cfg.ShowAvatars = v);
            Check("Avatars ronds", () => _cfg.RoundAvatars, v => _cfg.RoundAvatars = v);
            var avatarsBtn = new Button { Content = "Ouvrir le dossier des avatars", Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left };
            avatarsBtn.Click += (s, e) => App.OpenFolder(AvatarLoader.Folder);
            AddRow("Avatars téléchargés", avatarsBtn);
            Check("Nouveaux messages en haut", () => _cfg.NewestOnTop, v => _cfg.NewestOnTop = v);

            Section("Défilement horizontal");
            Check("Activer le bandeau défilant", () => _cfg.HorizontalScroll, v => _cfg.HorizontalScroll = v);
            Number("Vitesse (pixels / s)", () => _cfg.ScrollSpeed, v => _cfg.ScrollSpeed = v);
            Number("Espace entre messages (px)", () => _cfg.ScrollGap, v => _cfg.ScrollGap = v);
            Note("En mode bandeau : « Messages max » = file d'attente max, la durée d'affichage est ignorée. Réduire la hauteur de la fenêtre à une ligne (~40 px).");

            Section("Comportement");
            Number("Durée d'affichage (s, 0 = infini)", () => _cfg.MessageLifetimeSeconds, v => _cfg.MessageLifetimeSeconds = (int)v);
            Number("Messages max à l'écran", () => _cfg.MaxMessages, v => _cfg.MaxMessages = (int)v);
            Check("Masquer les commandes (!...)", () => _cfg.HideCommands, v => _cfg.HideCommands = v);
            Check("Afficher les messages système", () => _cfg.ShowSystemMessages, v => _cfg.ShowSystemMessages = v);
            Text("Pseudos ignorés (virgules)", () => string.Join(", ", _cfg.IgnoredUsers),
                 v => _cfg.IgnoredUsers = v.Split(',').Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).ToList());

            if (Edition.IsPro)
            {
                Section("Barre de réponse");
                Check("Toujours visible", () => _cfg.ReplyBarAlwaysVisible, v => _cfg.ReplyBarAlwaysVisible = v);
                Check("Rendre le focus au jeu après envoi", () => _cfg.ReturnFocusToGame, v => _cfg.ReturnFocusToGame = v);
                Check("Afficher mes messages dans l'overlay", () => _cfg.ShowOwnMessages, v => _cfg.ShowOwnMessages = v);
                Check("Quitter le mode interactif après un clic Répondre", () => _cfg.ExitInteractiveAfterReply, v => _cfg.ExitInteractiveAfterReply = v);
                Number("Largeur", () => _cfg.ReplyBarWidth, v => _cfg.ReplyBarWidth = v);
                Number("Gauche (X, vide = centré)", () => _cfg.ReplyBarLeft ?? double.NaN, v => _cfg.ReplyBarLeft = double.IsNaN(v) ? (double?)null : v);
                Number("Haut (Y, vide = bas de l'écran)", () => _cfg.ReplyBarTop ?? double.NaN, v => _cfg.ReplyBarTop = double.IsNaN(v) ? (double?)null : v);
                Number("Taille du texte", () => _cfg.ReplyBarFontSize, v => _cfg.ReplyBarFontSize = v);
                ColorField("Fond", () => _cfg.ReplyBarBackground, v => _cfg.ReplyBarBackground = v);
                ColorField("Couleur du texte", () => _cfg.ReplyBarTextColor, v => _cfg.ReplyBarTextColor = v);
                SliderField("Opacité", 0.2, 1.0, () => _cfg.ReplyBarOpacity, v => _cfg.ReplyBarOpacity = v);
                Note("Entrée = envoyer, Échap = fermer, flèche haut = message précédent. Commandes : /me, /ban, /timeout, /unban, /clear, /slow, /emoteonly, /followers, /subscribers (+off), /aide. Déplacer / élargir : mode édition.");

                Section("Modération");
                Check("Activer les options modérateur", () => _cfg.ModeratorOptions, v => _cfg.ModeratorOptions = v);
                Text("Durées de timeout (s, virgules)", () => string.Join(", ", _cfg.TimeoutDurations),
                     v => _cfg.TimeoutDurations = v.Split(',').Select(x => int.TryParse(x.Trim(), out int n) ? n : 0).Where(n => n > 0).ToList());
                Check("Confirmer avant de bannir", () => _cfg.ConfirmBan, v => _cfg.ConfirmBan = v);
                Number("Mode lent (secondes, 3 à 120)", () => _cfg.SlowModeSeconds, v => _cfg.SlowModeSeconds = (int)v);
                Note("Actions disponibles uniquement sur les chaînes où le compte connecté est modérateur ou propriétaire. Mode interactif : clic sur un message = répondre, clic droit = supprimer, timeout, bannir.");
            }

            Section("Fichiers");
            Note("Configuration : " + Config.FilePath);
            var cfgBtn = new Button { Content = "Ouvrir le dossier de configuration", Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left };
            cfgBtn.Click += (s, e) => App.OpenFolder(Config.DataDir);
            AddRow("Dossier", cfgBtn);

            Section("Raccourcis globaux");
            Text("Mode édition", () => _cfg.HotkeyEditMode, v => _cfg.HotkeyEditMode = v.Trim());
            Text("Afficher / masquer", () => _cfg.HotkeyToggleVisible, v => _cfg.HotkeyToggleVisible = v.Trim());
            if (Edition.IsPro)
            {
                Text("Ouvrir la barre de réponse", () => _cfg.HotkeyReply, v => _cfg.HotkeyReply = v.Trim());
                Text("Mode interactif (clic messages)", () => _cfg.HotkeyInteractive, v => _cfg.HotkeyInteractive = v.Trim());
            }

            _building = false;

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(14, 8, 14, 14)
            };
            buttons.Children.Add(MakeButton("Appliquer", () => { ReadAll(); _apply(); }));
            buttons.Children.Add(MakeButton("OK", () => { ReadAll(); _apply(); _cfg.Save(); Close(); }, true));
            buttons.Children.Add(MakeButton("Annuler", () =>
            {
                // La connexion du compte et l'état des catégories ne sont pas annulés
                var keep = _cfg.Clone();
                _cfg.CopyFrom(_backup);
                _cfg.CollapsedSections = keep.CollapsedSections;
                _cfg.ClientId = keep.ClientId;
                _cfg.AccountLogin = keep.AccountLogin;
                _cfg.AccessToken = keep.AccessToken;
                _cfg.RefreshToken = keep.RefreshToken;
                _apply();
                _cfg.Save();
                Close();
            }));

            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 10, 14, 0) };
            top.Children.Add(MakeLink("Tout développer", () => _expanders.ForEach(x => x.IsExpanded = true)));
            top.Children.Add(MakeLink("Tout réduire", () => _expanders.ForEach(x => x.IsExpanded = false)));

            var dock = new DockPanel();
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(buttons, Dock.Bottom);
            dock.Children.Add(top);
            dock.Children.Add(buttons);
            dock.Children.Add(new ScrollViewer { Content = _sections, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = dock;
        }

        // ---------- Compte Twitch ----------

        private void AccountSection()
        {
            _clientIdBox = new TextBox { Text = _cfg.ClientId ?? "" };
            if (TwitchAuth.BuiltInClientId.Length > 0) _clientIdBox.ToolTip = "Vide = Client-ID intégré au programme";
            AddRow("Client-ID (dev.twitch.tv)", _clientIdBox);
            _readers.Add(() => _cfg.ClientId = _clientIdBox.Text.Trim());
            Note("dev.twitch.tv/console > Applications > Enregistrer : URL de redirection http://localhost, type de client « Public ».");

            _accountText = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            AddRow("Compte", _accountText);

            _codeBox = new TextBox
            {
                IsReadOnly = true,
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Visibility = Visibility.Collapsed,
                ToolTip = "Code à vérifier sur la page Twitch"
            };
            AddRow("", _codeBox);

            _loginButton = new Button { Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left };
            _loginButton.Click += async (s, e) => await OnLoginClick();
            AddRow("", _loginButton);

            RefreshAccountUi(null);
        }

        private void RefreshAccountUi(string message)
        {
            if (_loginCts != null)
            {
                _loginButton.Content = "Annuler";
                return;
            }
            _codeBox.Visibility = Visibility.Collapsed;
            if (_cfg.IsLoggedIn)
            {
                _accountText.Text = "Connecté : " + _cfg.AccountLogin + (message != null ? "\n" + message : "");
                _loginButton.Content = "Se déconnecter";
            }
            else
            {
                _accountText.Text = message ?? "Non connecté";
                _loginButton.Content = "Se connecter avec Twitch";
            }
        }

        private async Task OnLoginClick()
        {
            // Annulation d'une connexion en cours
            if (_loginCts != null) { _loginCts.Cancel(); return; }

            // Déconnexion
            if (_cfg.IsLoggedIn)
            {
                string oldToken = TwitchAuth.Unprotect(_cfg.AccessToken);
                string cid = TwitchAuth.ClientId(_cfg);
                _cfg.AccessToken = "";
                _cfg.RefreshToken = "";
                _cfg.AccountLogin = "";
                _cfg.Save();
                RefreshAccountUi("Déconnecté.");
                _onAccountChanged();
                await TwitchAuth.RevokeAsync(cid, oldToken);
                return;
            }

            // Connexion
            _cfg.ClientId = _clientIdBox.Text.Trim();
            string clientId = TwitchAuth.ClientId(_cfg);
            if (string.IsNullOrEmpty(clientId))
            {
                RefreshAccountUi("Renseignez d'abord le Client-ID.");
                return;
            }

            string result = null;
            bool wasTopmost = Topmost;
            _loginCts = new CancellationTokenSource();
            RefreshAccountUi(null);
            try
            {
                _accountText.Text = "Demande de code à Twitch...";
                var info = await TwitchAuth.StartDeviceFlowAsync(clientId);

                _codeBox.Text = info.UserCode;
                _codeBox.Visibility = Visibility.Visible;
                _accountText.Text = "Page Twitch ouverte dans le navigateur : vérifiez que ce code s'affiche, puis cliquez sur « Autoriser ».";
                Topmost = false;   // laisser le navigateur passer devant
                OpenUrl(info.VerificationUri);

                var pair = await TwitchAuth.PollDeviceTokenAsync(clientId, info, _loginCts.Token);
                var tokenInfo = await TwitchAuth.ValidateAsync(pair.AccessToken);
                if (tokenInfo == null) throw new Exception("Jeton refusé par Twitch.");
                string login = tokenInfo.Login;
                TwitchAuth.InvalidateCache();

                _cfg.AccessToken = TwitchAuth.Protect(pair.AccessToken);
                _cfg.RefreshToken = TwitchAuth.Protect(pair.RefreshToken);
                _cfg.AccountLogin = login;
                _cfg.Save();
                _onAccountChanged();
            }
            catch (OperationCanceledException) { result = "Connexion annulée."; }
            catch (Exception ex) { result = "Erreur : " + ex.Message; }
            finally
            {
                _loginCts.Dispose();
                _loginCts = null;
                Topmost = wasTopmost;
                if (!_closed) { RefreshAccountUi(result); Activate(); }
            }
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        // ---------- Thèmes ----------

        private void ApplyUiTheme(string theme)
        {
            switch (theme)
            {
                case "Sombre": ThemeMode = ThemeMode.Dark; break;
                case "Clair": ThemeMode = ThemeMode.Light; break;
                default: ThemeMode = ThemeMode.System; break;
            }
            _currentUiTheme = theme;
            ApplyTitleBar();
        }

        private string _currentUiTheme = "Système";

        /// <summary>Barre de titre sombre/claire (Windows 10 20H1+ ; couleur exacte sur Windows 11).</summary>
        private void ApplyTitleBar()
        {
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;   // rappelé dans OnSourceInitialized

            bool dark = _currentUiTheme == "Sombre" || (_currentUiTheme != "Clair" && NativeMethods.SystemUsesDarkTheme());
            int flag = dark ? 1 : 0;
            if (NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref flag, sizeof(int)) != 0)
                NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref flag, sizeof(int));

            // COLORREF = 0x00BBGGRR ; teintes proches du fond Fluent
            int caption = dark ? 0x00202020 : 0x00F3F3F3;
            int text = dark ? 0x00FFFFFF : 0x00000000;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_TEXT_COLOR, ref text, sizeof(int));
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ApplyTitleBar();
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemThemeChanged;
        }

        protected override void OnClosed(EventArgs e)
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemThemeChanged;
            base.OnClosed(e);
        }

        private void OnSystemThemeChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            if (e.Category == Microsoft.Win32.UserPreferenceCategory.General && _currentUiTheme == "Système")
                Dispatcher.BeginInvoke(new Action(ApplyTitleBar));
        }

        private void ApplyOverlayPreset(string theme)
        {
            if (_building || _textColorBox == null) return;
            if (theme == "Clair")
            {
                _textColorBox.Text = "#FF1A1A1A";
                _msgBgBox.Text = "#D9FFFFFF";
                _nameColorBox.Text = "#FF6A2BD9";
                _shadowBox.IsChecked = false;
            }
            else
            {
                _textColorBox.Text = "#FFFFFFFF";
                _msgBgBox.Text = "#99000000";
                _nameColorBox.Text = "#FF9146FF";
                _shadowBox.IsChecked = true;
            }
        }

        private void ReadAll()
        {
            foreach (var r in _readers)
            {
                try { r(); } catch { }
            }
            _cfg.Sanitize();
        }

        private Button MakeButton(string text, Action click, bool isDefault = false)
        {
            var b = new Button { Content = text, MinWidth = 90, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4), IsDefault = isDefault };
            b.Click += (s, e) => click();
            return b;
        }

        // ---------- Construction du formulaire ----------

        private Button MakeLink(string text, Action click)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 2, 8, 2) };
            b.Click += (s, e) => click();
            return b;
        }

        private void NewRow() => _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        /// <summary>Nouvelle catégorie repliable ; l'état est mémorisé dans config.json.</summary>
        private void Section(string title)
        {
            _grid = new Grid { Margin = new Thickness(18, 4, 4, 8) };
            _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _row = 0;

            var header = new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x2C, 0xE8))
            };
            var exp = new Expander
            {
                Header = header,
                Content = _grid,
                IsExpanded = !_cfg.CollapsedSections.Contains(title),
                Margin = new Thickness(0, 4, 0, 4)
            };
            exp.Expanded += (s, e) => { _cfg.CollapsedSections.Remove(title); };
            exp.Collapsed += (s, e) => { if (!_cfg.CollapsedSections.Contains(title)) _cfg.CollapsedSections.Add(title); };
            _expanders.Add(exp);
            _sections.Children.Add(exp);
        }

        private void Note(string text)
        {
            NewRow();
            var t = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 4, 0, 2)
            };
            Grid.SetRow(t, _row);
            Grid.SetColumnSpan(t, 2);
            _grid.Children.Add(t);
            _row++;
        }

        private void AddRow(string label, UIElement control)
        {
            NewRow();
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 8, 3) };
            Grid.SetRow(l, _row);
            Grid.SetRow(control, _row);
            Grid.SetColumn(control, 1);
            if (control is FrameworkElement fe) fe.Margin = new Thickness(0, 3, 0, 3);
            _grid.Children.Add(l);
            _grid.Children.Add(control);
            _row++;
        }

        private void Text(string label, Func<string> get, Action<string> set)
        {
            var tb = new TextBox { Text = get() ?? "" };
            AddRow(label, tb);
            _readers.Add(() => set(tb.Text));
        }

        private void Number(string label, Func<double> get, Action<double> set)
        {
            double init = get();
            var tb = new TextBox { Text = double.IsNaN(init) ? "" : init.ToString(CultureInfo.InvariantCulture) };
            bool allowEmpty = double.IsNaN(init) || label.Contains("vide");
            AddRow(label, tb);
            _readers.Add(() =>
            {
                string s = tb.Text.Replace(',', '.').Trim();
                if (s.Length == 0 && allowEmpty) { set(double.NaN); return; }
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) set(v);
            });
        }

        private CheckBox Check(string label, Func<bool> get, Action<bool> set)
        {
            var cb = new CheckBox { IsChecked = get(), VerticalAlignment = VerticalAlignment.Center };
            AddRow(label, cb);
            _readers.Add(() => set(cb.IsChecked == true));
            return cb;
        }

        private void Combo(string label, List<string> items, Func<string> get, Action<string> set)
        {
            var cb = new ComboBox { IsEditable = true, ItemsSource = items, Text = get() };
            AddRow(label, cb);
            _readers.Add(() => set(cb.Text));
        }

        private void SliderField(string label, double min, double max, Func<double> get, Action<double> set)
        {
            var panel = new DockPanel();
            var value = new TextBlock { Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var sl = new Slider { Minimum = min, Maximum = max, Value = get(), VerticalAlignment = VerticalAlignment.Center };
            value.Text = sl.Value.ToString("0.00", CultureInfo.InvariantCulture);
            sl.ValueChanged += (s, e) => value.Text = sl.Value.ToString("0.00", CultureInfo.InvariantCulture);
            DockPanel.SetDock(value, Dock.Right);
            panel.Children.Add(value);
            panel.Children.Add(sl);
            AddRow(label, panel);
            _readers.Add(() => set(Math.Round(sl.Value, 2)));
        }

        private void Choice(string label, string[] items, Func<string> get, Action<string> set, Action<string> onChange)
        {
            var cb = new ComboBox { IsEditable = false, ItemsSource = items };
            cb.SelectedItem = Array.IndexOf(items, get()) >= 0 ? get() : items[0];
            cb.SelectionChanged += (s, e) => { if (cb.SelectedItem is string v) onChange(v); };
            AddRow(label, cb);
            _readers.Add(() => { if (cb.SelectedItem is string v) set(v); });
        }

        private TextBox ColorField(string label, Func<string> get, Action<string> set)
        {
            var panel = new DockPanel();
            var preview = new Border
            {
                Width = 24,
                Height = 20,
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(6, 0, 0, 0)
            };
            var pick = new Button { Content = "...", Width = 30, Margin = new Thickness(6, 0, 0, 0) };
            var tb = new TextBox { Text = get() ?? "", ToolTip = "#AARRGGBB (AA = transparence, 00 = invisible, FF = opaque)" };

            void Refresh() => preview.Background = OverlayWindow.ParseBrush(tb.Text, Brushes.Transparent);
            tb.TextChanged += (s, e) => Refresh();
            Refresh();

            pick.Click += (s, e) =>
            {
                OverlayWindow.TryParseColor(tb.Text, out var cur);
                using (var dlg = new System.Windows.Forms.ColorDialog
                {
                    FullOpen = true,
                    Color = System.Drawing.Color.FromArgb(cur.R, cur.G, cur.B)
                })
                {
                    if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        byte a = cur.A == 0 ? (byte)0x99 : cur.A;
                        tb.Text = string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", a, dlg.Color.R, dlg.Color.G, dlg.Color.B);
                    }
                }
            };

            DockPanel.SetDock(pick, Dock.Right);
            DockPanel.SetDock(preview, Dock.Right);
            panel.Children.Add(pick);
            panel.Children.Add(preview);
            panel.Children.Add(tb);
            AddRow(label, panel);
            _readers.Add(() => { if (OverlayWindow.TryParseColor(tb.Text, out _)) set(tb.Text.Trim()); });
            return tb;
        }
    }
}
