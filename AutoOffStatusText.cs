namespace ScreenOffTray
{
    internal static class AutoOffStatusText
    {
        internal static string Format(AutoOffTimingResult result, int minutes, bool english)
        {
            if (result.PlaybackLimitReached && !result.Blocked)
                return english ? "Playback protection limit reached" : "播放保护已到上限";
            if (result.PauseReason == AutoOffPauseReason.Playback)
                return StringName(result.SourceName, english) + (english ? " playing · auto off paused" : " 正在播放 · 自动关屏已暂停");
            return Format(result.PauseReason, minutes, english);
        }

        private static string StringName(string name, bool english)
        { return System.String.IsNullOrWhiteSpace(name) ? (english ? "Whitelisted audio" : "白名单音频") : name; }

        internal static string Format(AutoOffPauseReason reason, int minutes, bool english)
        {
            switch (reason) {
                case AutoOffPauseReason.UserPause:
                    return english ? "Auto off paused" : "自动关屏已暂停";
                case AutoOffPauseReason.Playback:
                    return english ? "Auto paused: whitelisted audio playing" : "自动暂停 · 白名单音频正在播放";
                case AutoOffPauseReason.AudioUnknown:
                    return english ? "Auto paused: audio state unavailable" : "自动暂停 · 无法读取音频状态";
                case AutoOffPauseReason.PlaybackSettingsUnknown:
                    return english ? "Whitelist unavailable. Auto off is paused" : "白名单读取失败，自动关屏已暂停";
                case AutoOffPauseReason.DisplayRequired:
                    return english ? "Auto paused: app requests display" : "自动暂停 · 应用要求亮屏";
                case AutoOffPauseReason.StateUnknown:
                    return english ? "Auto paused: display state unavailable" : "自动暂停 · 无法读取亮屏状态";
                default:
                    return english ? "Off after " + minutes + " min idle" : "空闲" + minutes + "分钟关屏";
            }
        }
    }
}
