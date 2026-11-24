using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TchatOverlay
{
    public class EmoteFrames
    {
        public BitmapSource First;
        public ObjectAnimationUsingKeyFrames Animation;   // null si statique
    }

    /// <summary>Téléchargement + décodage des emotes Twitch (GIF animés compris), avec cache.</summary>
    public static class EmoteLoader
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly Dictionary<string, EmoteFrames> Cache = new Dictionary<string, EmoteFrames>();
        private static readonly Dictionary<string, List<Action<EmoteFrames>>> Pending = new Dictionary<string, List<Action<EmoteFrames>>>();

        /// <summary>À appeler depuis le thread UI. Le callback est exécuté sur le thread UI.</summary>
        public static void Get(string id, bool animated, Action<EmoteFrames> onReady)
        {
            string key = id + (animated ? "|a" : "|s");
            if (Cache.TryGetValue(key, out var cached)) { onReady(cached); return; }
            if (Pending.TryGetValue(key, out var waiting)) { waiting.Add(onReady); return; }
            Pending[key] = new List<Action<EmoteFrames>> { onReady };

            var dispatcher = Dispatcher.CurrentDispatcher;
            string url = "https://static-cdn.jtvnw.net/emoticons/v2/" + id + "/" + (animated ? "default" : "static") + "/dark/2.0";

            Task.Run(async () =>
            {
                byte[] data = null;
                try { data = await Http.GetByteArrayAsync(url); } catch { }

                await dispatcher.BeginInvoke(new Action(() =>
                {
                    EmoteFrames frames = null;
                    try { if (data != null) frames = Decode(data, animated); } catch { }
                    if (frames != null)
                    {
                        if (Cache.Count > 800) Cache.Clear();
                        Cache[key] = frames;
                    }
                    var callbacks = Pending[key];
                    Pending.Remove(key);
                    if (frames == null) return;
                    foreach (var cb in callbacks)
                    {
                        try { cb(frames); } catch { }
                    }
                }));
            });
        }

        public static void Apply(Image img, EmoteFrames f)
        {
            img.Source = f.First;
            if (f.Animation != null) img.BeginAnimation(Image.SourceProperty, f.Animation);
        }

        private static EmoteFrames Decode(byte[] data, bool animated)
        {
            var dec = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

            if (!animated || !(dec is GifBitmapDecoder) || dec.Frames.Count <= 1)
            {
                var single = dec.Frames[0];
                single.Freeze();
                return new EmoteFrames { First = single };
            }

            // Taille du canevas logique du GIF
            int w = 0, h = 0;
            try
            {
                if (dec.Metadata is BitmapMetadata gm)
                {
                    w = ToInt(Query(gm, "/logscrdesc/Width"));
                    h = ToInt(Query(gm, "/logscrdesc/Height"));
                }
            }
            catch { }
            if (w <= 0 || h <= 0)
            {
                foreach (var fr in dec.Frames)
                {
                    var m = fr.Metadata as BitmapMetadata;
                    w = Math.Max(w, ToInt(Query(m, "/imgdesc/Left")) + fr.PixelWidth);
                    h = Math.Max(h, ToInt(Query(m, "/imgdesc/Top")) + fr.PixelHeight);
                }
            }

            var full = new Rect(0, 0, w, h);
            var anim = new ObjectAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            BitmapSource background = null;
            BitmapSource first = null;
            var time = TimeSpan.Zero;

            foreach (var frame in dec.Frames)
            {
                var meta = frame.Metadata as BitmapMetadata;
                int left = ToInt(Query(meta, "/imgdesc/Left"));
                int top = ToInt(Query(meta, "/imgdesc/Top"));
                int delayCs = ToInt(Query(meta, "/grctlext/Delay"));
                int disposal = ToInt(Query(meta, "/grctlext/Disposal"));
                var frameRect = new Rect(left, top, frame.PixelWidth, frame.PixelHeight);

                var composed = Render(w, h, dc =>
                {
                    if (background != null) dc.DrawImage(background, full);
                    dc.DrawImage(frame, frameRect);
                });

                if (first == null) first = composed;
                anim.KeyFrames.Add(new DiscreteObjectKeyFrame(composed, KeyTime.FromTimeSpan(time)));
                time += TimeSpan.FromMilliseconds(delayCs < 2 ? 100 : delayCs * 10);

                switch (disposal)
                {
                    case 2: // effacer la zone de la frame
                        var bgCopy = composed;
                        background = Render(w, h, dc =>
                        {
                            var clip = new CombinedGeometry(GeometryCombineMode.Exclude,
                                new RectangleGeometry(full), new RectangleGeometry(frameRect));
                            dc.PushClip(clip);
                            dc.DrawImage(bgCopy, full);
                            dc.Pop();
                        });
                        break;
                    case 3: // revenir à l'état précédent : background inchangé
                        break;
                    default:
                        background = composed;
                        break;
                }
            }

            anim.Duration = new Duration(time);
            anim.Freeze();
            return new EmoteFrames { First = first, Animation = anim };
        }

        private static BitmapSource Render(int w, int h, Action<DrawingContext> draw)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) draw(dc);
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        private static object Query(BitmapMetadata m, string q)
        {
            try { return m != null && m.ContainsQuery(q) ? m.GetQuery(q) : null; }
            catch { return null; }
        }

        private static int ToInt(object o)
        {
            try { return o == null ? 0 : Convert.ToInt32(o); }
            catch { return 0; }
        }
    }
}
