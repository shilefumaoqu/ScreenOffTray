using System;

namespace ScreenOffTray.Playback
{
    // Read-only classification. Never advances the one authoritative countdown.
    internal static class PlaybackProtection
    {
        internal static PlaybackProtectionState Classify(AudioSnapshot audio, PlaybackOptions options,
            uint physicalIdle, bool manualPaused)
        {
            if (manualPaused) return new PlaybackProtectionState(AutoOffPauseReason.UserPause, false, null);
            if (options == null || !options.WhitelistKnown)
                return new PlaybackProtectionState(AutoOffPauseReason.PlaybackSettingsUnknown, false, null);
            if (audio == null || !audio.Known)
                return new PlaybackProtectionState(AutoOffPauseReason.AudioUnknown, false, null);
            string[] paths = options == null ? new string[0] : options.WhitelistPaths;
            if (audio.Sources != null && paths != null)
                foreach (AudioSource source in audio.Sources) {
                    if (source == null) continue;
                    string path = ExeIdentity.Normalize(source.ExePath);
                    if (path == null) continue;
                    foreach (string allowed in paths) {
                        if (!String.Equals(path, allowed, StringComparison.OrdinalIgnoreCase)) continue;
                        int minutes = options.PlaybackLimitMinutes;
                        if (minutes < 1 || minutes > 1440) minutes = 60;
                        bool limit = options.PlaybackLimitEnabled && physicalIdle >= (uint)minutes * 60000U;
                        return new PlaybackProtectionState(limit ? AutoOffPauseReason.None : AutoOffPauseReason.Playback,
                            limit, source.DisplayName);
                    }
                }
            return new PlaybackProtectionState(AutoOffPauseReason.None, false, null);
        }

        // The session consults this at both automatic boundaries; a missing or
        // failed callback cannot silently turn an automatic request into manual.
        internal static PlaybackProtectionState Read(Func<PlaybackProtectionState> read)
        {
            try { if (read != null) return read(); }
            catch (Exception) { }
            return new PlaybackProtectionState(AutoOffPauseReason.AudioUnknown, false, null);
        }
    }
}
