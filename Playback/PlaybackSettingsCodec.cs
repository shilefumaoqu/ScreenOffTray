using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace ScreenOffTray.Playback
{
    internal static class PlaybackSettingsCodec
    {
        internal const int MaxBytes = 1024 * 1024, MaxEntries = 128;

        internal static string[] Normalize(string[] candidates)
        {
            candidates = candidates ?? new string[0];
            if (candidates.Length > MaxEntries) throw new InvalidDataException("白名单最多允许 128 项 / At most 128 whitelist entries.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var paths = new List<string>();
            foreach (string candidate in candidates) {
                string path = ExeIdentity.Normalize(candidate);
                if (path == null) throw new InvalidDataException("白名单需要有效的绝对 exe 路径 / Whitelist requires absolute exe paths.");
                if (seen.Add(path)) paths.Add(path);
            }
            return paths.ToArray();
        }

        internal static string Serialize(string[] paths)
        {
            string json = Serializer().Serialize(Normalize(paths));
            if (Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new InvalidDataException("白名单过大 / Whitelist is too large.");
            return json;
        }

        internal static string[] Deserialize(string json)
        {
            if (json == null || Encoding.UTF8.GetByteCount(json) > MaxBytes)
                throw new InvalidDataException("白名单格式或大小无效 / Invalid whitelist data.");
            object[] values = Serializer().DeserializeObject(json) as object[];
            if (values == null || values.Length > MaxEntries) throw new InvalidDataException("白名单需要数组 / Whitelist must be an array.");
            string[] paths = new string[values.Length];
            for (int i = 0; i < paths.Length; i++) {
                paths[i] = values[i] as string;
                if (paths[i] == null) throw new InvalidDataException("白名单路径无效 / Invalid whitelist path.");
            }
            return Normalize(paths);
        }

        private static JavaScriptSerializer Serializer()
        { return new JavaScriptSerializer { MaxJsonLength = MaxBytes, RecursionLimit = 8 }; }
    }
}
