using System;
using System.Collections.Generic;

namespace ScreenOffTray.Playback
{
    internal enum MediaSessionPlayback { Unknown, Playing, Paused, Other }

    internal sealed class MediaSessionStatus
    {
        public string SourceAppId { get; set; }
        public MediaSessionPlayback State { get; set; }
    }

    internal sealed class MediaStateSnapshot
    {
        public bool Known { get; set; }
        public long ObservedMilliseconds { get; set; }
        public MediaSessionStatus[] Sessions { get; set; }
    }

    internal interface IMediaSourceResolver
    {
        bool TryResolve(AudioSource source, out string appId);
    }

    // Auxiliary evidence only: uncertainty always falls back to the original audio source.
    internal static class MediaPauseFilter
    {
        internal static bool IsFresh(MediaStateSnapshot media, long nowMilliseconds)
        {
            return media != null && media.Known && media.ObservedMilliseconds <= nowMilliseconds &&
                unchecked((ulong)nowMilliseconds - (ulong)media.ObservedMilliseconds) <= 2000UL;
        }

        public static AudioSnapshot Apply(AudioSnapshot audio, MediaStateSnapshot media,
            IMediaSourceResolver resolver, long nowMilliseconds)
        {
            if (audio == null || !audio.Known || audio.Sources == null || audio.Sources.Length == 0 ||
                media == null || !media.Known || media.Sessions == null || media.Sessions.Length == 0 ||
                resolver == null) return audio;

            // Ordered signed timestamps plus unsigned subtraction also handle signed-range overflow.
            if (!IsFresh(media, nowMilliseconds)) return audio;

            Dictionary<string, bool> allPaused = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (MediaSessionStatus session in media.Sessions)
            {
                // An unassigned session could belong to any source; do not claim a complete match.
                if (session == null || String.IsNullOrWhiteSpace(session.SourceAppId)) return audio;
                bool previous;
                bool paused = session.State == MediaSessionPlayback.Paused;
                if (allPaused.TryGetValue(session.SourceAppId, out previous)) paused = previous && paused;
                allPaused[session.SourceAppId] = paused;
            }

            string[] ids = new string[audio.Sources.Length];
            Dictionary<string, string> paths = new Dictionary<string, string>(StringComparer.Ordinal);
            HashSet<string> ambiguous = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < audio.Sources.Length; i++)
            {
                AudioSource source = audio.Sources[i];
                if (source == null) continue;
                string id;
                try
                {
                    if (!resolver.TryResolve(source, out id) || String.IsNullOrWhiteSpace(id)) continue;
                }
                catch (Exception)
                {
                    // Identity lookup failure must not erase this source or other audio evidence.
                    continue;
                }
                ids[i] = id;
                if (String.IsNullOrWhiteSpace(source.ExePath)) ambiguous.Add(id);
                string previousPath;
                if (paths.TryGetValue(id, out previousPath))
                {
                    // Windows executable paths are case insensitive; AppIDs deliberately are not.
                    if (!String.Equals(previousPath, source.ExePath, StringComparison.OrdinalIgnoreCase))
                        ambiguous.Add(id);
                }
                else paths.Add(id, source.ExePath);
            }

            List<AudioSource> kept = new List<AudioSource>(audio.Sources.Length);
            for (int i = 0; i < audio.Sources.Length; i++)
            {
                string id = ids[i];
                bool paused;
                if (id != null && !ambiguous.Contains(id) && allPaused.TryGetValue(id, out paused) && paused)
                    continue;
                kept.Add(audio.Sources[i]);
            }
            if (kept.Count == audio.Sources.Length) return audio;
            return new AudioSnapshot
            {
                Known = audio.Known,
                SampleUtc = audio.SampleUtc,
                Error = audio.Error,
                Warning = audio.Warning,
                UnidentifiedSourceCount = audio.UnidentifiedSourceCount,
                Sources = kept.ToArray()
            };
        }
    }
}
