using System;
using System.Diagnostics;
using System.IO;

namespace ScreenOffTray.Playback
{
    internal static class AudioSourceNames
    {
        // Called only on the audio reader thread, once per cached executable.
        internal static string Read(string path)
        {
            string known = KnownName(FileStem(path));
            if (known != null) return known;
            string description = null, product = null;
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                description = info.FileDescription; product = info.ProductName;
            }
            catch { }
            return Resolve(path, description, product);
        }

        internal static string Resolve(string path, string description, string product)
        {
            string name = FileStem(path);
            string known = KnownName(name);
            if (known != null) return known;
            string useful = Clean(description);
            if (String.Equals(useful, name, StringComparison.OrdinalIgnoreCase)) useful = null;
            useful = useful ?? Clean(product);
            if (useful != null && !String.Equals(useful, name, StringComparison.OrdinalIgnoreCase)) return useful;
            return name + "（未提供软件名）";
        }

        // Safe UI fallback: never opens or reads the executable on the UI thread.
        internal static string Fallback(string path)
        {
            string name = FileStem(path);
            return KnownName(name) ?? name + "（未提供软件名）";
        }

        private static string KnownName(string stem)
        {
            switch (stem.ToLowerInvariant())
            {
                case "chrome": return "Chrome";
                case "msedge": return "Microsoft Edge";
                case "firefox": return "Firefox";
                case "cloudmusic": return "网易云音乐";
                case "qqmusic": return "QQ音乐";
                default: return null;
            }
        }

        private static string FileStem(string path)
        {
            try { return String.IsNullOrWhiteSpace(path) ? "未知程序" : Path.GetFileNameWithoutExtension(path); }
            catch { return "未知程序"; }
        }

        private static string Clean(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            string clean = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            if (String.Equals(clean, "Electron", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(clean, "Application", StringComparison.OrdinalIgnoreCase)) return null;
            return clean.Length > 80 ? clean.Substring(0, 80) : clean;
        }
    }
}
