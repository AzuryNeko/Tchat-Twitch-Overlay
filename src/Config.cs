using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TchatOverlay
{
    public class Config
    {
        // Thèmes
        public string UiTheme { get; set; } = "Système";      // fenêtre Paramètres : Système / Sombre / Clair
        public string OverlayTheme { get; set; } = "Sombre";  // bulles du tchat : Sombre / Clair

        // Connexion au compte Twitch (jetons chiffrés DPAPI, lisibles uniquement par cette session Windows)
        public string ClientId { get; set; } = "";
        public string AccountLogin { get; set; } = "";
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        [JsonIgnore] public bool IsLoggedIn => !string.IsNullOrEmpty(AccessToken);

        // Chaîne affichée (vide = chaîne du compte connecté)
        public string Channel { get; set; } = "";

        // Multi-tchat
        public bool MultiChat { get; set; } = false;
        public List<string> ExtraChannels { get; set; } = new List<string>();
        public bool ShowChannelName { get; set; } = true;

        /// <summary>Liste des chaînes à rejoindre (principale + multi-tchat), normalisées, sans doublon.</summary>
        public List<string> ChannelsToJoin()
        {
            var list = new List<string>();
            void Add(string c)
            {
                string n = TwitchIrcClient.NormalizeChannel(c);
                if (n.Length > 0 && !list.Contains(n)) list.Add(n);
            }
            Add(string.IsNullOrWhiteSpace(Channel) ? AccountLogin : Channel);
            if (MultiChat && ExtraChannels != null)
                foreach (var c in ExtraChannels) Add(c);
            return list;
        }

        // Position / taille (null = position par défaut sur l'écran principal)
        public double? Left { get; set; }
        public double? Top { get; set; }
        public double Width { get; set; } = 420;
        public double Height { get; set; } = 480;

        // Apparence
        public string FontFamily { get; set; } = "Segoe UI";
        public double FontSize { get; set; } = 16;
        public string TextColor { get; set; } = "#FFFFFFFF";
        public string MessageBackground { get; set; } = "#99000000";
        public string PanelBackground { get; set; } = "#00000000";
        public double CornerRadius { get; set; } = 6;
        public double WindowOpacity { get; set; } = 1.0;
        public bool TextShadow { get; set; } = true;
        public bool UseUserColors { get; set; } = true;
        public bool ReadableNames { get; set; } = true;
        public string DefaultNameColor { get; set; } = "#FF9146FF";
        public bool ShowEmotes { get; set; } = true;
        public bool AnimatedEmotes { get; set; } = true;
        public bool ShowAvatars { get; set; } = true;
        public bool RoundAvatars { get; set; } = true;
        public bool NewestOnTop { get; set; } = false;

        // Défilement horizontal (bandeau type ticker)
        public bool HorizontalScroll { get; set; } = false;
        public double ScrollSpeed { get; set; } = 120;   // pixels / seconde
        public double ScrollGap { get; set; } = 60;      // espace entre deux messages

        // Comportement
        public int MessageLifetimeSeconds { get; set; } = 30;   // 0 = permanent
        public int MaxMessages { get; set; } = 20;
        public bool HideCommands { get; set; } = false;         // messages commençant par "!"
        public List<string> IgnoredUsers { get; set; } = new List<string>();
        public bool ShowSystemMessages { get; set; } = true;

        // Raccourcis globaux
        public string HotkeyEditMode { get; set; } = "Ctrl+Shift+F10";
        public string HotkeyToggleVisible { get; set; } = "Ctrl+Shift+F11";

        // ----- Édition Pro : barre de réponse -----
        public string HotkeyReply { get; set; } = "Ctrl+Shift+F9";
        public string HotkeyInteractive { get; set; } = "Ctrl+Shift+F8";
        public bool ReplyBarAlwaysVisible { get; set; } = false;
        public double? ReplyBarLeft { get; set; }
        public double? ReplyBarTop { get; set; }
        public double ReplyBarWidth { get; set; } = 420;
        public double ReplyBarFontSize { get; set; } = 15;
        public string ReplyBarBackground { get; set; } = "#E6181818";
        public string ReplyBarTextColor { get; set; } = "#FFFFFFFF";
        public double ReplyBarOpacity { get; set; } = 1.0;
        public bool ReturnFocusToGame { get; set; } = true;
        public bool ShowOwnMessages { get; set; } = true;
        public bool ExitInteractiveAfterReply { get; set; } = true;

        // ----- Édition Pro : modération -----
        public bool ModeratorOptions { get; set; } = true;
        public List<int> TimeoutDurations { get; set; } = new List<int> { 60, 600, 3600 };
        public bool ConfirmBan { get; set; } = true;
        public int SlowModeSeconds { get; set; } = 30;

        // Interface des paramètres : sections repliées
        public List<string> CollapsedSections { get; set; } = new List<string>();

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        /// <summary>%AppData%\Tchat Overlay : config.json, avatars, erreurs.log (hors du dossier du .exe).</summary>
        public static string DataDir
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string d = Path.Combine(appData, "Tchat Overlay");
                if (!_dirChecked)
                {
                    _dirChecked = true;
                    // Ancien dossier sans espace : déplacé une seule fois
                    string old = Path.Combine(appData, "TchatOverlay");
                    try { if (Directory.Exists(old) && !Directory.Exists(d)) Directory.Move(old, d); } catch { }
                }
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        private static bool _dirChecked;

        public static string FilePath => Path.Combine(DataDir, "config.json");

        /// <summary>Déplace l'ancien config.json situé à côté du .exe (anciennes versions).</summary>
        private static void MigrateOldFile()
        {
            try
            {
                string old = Path.Combine(AppContext.BaseDirectory, "config.json");
                if (!File.Exists(old)) return;
                if (!File.Exists(FilePath)) File.Move(old, FilePath);
                else File.Delete(old);
            }
            catch { }
        }

        public static Config Load()
        {
            MigrateOldFile();
            try
            {
                if (File.Exists(FilePath))
                {
                    var c = JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), JsonOpts);
                    if (c != null)
                    {
                        c.Sanitize();
                        return c;
                    }
                }
            }
            catch
            {
                try { File.Copy(FilePath, FilePath + ".invalide", true); } catch { }
            }
            var def = new Config();
            def.Save();
            return def;
        }

        public void Save()
        {
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts)); }
            catch { }
        }

        public Config Clone()
        {
            return JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(this, JsonOpts), JsonOpts);
        }

        public void CopyFrom(Config o)
        {
            foreach (var p in typeof(Config).GetProperties())
                if (p.CanWrite) p.SetValue(this, p.GetValue(o));
        }

        public void Sanitize()
        {
            if (Width < 150) Width = 150;
            if (Height < 80) Height = 80;
            if (FontSize < 6) FontSize = 6;
            if (FontSize > 72) FontSize = 72;
            if (WindowOpacity < 0.1) WindowOpacity = 0.1;
            if (WindowOpacity > 1) WindowOpacity = 1;
            if (MaxMessages < 1) MaxMessages = 1;
            if (MaxMessages > 200) MaxMessages = 200;
            if (MessageLifetimeSeconds < 0) MessageLifetimeSeconds = 0;
            if (CornerRadius < 0) CornerRadius = 0;
            if (ScrollSpeed < 10) ScrollSpeed = 10;
            if (ScrollSpeed > 2000) ScrollSpeed = 2000;
            if (ScrollGap < 0) ScrollGap = 0;
            if (ScrollGap > 2000) ScrollGap = 2000;
            if (IgnoredUsers == null) IgnoredUsers = new List<string>();
            if (CollapsedSections == null) CollapsedSections = new List<string>();
            if (ExtraChannels == null) ExtraChannels = new List<string>();
            if (TimeoutDurations == null || TimeoutDurations.Count == 0) TimeoutDurations = new List<int> { 60, 600, 3600 };
            TimeoutDurations = TimeoutDurations.FindAll(d => d >= 1 && d <= 1209600);
            if (TimeoutDurations.Count == 0) TimeoutDurations = new List<int> { 600 };
            if (ReplyBarWidth < 200) ReplyBarWidth = 200;
            if (ReplyBarFontSize < 8) ReplyBarFontSize = 8;
            if (ReplyBarFontSize > 40) ReplyBarFontSize = 40;
            if (ReplyBarOpacity < 0.2) ReplyBarOpacity = 0.2;
            if (ReplyBarOpacity > 1) ReplyBarOpacity = 1;
            if (SlowModeSeconds < 3) SlowModeSeconds = 3;
            if (SlowModeSeconds > 120) SlowModeSeconds = 120;
            if (Channel == null) Channel = "";
            if (ClientId == null) ClientId = "";
            if (AccountLogin == null) AccountLogin = "";
            if (AccessToken == null) AccessToken = "";
            if (RefreshToken == null) RefreshToken = "";
            if (UiTheme != "Sombre" && UiTheme != "Clair") UiTheme = "Système";
            if (OverlayTheme != "Clair") OverlayTheme = "Sombre";
        }
    }
}
