using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace TchatOverlay
{
    /// <summary>Édition Pro : relie la barre de réponse, le mode interactif et les actions de modération.</summary>
    public class ProController
    {
        private readonly Config _cfg;
        private readonly OverlayWindow _overlay;
        private readonly TwitchIrcClient _irc;
        private readonly ModApi _api;
        private readonly ReplyBarWindow _bar;
        private readonly Action<string> _notify;
        private bool _scopeWarningShown;

        public ProController(Config cfg, OverlayWindow overlay, TwitchIrcClient irc, Action<string> notify)
        {
            _cfg = cfg;
            _overlay = overlay;
            _irc = irc;
            _notify = notify;
            _api = new ModApi(cfg, irc);

            _bar = new ReplyBarWindow(cfg)
            {
                SendHandler = SendAsync,
                ChannelsProvider = () => _cfg.ChannelsToJoin(),
                ModMenuProvider = BuildChannelMenu
            };
            _bar.ApplyConfig();

            _overlay.ReplyHotkeyPressed += () => _bar.OpenForInput(null);
            _overlay.ReplyRequested += Reply;
            _overlay.MessageMenuProvider = BuildMessageMenu;
            _overlay.TopmostReasserted += _bar.ForceTopmost;
            _overlay.EditModeChanged += () =>
            {
                _bar.SetEditMode(_overlay.IsEditMode);
                if (!_overlay.IsEditMode) _cfg.Save();
            };

            _irc.NoticeReceived += (ch, text) =>
                Application.Current.Dispatcher.BeginInvoke(new Action(() => SysMsg(ch, text)));
        }

        public void ApplyConfig() => _bar.ApplyConfig();
        public void StorePosition() { if (_overlay.IsEditMode) _bar.StorePosition(); }
        public void OpenBar() => _bar.OpenForInput(null);

        /// <summary>Avertit une fois si le compte a été connecté avec l'édition Standard (droits insuffisants).</summary>
        public async Task CheckScopesAsync()
        {
            if (!_cfg.IsLoggedIn || _scopeWarningShown) return;
            try
            {
                var creds = await TwitchAuth.EnsureValidAsync(_cfg);
                if (creds != null && creds.MissingScopes)
                {
                    _scopeWarningShown = true;
                    _notify("Réponse et modération : reconnectez le compte Twitch (Paramètres > Connexion) pour accorder les nouveaux droits.");
                }
            }
            catch { }
        }

        private void Reply(ChatMessage m)
        {
            if (_cfg.ExitInteractiveAfterReply) _overlay.SetInteractive(false);
            _bar.OpenForInput(m);
        }

        // ---------- Envoi ----------

        private async Task<string> SendAsync(string channel, string text, ChatMessage replyTo)
        {
            if (!_cfg.IsLoggedIn) return "Connectez un compte Twitch (Paramètres > Connexion).";
            IrcCredentials creds;
            try { creds = await TwitchAuth.EnsureValidAsync(_cfg); }
            catch (Exception ex) { return "Réseau : " + ex.Message; }
            if (creds == null) return "Session Twitch expirée : reconnectez le compte.";
            if (creds.MissingScopes) return "Droits manquants : reconnectez le compte (Paramètres > Connexion).";

            if (text.StartsWith("/")) return await RunCommandAsync(channel, text, creds);

            string err = await _irc.SendAsync(channel, text, replyTo?.Id);
            if (err == null) Echo(channel, (replyTo != null ? "@" + replyTo.DisplayName + " " : "") + text, false, creds);
            return err;
        }

        /// <summary>Twitch ne renvoie pas nos propres messages : affichage local.</summary>
        private void Echo(string channel, string text, bool action, IrcCredentials creds)
        {
            if (!_cfg.ShowOwnMessages) return;
            var st = _irc.GetUserState(channel);
            _overlay.AddMessage(new ChatMessage
            {
                Login = creds.Login,
                DisplayName = !string.IsNullOrEmpty(st?.DisplayName) ? st.DisplayName : creds.Login,
                Color = st?.Color ?? "",
                UserId = creds.UserId,
                Channel = channel,
                Text = text,
                IsAction = action
            });
        }

        private async Task<string> RunCommandAsync(string channel, string line, IrcCredentials creds)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string cmd = parts[0].ToLowerInvariant();
            string Arg(int i) => parts.Length > i ? parts[i] : "";
            string Rest(int from) => parts.Length > from ? string.Join(" ", parts.Skip(from)) : "";

            try
            {
                switch (cmd)
                {
                    case "/me":
                        {
                            string t = Rest(1);
                            if (t.Length == 0) return "Usage : /me texte";
                            string err = await _irc.SendAsync(channel, "\u0001ACTION " + t + "\u0001");
                            if (err == null) Echo(channel, t, true, creds);
                            return err;
                        }
                    case "/ban":
                        if (Arg(1).Length == 0) return "Usage : /ban pseudo [raison]";
                        await _api.BanAsync(channel, await _api.GetUserIdAsync(Arg(1)), null, Rest(2));
                        SysMsg(channel, Arg(1) + " banni.");
                        return null;
                    case "/unban":
                    case "/untimeout":
                        if (Arg(1).Length == 0) return "Usage : " + cmd + " pseudo";
                        await _api.UnbanAsync(channel, await _api.GetUserIdAsync(Arg(1)));
                        SysMsg(channel, Arg(1) + " débanni.");
                        return null;
                    case "/timeout":
                        {
                            if (Arg(1).Length == 0) return "Usage : /timeout pseudo [secondes] [raison]";
                            int sec = int.TryParse(Arg(2), out int d) ? d : 600;
                            string reason = int.TryParse(Arg(2), out _) ? Rest(3) : Rest(2);
                            await _api.BanAsync(channel, await _api.GetUserIdAsync(Arg(1)), sec, reason);
                            SysMsg(channel, Arg(1) + " : timeout " + FormatDuration(sec) + ".");
                            return null;
                        }
                    case "/clear":
                        await _api.ClearChatAsync(channel);
                        return null;
                    case "/slow":
                        {
                            int sec = int.TryParse(Arg(1), out int d) ? d : _cfg.SlowModeSeconds;
                            await SetMode(channel, new Dictionary<string, object> { ["slow_mode"] = true, ["slow_mode_wait_time"] = Math.Clamp(sec, 3, 120) });
                            return null;
                        }
                    case "/slowoff": await SetMode(channel, new Dictionary<string, object> { ["slow_mode"] = false }); return null;
                    case "/emoteonly": await SetMode(channel, new Dictionary<string, object> { ["emote_mode"] = true }); return null;
                    case "/emoteonlyoff": await SetMode(channel, new Dictionary<string, object> { ["emote_mode"] = false }); return null;
                    case "/followers": await SetMode(channel, new Dictionary<string, object> { ["follower_mode"] = true, ["follower_mode_duration"] = 0 }); return null;
                    case "/followersoff": await SetMode(channel, new Dictionary<string, object> { ["follower_mode"] = false }); return null;
                    case "/subscribers": await SetMode(channel, new Dictionary<string, object> { ["subscriber_mode"] = true }); return null;
                    case "/subscribersoff": await SetMode(channel, new Dictionary<string, object> { ["subscriber_mode"] = false }); return null;
                    case "/aide":
                    case "/help":
                        SysMsg(channel, "Commandes : /me, /ban, /unban, /timeout, /untimeout, /clear, /slow [s], /slowoff, /emoteonly(off), /followers(off), /subscribers(off)");
                        return null;
                    default:
                        return "Commande inconnue : " + cmd + " (tapez /aide)";
                }
            }
            catch (Exception ex) { return ex.Message; }
        }

        private async Task SetMode(string channel, Dictionary<string, object> settings)
        {
            await _api.UpdateChatSettingsAsync(channel, settings);
            SysMsg(channel, "Mode du tchat mis à jour.");
        }

        // ---------- Menus ----------

        private ContextMenu BuildMessageMenu(ChatMessage m)
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("Répondre à @" + m.DisplayName, () => Reply(m)));
            menu.Items.Add(Item("Copier le message", () => TryCopy(m.Text)));
            menu.Items.Add(Item("Copier le pseudo", () => TryCopy(m.DisplayName)));

            if (!_cfg.ModeratorOptions) return menu;
            menu.Items.Add(new Separator());

            if (!_irc.IsModerator(m.Channel))
            {
                menu.Items.Add(new MenuItem { Header = "Modération : vous n'êtes pas modérateur de #" + m.Channel, IsEnabled = false });
                return menu;
            }

            var del = Item("Supprimer le message", () => Mod(m.Channel, () => _api.DeleteMessageAsync(m.Channel, m.Id), null));
            del.IsEnabled = !string.IsNullOrEmpty(m.Id);
            menu.Items.Add(del);

            var timeout = new MenuItem { Header = "Timeout @" + m.DisplayName };
            foreach (int d in _cfg.TimeoutDurations)
            {
                int sec = d;
                timeout.Items.Add(Item(FormatDuration(sec), () =>
                    Mod(m.Channel, () => _api.BanAsync(m.Channel, m.UserId, sec), m.DisplayName + " : timeout " + FormatDuration(sec) + ".")));
            }
            menu.Items.Add(timeout);

            menu.Items.Add(Item("Bannir @" + m.DisplayName, () =>
            {
                if (_cfg.ConfirmBan && MessageBox.Show("Bannir définitivement " + m.DisplayName + " de #" + m.Channel + " ?",
                        Edition.Name, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                Mod(m.Channel, () => _api.BanAsync(m.Channel, m.UserId, null), m.DisplayName + " banni.");
            }));
            menu.Items.Add(Item("Débannir / lever le timeout", () =>
                Mod(m.Channel, () => _api.UnbanAsync(m.Channel, m.UserId), m.DisplayName + " débanni.")));
            return menu;
        }

        private ContextMenu BuildChannelMenu(string channel)
        {
            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = "#" + channel, IsEnabled = false });
            menu.Items.Add(new Separator());

            if (!_irc.IsModerator(channel))
            {
                menu.Items.Add(new MenuItem { Header = "Vous n'êtes pas modérateur de cette chaîne", IsEnabled = false });
                return menu;
            }

            var slow = new MenuItem { Header = "Mode lent (" + _cfg.SlowModeSeconds + " s)" };
            var emote = new MenuItem { Header = "Emotes uniquement" };
            var follow = new MenuItem { Header = "Followers uniquement" };
            var subs = new MenuItem { Header = "Abonnés uniquement" };

            slow.Click += (s, e) => Mod(channel, () => _api.UpdateChatSettingsAsync(channel, slow.IsChecked
                ? new Dictionary<string, object> { ["slow_mode"] = false }
                : new Dictionary<string, object> { ["slow_mode"] = true, ["slow_mode_wait_time"] = _cfg.SlowModeSeconds }),
                slow.IsChecked ? "Mode lent désactivé." : "Mode lent activé.");
            emote.Click += (s, e) => Mod(channel, () => _api.UpdateChatSettingsAsync(channel,
                new Dictionary<string, object> { ["emote_mode"] = !emote.IsChecked }),
                emote.IsChecked ? "Emotes uniquement désactivé." : "Emotes uniquement activé.");
            follow.Click += (s, e) => Mod(channel, () => _api.UpdateChatSettingsAsync(channel, follow.IsChecked
                ? new Dictionary<string, object> { ["follower_mode"] = false }
                : new Dictionary<string, object> { ["follower_mode"] = true, ["follower_mode_duration"] = 0 }),
                follow.IsChecked ? "Followers uniquement désactivé." : "Followers uniquement activé.");
            subs.Click += (s, e) => Mod(channel, () => _api.UpdateChatSettingsAsync(channel,
                new Dictionary<string, object> { ["subscriber_mode"] = !subs.IsChecked }),
                subs.IsChecked ? "Abonnés uniquement désactivé." : "Abonnés uniquement activé.");

            menu.Items.Add(slow);
            menu.Items.Add(emote);
            menu.Items.Add(follow);
            menu.Items.Add(subs);
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Vider le tchat", () =>
            {
                if (MessageBox.Show("Vider tout le tchat de #" + channel + " ?", Edition.Name,
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                Mod(channel, () => _api.ClearChatAsync(channel), null);
            }));

            // État actuel des modes (coches)
            menu.Opened += async (s, e) =>
            {
                try
                {
                    var st = await _api.GetChatSettingsAsync(channel);
                    slow.IsChecked = st.Slow;
                    emote.IsChecked = st.EmoteOnly;
                    follow.IsChecked = st.FollowersOnly;
                    subs.IsChecked = st.SubscribersOnly;
                }
                catch { }
            };
            return menu;
        }

        // ---------- Utilitaires ----------

        private async void Mod(string channel, Func<Task> action, string success)
        {
            try
            {
                await action();
                if (success != null) SysMsg(channel, success);
            }
            catch (Exception ex)
            {
                SysMsg(channel, "Modération : " + ex.Message);
            }
        }

        private void SysMsg(string channel, string text)
        {
            _overlay.AddMessage(new ChatMessage
            {
                IsSystem = true,
                Channel = channel ?? "",
                Text = (_cfg.MultiChat && !string.IsNullOrEmpty(channel) ? "[#" + channel + "] " : "") + text
            });
        }

        private static MenuItem Item(string header, Action click)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (s, e) => click();
            return mi;
        }

        private static void TryCopy(string text)
        {
            try { Clipboard.SetText(text ?? ""); } catch { }
        }

        public static string FormatDuration(int sec)
        {
            if (sec < 60) return sec + " s";
            if (sec < 3600) return (sec / 60) + " min";
            if (sec < 86400) return (sec / 3600) + " h";
            return (sec / 86400) + " j";
        }
    }
}
