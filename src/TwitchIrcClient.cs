using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TchatOverlay
{
    public class EmoteRange
    {
        public string Id;
        public int Start;
        public int End;
    }

    public class ChatMessage
    {
        public string Id = "";
        public string Login = "";
        public string UserId = "";
        public string Channel = "";
        public string DisplayName = "";
        public string Color = "";
        public string Text = "";
        public bool IsAction;
        public bool IsSystem;
        public List<EmoteRange> Emotes = new List<EmoteRange>();
    }

    public class ChannelUserState
    {
        public bool IsModerator;     // modérateur ou propriétaire de la chaîne
        public string Color = "";
        public string DisplayName = "";
    }

    /// <summary>Client IRC Twitch anonyme (lecture seule, aucun compte requis).</summary>
    public sealed class TwitchIrcClient : IDisposable
    {
        public event Action<ChatMessage> MessageReceived;
        public event Action<string> MessageDeleted;   // id du message
        public event Action<string, string> ChatCleared;   // (chaîne, login ou null = tout le tchat)
        public event Action<string> StatusChanged;
        public event Action AuthRequired;              // jeton invalide : reconnexion du compte nécessaire

        public event Action<string, string> NoticeReceived;   // (chaîne, texte)

        private CancellationTokenSource _cts;
        private readonly Random _rnd = new Random();
        private volatile StreamWriter _writer;                 // non null = connecté avec un compte
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, string> _roomIds = new ConcurrentDictionary<string, string>();
        private readonly ConcurrentDictionary<string, ChannelUserState> _states = new ConcurrentDictionary<string, ChannelUserState>();

        public bool CanSend => _writer != null;
        public string GetRoomId(string channel) => _roomIds.TryGetValue(channel ?? "", out var id) ? id : null;
        public ChannelUserState GetUserState(string channel) => _states.TryGetValue(channel ?? "", out var st) ? st : null;
        public bool IsModerator(string channel) => GetUserState(channel)?.IsModerator == true;

        /// <summary>Envoie un message (compte connecté requis). Renvoie null si OK, sinon le message d'erreur.</summary>
        public async Task<string> SendAsync(string channel, string text, string replyParentId = null)
        {
            var w = _writer;
            if (w == null) return "Non connecté avec un compte Twitch.";
            text = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (text.Length == 0) return "Message vide.";
            string line = (string.IsNullOrEmpty(replyParentId) ? "" : "@reply-parent-msg-id=" + replyParentId + " ")
                          + "PRIVMSG #" + NormalizeChannel(channel) + " :" + text;
            try { await WriteAsync(w, line); return null; }
            catch (Exception ex) { return ex.Message; }
        }

        private async Task WriteAsync(StreamWriter w, string line)
        {
            await _writeLock.WaitAsync();
            try { await w.WriteLineAsync(line); }
            finally { _writeLock.Release(); }
        }

        public static string NormalizeChannel(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            string s = input.Trim().TrimEnd('/');
            int slash = s.LastIndexOf('/');
            if (slash >= 0) s = s.Substring(slash + 1);
            return s.TrimStart('#').ToLowerInvariant();
        }

        public void Start(IEnumerable<string> channels, Func<Task<IrcCredentials>> credentials = null)
        {
            Stop();
            var list = new List<string>();
            foreach (var c in channels)
            {
                string n = NormalizeChannel(c);
                if (n.Length > 0 && !list.Contains(n)) list.Add(n);
            }
            string channel = string.Join(",", list);
            if (channel.Length == 0)
            {
                StatusChanged?.Invoke("Aucune chaîne configurée");
                return;
            }
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Task.Run(() => RunLoop(channel, credentials, token));
        }

        public void Stop()
        {
            _writer = null;
            if (_cts != null)
            {
                try { _cts.Cancel(); } catch { }
                _cts.Dispose();
                _cts = null;
            }
        }

        public void Dispose() => Stop();

        private async Task RunLoop(string channel, Func<Task<IrcCredentials>> credentials, CancellationToken token)
        {
            int backoff = 1;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    IrcCredentials creds = null;
                    if (credentials != null)
                    {
                        StatusChanged?.Invoke("Vérification du compte Twitch...");
                        creds = await credentials();
                        if (creds == null)
                        {
                            StatusChanged?.Invoke("Session Twitch expirée — reconnectez le compte dans Paramètres");
                            AuthRequired?.Invoke();
                            return;
                        }
                    }
                    if (token.IsCancellationRequested) break;

                    using (var tcp = new TcpClient())
                    {
                        StatusChanged?.Invoke("Connexion à #" + channel.Replace(",", ", #") + "...");
                        await tcp.ConnectAsync("irc.chat.twitch.tv", 6667, token);
                        using (var stream = tcp.GetStream())
                        using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
                        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true })
                        {
                            await writer.WriteLineAsync("CAP REQ :twitch.tv/tags twitch.tv/commands");
                            if (creds != null)
                            {
                                await writer.WriteLineAsync("PASS oauth:" + creds.Token);
                                await writer.WriteLineAsync("NICK " + creds.Login);
                            }
                            else
                            {
                                await writer.WriteLineAsync("PASS SCHMOOPIIE");
                                await writer.WriteLineAsync("NICK justinfan" + _rnd.Next(10000, 99999));
                            }
                            await writer.WriteLineAsync("JOIN #" + channel.Replace(",", ",#"));
                            if (creds != null) _writer = writer;

                            try
                            {
                                while (!token.IsCancellationRequested)
                                {
                                    string line = await reader.ReadLineAsync(token);
                                    if (line == null) break;
                                    if (line.StartsWith("PING"))
                                    {
                                        await WriteAsync(writer, "PONG" + line.Substring(4));
                                        continue;
                                    }
                                    int r = HandleLine(line, channel);
                                    if (r == 1) backoff = 1;
                                    else if (r < 0) break;
                                }
                            }
                            finally
                            {
                                if (_writer == writer) _writer = null;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke("Erreur réseau : " + ex.Message);
                }

                if (token.IsCancellationRequested) break;
                StatusChanged?.Invoke("Déconnecté — reconnexion dans " + backoff + " s");
                try { await Task.Delay(backoff * 1000, token); } catch { break; }
                backoff = Math.Min(backoff * 2, 30);
            }
        }

        /// <returns>-1 = reconnexion demandée, 1 = connecté, 0 = normal.</returns>
        private int HandleLine(string line, string channel)
        {
            Parse(line, out var tags, out var prefix, out var command, out var target, out var trailing);

            switch (command)
            {
                case "ROOMSTATE":
                    {
                        string rid = Tag(tags, "room-id");
                        if (rid.Length > 0) _roomIds[target] = rid;
                        // ROOMSTATE complet (à l'arrivée) = tous les modes ; partiel = un seul mode changé
                        if (!(tags.ContainsKey("r9k") && tags.ContainsKey("subs-only") && tags.ContainsKey("slow"))) return 0;
                        StatusChanged?.Invoke("Connecté à #" + target);
                        return 1;
                    }

                case "USERSTATE":
                    {
                        string badges = Tag(tags, "badges");
                        _states[target] = new ChannelUserState
                        {
                            IsModerator = Tag(tags, "mod") == "1" || badges.Contains("broadcaster/"),
                            Color = Tag(tags, "color"),
                            DisplayName = Tag(tags, "display-name")
                        };
                        break;
                    }

                case "PRIVMSG":
                    {
                        var m = new ChatMessage();
                        int bang = prefix.IndexOf('!');
                        m.Login = bang > 0 ? prefix.Substring(0, bang) : prefix;
                        m.DisplayName = Tag(tags, "display-name");
                        if (m.DisplayName.Length == 0) m.DisplayName = m.Login;
                        m.Color = Tag(tags, "color");
                        m.Id = Tag(tags, "id");
                        m.UserId = Tag(tags, "user-id");
                        m.Channel = target;
                        string roomId = Tag(tags, "room-id");
                        if (roomId.Length > 0) _roomIds[target] = roomId;

                        string text = trailing;
                        if (text.StartsWith("\u0001ACTION ") && text.EndsWith("\u0001"))
                        {
                            m.IsAction = true;
                            text = text.Substring(8, text.Length - 9);
                        }
                        m.Text = text;
                        m.Emotes = ParseEmotes(Tag(tags, "emotes"));
                        MessageReceived?.Invoke(m);
                        break;
                    }

                case "CLEARMSG":
                    MessageDeleted?.Invoke(Tag(tags, "target-msg-id"));
                    break;

                case "CLEARCHAT":
                    ChatCleared?.Invoke(target, string.IsNullOrEmpty(trailing) ? null : trailing.ToLowerInvariant());
                    break;

                case "NOTICE":
                    if (trailing.Contains("authentication failed") || trailing.Contains("Improperly formatted auth"))
                    {
                        StatusChanged?.Invoke("Authentification refusée — nouvel essai");
                        return -1;
                    }
                    if (trailing.Length > 0)
                    {
                        StatusChanged?.Invoke(trailing);
                        NoticeReceived?.Invoke(target, trailing);
                    }
                    break;

                case "RECONNECT":
                    return -1;
            }
            return 0;
        }

        private static string Tag(Dictionary<string, string> tags, string key)
        {
            return tags.TryGetValue(key, out var v) ? v : "";
        }

        private static List<EmoteRange> ParseEmotes(string tag)
        {
            var list = new List<EmoteRange>();
            if (string.IsNullOrEmpty(tag)) return list;
            foreach (var group in tag.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = group.IndexOf(':');
                if (colon <= 0) continue;
                string id = group.Substring(0, colon);
                foreach (var range in group.Substring(colon + 1).Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    int dash = range.IndexOf('-');
                    if (dash <= 0) continue;
                    if (int.TryParse(range.Substring(0, dash), out int s) &&
                        int.TryParse(range.Substring(dash + 1), out int e))
                        list.Add(new EmoteRange { Id = id, Start = s, End = e });
                }
            }
            list.Sort((a, b) => a.Start.CompareTo(b.Start));
            return list;
        }

        private static void Parse(string line, out Dictionary<string, string> tags, out string prefix,
                                  out string command, out string target, out string trailing)
        {
            tags = new Dictionary<string, string>();
            prefix = "";
            command = "";
            target = "";
            trailing = "";
            int i = 0;

            if (line.StartsWith("@"))
            {
                int sp = line.IndexOf(' ');
                if (sp < 0) return;
                foreach (var kv in line.Substring(1, sp - 1).Split(';'))
                {
                    int eq = kv.IndexOf('=');
                    if (eq < 0) tags[kv] = "";
                    else tags[kv.Substring(0, eq)] = Unescape(kv.Substring(eq + 1));
                }
                i = sp + 1;
            }

            if (i < line.Length && line[i] == ':')
            {
                int sp = line.IndexOf(' ', i);
                if (sp < 0) return;
                prefix = line.Substring(i + 1, sp - i - 1);
                i = sp + 1;
            }

            int t = line.IndexOf(" :", i);
            string middle = t >= 0 ? line.Substring(i, t - i) : line.Substring(i);
            if (t >= 0) trailing = line.Substring(t + 2);

            var parts = middle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0) command = parts[0];
            if (parts.Length > 1) target = parts[1].TrimStart('#').ToLowerInvariant();
        }

        private static string Unescape(string v)
        {
            if (v.IndexOf('\\') < 0) return v;
            var sb = new StringBuilder(v.Length);
            for (int i = 0; i < v.Length; i++)
            {
                char c = v[i];
                if (c == '\\' && i + 1 < v.Length)
                {
                    char n = v[++i];
                    switch (n)
                    {
                        case 's': sb.Append(' '); break;
                        case ':': sb.Append(';'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'n': sb.Append('\n'); break;
                        default: sb.Append(n); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
