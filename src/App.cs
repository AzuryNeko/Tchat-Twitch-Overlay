using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace TchatOverlay
{
    public class App : Application
    {
        private Config _cfg;
        private OverlayWindow _overlay;
        private TwitchIrcClient _irc;
        private WinForms.NotifyIcon _tray;
        private WinForms.ToolStripMenuItem _editItem;
        private SettingsWindow _settings;
        private string _currentChannel = "";
        private DispatcherTimer _validateTimer;
        private ProController _pro;   // édition Pro uniquement

        [STAThread]
        public static void Main()
        {
            using (var mutex = new Mutex(true, "TchatOverlay_AzuryNeko_Instance", out bool created))
            {
                if (!created)
                {
                    MessageBox.Show("Tchat Overlay (Standard ou Pro) est déjà lancé (icône dans la zone de notification).",
                        Edition.Name, MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Run();
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (s, ex) =>
            {
                Log(ex.Exception.ToString());
                ex.Handled = true;
            };

            _cfg = Config.Load();
            SetupTray();

            _overlay = new OverlayWindow(_cfg);
            _overlay.EditModeChanged += () =>
            {
                _editItem.Checked = _overlay.IsEditMode;
                if (!_overlay.IsEditMode) _cfg.Save();
            };
            _overlay.HotkeyError += msg => Notify(msg, WinForms.ToolTipIcon.Warning);
            _overlay.Show();

            AvatarLoader.Credentials = () => _cfg.IsLoggedIn ? TwitchAuth.EnsureValidAsync(_cfg) : Task.FromResult<IrcCredentials>(null);
            AvatarLoader.ClientId = () => TwitchAuth.ClientId(_cfg);

            _irc = new TwitchIrcClient();
            _irc.MessageReceived += m => Dispatcher.BeginInvoke(new Action(() => _overlay.AddMessage(m)));
            _irc.MessageDeleted += id => Dispatcher.BeginInvoke(new Action(() => _overlay.DeleteMessage(id)));
            _irc.ChatCleared += (chan, login) => Dispatcher.BeginInvoke(new Action(() => _overlay.ClearChat(chan, login)));
            _irc.StatusChanged += st => Dispatcher.BeginInvoke(new Action(() => OnStatus(st)));
            _irc.AuthRequired += () => Dispatcher.BeginInvoke(new Action(OnAuthRequired));

            if (Edition.IsPro)
                _pro = new ProController(_cfg, _overlay, _irc, msg => Notify(msg, WinForms.ToolTipIcon.Warning));

            // Twitch impose une validation régulière du jeton (au moins toutes les heures)
            _validateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(50) };
            _validateTimer.Tick += async (s, ev) => await PeriodicValidate();
            _validateTimer.Start();

            if (_cfg.ChannelsToJoin().Count == 0)
            {
                Notify("Connectez votre compte Twitch ou indiquez une chaîne dans les paramètres.", WinForms.ToolTipIcon.Info);
                OpenSettings();
            }
            else
            {
                Connect();
            }
        }

        private void Connect()
        {
            var channels = _cfg.ChannelsToJoin();
            _currentChannel = string.Join(",", channels);
            if (channels.Count == 0)
            {
                _irc.Stop();
                OnStatus("Aucune chaîne — Paramètres > Connexion");
                return;
            }
            // Compte connecté : connexion authentifiée ; sinon lecture anonyme
            if (_cfg.IsLoggedIn) _irc.Start(channels, () => TwitchAuth.EnsureValidAsync(_cfg));
            else _irc.Start(channels);
            if (_pro != null) _ = _pro.CheckScopesAsync();
        }

        private void OnAccountChanged()
        {
            _overlay.ClearAll();
            Connect();
        }

        private void OnAuthRequired()
        {
            Notify("Session Twitch expirée. Reconnectez votre compte.", WinForms.ToolTipIcon.Warning);
            OpenSettings();
        }

        private async Task PeriodicValidate()
        {
            if (!_cfg.IsLoggedIn) return;
            try
            {
                var creds = await Task.Run(() => TwitchAuth.EnsureValidAsync(_cfg));
                if (creds == null)
                {
                    _irc.Stop();
                    OnAuthRequired();
                }
            }
            catch { /* réseau indisponible : nouvel essai au prochain passage */ }
        }

        private void OnStatus(string status)
        {
            _overlay.SetStatus(status);
            if (_tray != null) _tray.Text = Truncate(Edition.Name + " — " + status, 63);
            if (status.StartsWith("Connecté") || status.StartsWith("Déconnecté"))
                _overlay.AddMessage(new ChatMessage { IsSystem = true, Text = status });
        }

        private void SetupTray()
        {
            System.Drawing.Icon icon;
            try
            {
                var res = GetResourceStream(new Uri("pack://application:,,,/Assets/TchatOverlay.ico"));
                icon = new System.Drawing.Icon(res.Stream, WinForms.SystemInformation.SmallIconSize);
            }
            catch
            {
                try { icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath) ?? System.Drawing.SystemIcons.Application; }
                catch { icon = System.Drawing.SystemIcons.Application; }
            }

            var menu = new WinForms.ContextMenuStrip();
            _editItem = new WinForms.ToolStripMenuItem("Mode édition (" + _cfg.HotkeyEditMode + ")");
            _editItem.Click += (s, e) => _overlay.SetEditMode(!_overlay.IsEditMode);
            menu.Items.Add(_editItem);
            menu.Items.Add("Afficher / masquer (" + _cfg.HotkeyToggleVisible + ")", null, (s, e) => _overlay.ToggleVisible());
            if (Edition.IsPro)
            {
                menu.Items.Add("Écrire dans le tchat (" + _cfg.HotkeyReply + ")", null, (s, e) => _pro?.OpenBar());
                menu.Items.Add("Mode interactif (" + _cfg.HotkeyInteractive + ")", null, (s, e) => _overlay.SetInteractive(!_overlay.IsInteractive));
            }
            menu.Items.Add("Paramètres...", null, (s, e) => OpenSettings());
            menu.Items.Add("Vider le tchat", null, (s, e) => _overlay.ClearAll());
            menu.Items.Add("Reconnecter", null, (s, e) => Connect());
            menu.Items.Add("Ouvrir config.json", null, (s, e) => OpenConfigFile());
            menu.Items.Add("Ouvrir le dossier de configuration", null, (s, e) => OpenFolder(Config.DataDir));
            menu.Items.Add("Ouvrir le dossier des avatars", null, (s, e) => OpenFolder(AvatarLoader.Folder));
            menu.Items.Add("Recharger config.json", null, (s, e) => ReloadConfig());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Quitter", null, (s, e) => Quit());

            _tray = new WinForms.NotifyIcon
            {
                Icon = icon,
                Text = Edition.Name,
                ContextMenuStrip = menu,
                Visible = true
            };
            _tray.DoubleClick += (s, e) => OpenSettings();
        }

        private void RefreshMenuLabels()
        {
            _editItem.Text = "Mode édition (" + _cfg.HotkeyEditMode + ")";
            _tray.ContextMenuStrip.Items[1].Text = "Afficher / masquer (" + _cfg.HotkeyToggleVisible + ")";
        }

        private void OpenSettings()
        {
            if (_settings != null)
            {
                _settings.Activate();
                return;
            }
            _overlay.StorePosition();
            _pro?.StorePosition();
            _settings = new SettingsWindow(_cfg, ApplySettings, OnAccountChanged);
            _settings.Closed += (s, e) => _settings = null;
            _settings.Show();
            _settings.Activate();
        }

        private void ApplySettings()
        {
            _overlay.ApplyConfig();
            _pro?.ApplyConfig();
            RefreshMenuLabels();
            if (string.Join(",", _cfg.ChannelsToJoin()) != _currentChannel)
                OnAccountChanged();
        }

        private void ReloadConfig()
        {
            var fresh = Config.Load();
            _cfg.CopyFrom(fresh);
            ApplySettings();
        }

        private void OpenConfigFile()
        {
            _overlay.StorePosition();
            _pro?.StorePosition();
            _cfg.Save();
            try { Process.Start(new ProcessStartInfo(Config.FilePath) { UseShellExecute = true }); }
            catch { Process.Start("notepad.exe", "\"" + Config.FilePath + "\""); }
        }

        public static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
            catch { }
        }

        private void Notify(string text, WinForms.ToolTipIcon icon)
        {
            if (_tray == null) return;
            _tray.BalloonTipTitle = Edition.Name;
            _tray.BalloonTipText = text;
            _tray.BalloonTipIcon = icon;
            _tray.ShowBalloonTip(4000);
        }

        private void Quit()
        {
            try
            {
                if (_overlay.IsEditMode) _overlay.SetEditMode(false);
                _overlay.StorePosition();
            _pro?.StorePosition();
                _cfg.Save();
                _irc.Dispose();
                _tray.Visible = false;
                _tray.Dispose();
            }
            catch { }
            Shutdown();
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

        private static void Log(string text)
        {
            try
            {
                File.AppendAllText(Path.Combine(Config.DataDir, "erreurs.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + Environment.NewLine);
            }
            catch { }
        }
    }
}
