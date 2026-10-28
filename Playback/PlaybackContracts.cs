using System;
using System.IO;

namespace ScreenOffTray.Playback
{
    internal sealed class AudioSource
    {
        public string ExePath { get; set; }
        public string DisplayName { get; set; }
        public int SessionCount { get; set; }
        public int[] ProcessIds { get; set; }
    }

    internal sealed class AudioSnapshot
    {
        public bool Known { get; set; }
        public string Error { get; set; }
        public int UnidentifiedSourceCount { get; set; }
        public string Warning { get; set; }
        public AudioSource[] Sources { get; set; }
        public DateTime SampleUtc { get; set; }

        internal static AudioSnapshot Unknown(string error)
        {
            return new AudioSnapshot { Known = false, Error = error,
                Sources = new AudioSource[0], SampleUtc = DateTime.UtcNow };
        }
    }

    internal interface IAudioMonitor : IDisposable
    {
        AudioSnapshot Latest { get; }
        bool IsReading { get; }
        void RequestRefresh();
    }

    internal sealed class PlaybackOptions
    {
        internal bool WhitelistKnown = true;
        internal bool PlaybackLimitEnabled = true;
        internal int PlaybackLimitMinutes = 60;
        internal string[] WhitelistPaths = new string[0];

        internal PlaybackOptions Copy()
        {
            return new PlaybackOptions { PlaybackLimitEnabled = PlaybackLimitEnabled,
                WhitelistKnown = WhitelistKnown,
                PlaybackLimitMinutes = PlaybackLimitMinutes,
                WhitelistPaths = WhitelistPaths == null ? new string[0] : (string[])WhitelistPaths.Clone() };
        }
    }

    internal sealed class PlaybackPanelState
    {
        internal AudioSnapshot Audio;
        internal AutoOffTimingResult Timing;
        internal uint PhysicalIdleMilliseconds;
        internal PlaybackOptions AppliedOptions;
        internal string SettingsWarning;
    }

    internal static class ExeIdentity
    {
        internal static string Normalize(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) ||
                !String.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) return null;
            try { return Path.GetFullPath(path); }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
        }
    }
}
