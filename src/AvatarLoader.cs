using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TchatOverlay
{
    /// <summary>Photos de profil Twitch (API Helix /users), requêtes groupées par 100 et cache mémoire.</summary>
    public static class AvatarLoader
    {
        public static Func<Task<IrcCredentials>> Credentials;   // fourni par App
        public static Func<string> ClientId;

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly Dictionary<string, BitmapSource> Cache = new Dictionary<string, BitmapSource>();
        private static readonly Dictionary<string, List<Action<BitmapSource>>> Pending = new Dictionary<string, List<Action<BitmapSource>>>();
        private static readonly HashSet<string> Failed = new HashSet<string>();
        private static readonly List<string> Queue = new List<string>();
        private static DispatcherTimer _flush;

        /// <summary>À appeler depuis le thread UI ; le callback est exécuté sur le thread UI.</summary>
        private static void SaveToDisk(string id, string login, string url, byte[] bytes)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                foreach (var old in Directory.GetFiles(Folder, "*_" + id + ".*")) File.Delete(old);
                string ext = Path.GetExtension(new Uri(url).AbsolutePath);
                if (string.IsNullOrEmpty(ext)) ext = ".png";
                string safeLogin = string.Concat((login ?? "user").Split(Path.GetInvalidFileNameChars()));
                File.WriteAllBytes(Path.Combine(Folder, safeLogin + "_" + id + ext), bytes);
            }
            catch { }
        }

        public static void Get(string userId, Action<BitmapSource> onReady)
        {
            if (string.IsNullOrEmpty(userId) || Credentials == null || Failed.Contains(userId)) return;
            if (Cache.TryGetValue(userId, out var bmp)) { onReady(bmp); return; }
            if (Pending.TryGetValue(userId, out var list)) { list.Add(onReady); return; }

            Pending[userId] = new List<Action<BitmapSource>> { onReady };
            Queue.Add(userId);

            if (_flush == null)
            {
                _flush = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                _flush.Tick += (s, e) => { _flush.Stop(); Flush(); };
            }
            if (!_flush.IsEnabled) _flush.Start();
        }

        private static void Flush()
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            while (Queue.Count > 0)
            {
                var batch = Queue.Take(100).ToList();
                Queue.RemoveRange(0, batch.Count);
                Task.Run(() => LoadBatch(batch, dispatcher));
            }
        }

        public static string Folder => Path.Combine(Config.DataDir, "avatars");

        private static string FindOnDisk(string id)
        {
            try
            {
                if (!Directory.Exists(Folder)) return null;
                return Directory.GetFiles(Folder, "*_" + id + ".*").FirstOrDefault();
            }
            catch { return null; }
        }

        private static async Task LoadBatch(List<string> ids, Dispatcher dispatcher)
        {
            var data = new Dictionary<string, byte[]>();
            var stale = new Dictionary<string, string>();
            var toFetch = new List<string>();

            // 1. Cache disque (rafraîchi au bout de 7 jours)
            foreach (var id in ids)
            {
                string f = FindOnDisk(id);
                try
                {
                    if (f != null && (DateTime.Now - File.GetLastWriteTime(f)).TotalDays < 7)
                    {
                        data[id] = File.ReadAllBytes(f);
                        continue;
                    }
                }
                catch { }
                if (f != null) stale[id] = f;
                toFetch.Add(id);
            }

            // 2. API Helix pour les autres
            var urls = new Dictionary<string, (string Url, string Login)>();
            bool helixOk = false;
            if (toFetch.Count > 0)
            {
                try
                {
                    var creds = await Credentials();
                    if (creds != null)
                    {
                        string query = string.Join("&", toFetch.Select(i => "id=" + Uri.EscapeDataString(i)));
                        using (var req = new HttpRequestMessage(HttpMethod.Get, "https://api.twitch.tv/helix/users?" + query))
                        {
                            req.Headers.Add("Client-Id", ClientId());
                            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.Token);
                            using (var resp = await Http.SendAsync(req))
                            {
                                if (resp.StatusCode == HttpStatusCode.Unauthorized) TwitchAuth.InvalidateCache();
                                if (resp.IsSuccessStatusCode)
                                {
                                    helixOk = true;
                                    using (var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()))
                                    {
                                        foreach (var u in doc.RootElement.GetProperty("data").EnumerateArray())
                                        {
                                            string id = u.GetProperty("id").GetString();
                                            string login = u.GetProperty("login").GetString();
                                            string url = u.GetProperty("profile_image_url").GetString();
                                            if (!string.IsNullOrEmpty(url)) urls[id] = (url.Replace("300x300", "70x70"), login);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            // 3. Téléchargement en parallèle + enregistrement dans le dossier avatars
            var downloads = urls.Select(async kv =>
            {
                try { return (kv.Key, await Http.GetByteArrayAsync(kv.Value.Url)); }
                catch { return (kv.Key, (byte[])null); }
            }).ToList();
            foreach (var r in await Task.WhenAll(downloads))
            {
                if (r.Item2 == null) continue;
                data[r.Item1] = r.Item2;
                SaveToDisk(r.Item1, urls[r.Item1].Login, urls[r.Item1].Url, r.Item2);
            }

            // Échec réseau : réutiliser l'ancienne image du disque
            foreach (var kv in stale)
            {
                if (data.ContainsKey(kv.Key)) continue;
                try { data[kv.Key] = File.ReadAllBytes(kv.Value); } catch { }
            }

            await dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var id in ids)
                {
                    BitmapSource bmp = null;
                    if (data.TryGetValue(id, out var bytes))
                    {
                        try
                        {
                            var bi = new BitmapImage();
                            bi.BeginInit();
                            bi.StreamSource = new MemoryStream(bytes);
                            bi.CacheOption = BitmapCacheOption.OnLoad;
                            bi.DecodePixelWidth = 64;
                            bi.EndInit();
                            bi.Freeze();
                            bmp = bi;
                        }
                        catch { }
                    }

                    Pending.TryGetValue(id, out var callbacks);
                    Pending.Remove(id);
                    if (bmp == null)
                    {
                        // pas de nouvel essai pour ce compte pendant la session (sauf si non connecté)
                        if (helixOk) Failed.Add(id);
                        continue;
                    }
                    if (Cache.Count > 1000) Cache.Clear();
                    Cache[id] = bmp;
                    if (callbacks == null) continue;
                    foreach (var cb in callbacks)
                    {
                        try { cb(bmp); } catch { }
                    }
                }
            }));
        }
    }
}
