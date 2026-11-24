using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TchatOverlay
{
    public class ChatSettingsState
    {
        public bool Slow, EmoteOnly, FollowersOnly, SubscribersOnly;
        public int SlowSeconds;
    }

    /// <summary>Actions de modération via l'API Helix (édition Pro). Lève une exception avec le message Twitch en cas d'échec.</summary>
    public class ModApi
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private readonly Config _cfg;
        private readonly TwitchIrcClient _irc;

        public ModApi(Config cfg, TwitchIrcClient irc)
        {
            _cfg = cfg;
            _irc = irc;
        }

        // ---------- Utilisateurs ----------

        public async Task<string> GetUserIdAsync(string login)
        {
            login = TwitchIrcClient.NormalizeChannel(login).TrimStart('@');
            using (var doc = await CallAsync(HttpMethod.Get, "users?login=" + Uri.EscapeDataString(login), null))
            {
                foreach (var u in doc.RootElement.GetProperty("data").EnumerateArray())
                    return u.GetProperty("id").GetString();
            }
            throw new Exception("Utilisateur introuvable : " + login);
        }

        private async Task<string> BroadcasterIdAsync(string channel)
        {
            return _irc.GetRoomId(channel) ?? await GetUserIdAsync(channel);
        }

        // ---------- Bannissement / timeout ----------

        /// <param name="durationSeconds">null = bannissement définitif</param>
        public async Task BanAsync(string channel, string userId, int? durationSeconds, string reason = "")
        {
            var data = new Dictionary<string, object> { ["user_id"] = userId };
            if (durationSeconds.HasValue) data["duration"] = durationSeconds.Value;
            if (!string.IsNullOrWhiteSpace(reason)) data["reason"] = reason;
            var (bid, mid) = await IdsAsync(channel);
            using (await CallAsync(HttpMethod.Post, "moderation/bans?broadcaster_id=" + bid + "&moderator_id=" + mid,
                                   new Dictionary<string, object> { ["data"] = data })) { }
        }

        public async Task UnbanAsync(string channel, string userId)
        {
            var (bid, mid) = await IdsAsync(channel);
            using (await CallAsync(HttpMethod.Delete, "moderation/bans?broadcaster_id=" + bid + "&moderator_id=" + mid + "&user_id=" + userId, null)) { }
        }

        // ---------- Messages ----------

        public async Task DeleteMessageAsync(string channel, string messageId)
        {
            var (bid, mid) = await IdsAsync(channel);
            using (await CallAsync(HttpMethod.Delete, "moderation/chat?broadcaster_id=" + bid + "&moderator_id=" + mid + "&message_id=" + messageId, null)) { }
        }

        public async Task ClearChatAsync(string channel)
        {
            var (bid, mid) = await IdsAsync(channel);
            using (await CallAsync(HttpMethod.Delete, "moderation/chat?broadcaster_id=" + bid + "&moderator_id=" + mid, null)) { }
        }

        // ---------- Modes du tchat ----------

        public async Task<ChatSettingsState> GetChatSettingsAsync(string channel)
        {
            var (bid, mid) = await IdsAsync(channel);
            using (var doc = await CallAsync(HttpMethod.Get, "chat/settings?broadcaster_id=" + bid + "&moderator_id=" + mid, null))
            {
                foreach (var d in doc.RootElement.GetProperty("data").EnumerateArray())
                {
                    return new ChatSettingsState
                    {
                        Slow = Bool(d, "slow_mode"),
                        SlowSeconds = d.TryGetProperty("slow_mode_wait_time", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 0,
                        EmoteOnly = Bool(d, "emote_mode"),
                        FollowersOnly = Bool(d, "follower_mode"),
                        SubscribersOnly = Bool(d, "subscriber_mode")
                    };
                }
            }
            return new ChatSettingsState();
        }

        /// <summary>Exemple : { "slow_mode": true, "slow_mode_wait_time": 30 }</summary>
        public async Task UpdateChatSettingsAsync(string channel, Dictionary<string, object> settings)
        {
            var (bid, mid) = await IdsAsync(channel);
            using (await CallAsync(HttpMethod.Patch, "chat/settings?broadcaster_id=" + bid + "&moderator_id=" + mid, settings)) { }
        }

        // ---------- HTTP ----------

        private async Task<(string broadcaster, string moderator)> IdsAsync(string channel)
        {
            var creds = await TwitchAuth.EnsureValidAsync(_cfg);
            if (creds == null) throw new Exception("Compte Twitch non connecté.");
            return (await BroadcasterIdAsync(channel), creds.UserId);
        }

        private async Task<JsonDocument> CallAsync(HttpMethod method, string pathAndQuery, object body)
        {
            var creds = await TwitchAuth.EnsureValidAsync(_cfg);
            if (creds == null) throw new Exception("Compte Twitch non connecté.");
            if (creds.MissingScopes) throw new Exception("Droits manquants : reconnectez le compte (Paramètres > Connexion).");

            using (var req = new HttpRequestMessage(method, "https://api.twitch.tv/helix/" + pathAndQuery))
            {
                req.Headers.Add("Client-Id", TwitchAuth.ClientId(_cfg));
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.Token);
                if (body != null)
                    req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

                using (var resp = await Http.SendAsync(req))
                {
                    string text = await resp.Content.ReadAsStringAsync();
                    if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) TwitchAuth.InvalidateCache();
                    if (!resp.IsSuccessStatusCode)
                    {
                        string msg = "";
                        try
                        {
                            using (var err = JsonDocument.Parse(text))
                                if (err.RootElement.TryGetProperty("message", out var m)) msg = m.GetString();
                        }
                        catch { }
                        throw new Exception(string.IsNullOrEmpty(msg) ? "Erreur Twitch " + (int)resp.StatusCode : msg);
                    }
                    return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
                }
            }
        }

        private static bool Bool(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }
}
