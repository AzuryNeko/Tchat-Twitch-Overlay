using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TchatOverlay
{
    public class DeviceCodeInfo
    {
        public string DeviceCode;
        public string UserCode;
        public string VerificationUri;
        public int Interval;
        public int ExpiresIn;
    }

    public class TokenPair
    {
        public string AccessToken;
        public string RefreshToken;
    }

    public class IrcCredentials
    {
        public string Login;
        public string UserId;
        public string Token;
        public bool MissingScopes;   // jeton sans les droits requis par cette édition
    }

    public class TokenInfo
    {
        public string Login;
        public string UserId;
        public List<string> Scopes = new List<string>();
    }

    /// <summary>Connexion au compte Twitch par « Device Code Grant Flow » (client public, sans secret).</summary>
    public static class TwitchAuth
    {
        /// <summary>Client-ID intégré au .exe (facultatif). Vide = utiliser celui de config.json.</summary>
        public const string BuiltInClientId = "";

        public const string Scopes = Edition.Scopes;

        public static bool HasAllScopes(TokenInfo info)
        {
            foreach (var s in Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (!info.Scopes.Contains(s)) return false;
            return true;
        }

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly SemaphoreSlim RefreshLock = new SemaphoreSlim(1, 1);
        private static IrcCredentials _cached;
        private static DateTime _cachedAt;

        /// <summary>Force une nouvelle validation au prochain appel (ex. après un 401).</summary>
        public static void InvalidateCache() => _cached = null;

        public static string ClientId(Config cfg) =>
            !string.IsNullOrWhiteSpace(cfg.ClientId) ? cfg.ClientId.Trim() : BuiltInClientId;

        // ---------- Device Code Flow ----------

        public static async Task<DeviceCodeInfo> StartDeviceFlowAsync(string clientId)
        {
            using var json = await PostFormAsync("https://id.twitch.tv/oauth2/device", new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scopes"] = Scopes
            });
            var r = json.RootElement;
            return new DeviceCodeInfo
            {
                DeviceCode = Str(r, "device_code"),
                UserCode = Str(r, "user_code"),
                VerificationUri = Str(r, "verification_uri"),
                Interval = Int(r, "interval", 5),
                ExpiresIn = Int(r, "expires_in", 1800)
            };
        }

        public static async Task<TokenPair> PollDeviceTokenAsync(string clientId, DeviceCodeInfo info, CancellationToken ct)
        {
            int interval = Math.Max(1, info.Interval);
            var deadline = DateTime.UtcNow.AddSeconds(info.ExpiresIn);

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(interval * 1000, ct);
                var resp = await Http.PostAsync("https://id.twitch.tv/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["scopes"] = Scopes,
                    ["device_code"] = info.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                }), ct);
                string body = await resp.Content.ReadAsStringAsync(ct);

                if (resp.IsSuccessStatusCode)
                {
                    using (var doc = JsonDocument.Parse(body))
                        return new TokenPair { AccessToken = Str(doc.RootElement, "access_token"), RefreshToken = Str(doc.RootElement, "refresh_token") };
                }

                string msg = ErrorMessage(body);
                if (msg.Contains("authorization_pending")) continue;
                if (msg.Contains("slow_down")) { interval += 5; continue; }
                throw new Exception(msg.Length > 0 ? msg : "Échec de l'autorisation (" + (int)resp.StatusCode + ")");
            }
            throw new Exception("Code expiré, recommencez la connexion.");
        }

        // ---------- Validation / rafraîchissement ----------

        /// <summary>Renvoie les infos du jeton, ou null si le jeton est invalide.</summary>
        public static async Task<TokenInfo> ValidateAsync(string accessToken)
        {
            if (string.IsNullOrEmpty(accessToken)) return null;
            using (var req = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate"))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("OAuth", accessToken);
                using (var resp = await Http.SendAsync(req))
                {
                    if (resp.StatusCode == HttpStatusCode.Unauthorized) return null;
                    resp.EnsureSuccessStatusCode();
                    using (var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()))
                    {
                        var r = doc.RootElement;
                        var info = new TokenInfo { Login = Str(r, "login"), UserId = Str(r, "user_id") };
                        if (r.TryGetProperty("scopes", out var sc) && sc.ValueKind == JsonValueKind.Array)
                            foreach (var x in sc.EnumerateArray())
                                if (x.ValueKind == JsonValueKind.String) info.Scopes.Add(x.GetString());
                        return info;
                    }
                }
            }
        }

        public static async Task<TokenPair> RefreshAsync(string clientId, string refreshToken)
        {
            var resp = await Http.PostAsync("https://id.twitch.tv/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            }));
            if (!resp.IsSuccessStatusCode) return null;
            using (var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()))
                return new TokenPair { AccessToken = Str(doc.RootElement, "access_token"), RefreshToken = Str(doc.RootElement, "refresh_token") };
        }

        /// <summary>
        /// Valide le jeton enregistré et le rafraîchit si besoin.
        /// Renvoie null si l'utilisateur doit se reconnecter. Lève une exception en cas d'erreur réseau.
        /// </summary>
        public static async Task<IrcCredentials> EnsureValidAsync(Config cfg)
        {
            await RefreshLock.WaitAsync();
            try
            {
                // Jeton validé il y a moins de 10 minutes : pas de nouvel appel réseau
                var c = _cached;
                if (c != null && (DateTime.UtcNow - _cachedAt).TotalMinutes < 10 &&
                    c.Token == Unprotect(cfg.AccessToken))
                    return c;

                string token = Unprotect(cfg.AccessToken);
                var info = await ValidateAsync(token);
                if (info == null)
                {
                    string refresh = Unprotect(cfg.RefreshToken);
                    if (string.IsNullOrEmpty(refresh)) return null;
                    var pair = await RefreshAsync(ClientId(cfg), refresh);
                    if (pair == null || string.IsNullOrEmpty(pair.AccessToken)) return null;
                    token = pair.AccessToken;
                    info = await ValidateAsync(token);
                    if (info == null) return null;
                    cfg.AccessToken = Protect(pair.AccessToken);
                    cfg.RefreshToken = Protect(pair.RefreshToken);
                }
                cfg.AccountLogin = info.Login;
                cfg.Save();
                _cached = new IrcCredentials
                {
                    Login = info.Login,
                    UserId = info.UserId,
                    Token = token,
                    MissingScopes = !HasAllScopes(info)
                };
                _cachedAt = DateTime.UtcNow;
                return _cached;
            }
            finally { RefreshLock.Release(); }
        }

        public static async Task RevokeAsync(string clientId, string accessToken)
        {
            if (string.IsNullOrEmpty(accessToken)) return;
            try
            {
                await Http.PostAsync("https://id.twitch.tv/oauth2/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["token"] = accessToken
                }));
            }
            catch { }
        }

        // ---------- Utilitaires HTTP / JSON ----------

        private static async Task<JsonDocument> PostFormAsync(string url, Dictionary<string, string> form)
        {
            var resp = await Http.PostAsync(url, new FormUrlEncodedContent(form));
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                string msg = ErrorMessage(body);
                throw new Exception(msg.Length > 0 ? msg : "Erreur Twitch (" + (int)resp.StatusCode + ")");
            }
            return JsonDocument.Parse(body);
        }

        private static string ErrorMessage(string body)
        {
            try
            {
                using (var doc = JsonDocument.Parse(body))
                    return Str(doc.RootElement, "message");
            }
            catch { return ""; }
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

        private static int Int(JsonElement e, string name, int def) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : def;

        // ---------- Chiffrement local des jetons (DPAPI, lié au compte Windows) ----------

        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string szDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            byte[] r = Transform(Encoding.UTF8.GetBytes(plain), true);
            return r == null ? "" : "dpapi:" + Convert.ToBase64String(r);
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored) || !stored.StartsWith("dpapi:")) return "";
            try
            {
                byte[] r = Transform(Convert.FromBase64String(stored.Substring(6)), false);
                return r == null ? "" : Encoding.UTF8.GetString(r);
            }
            catch { return ""; }
        }

        private static byte[] Transform(byte[] data, bool protect)
        {
            var input = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
            var output = new DATA_BLOB();
            try
            {
                Marshal.Copy(data, 0, input.pbData, data.Length);
                bool ok = protect
                    ? CryptProtectData(ref input, "TchatOverlay", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output);
                if (!ok) return null;
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(input.pbData);
                if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
            }
        }
    }
}
