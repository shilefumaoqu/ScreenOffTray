namespace ScreenOffTray
{
    // A display request does not prove video playback. Keep the wording exact
    // without adding controls or claiming a source that a normal user cannot read.
    internal static class AutoOffStatusText
    {
        internal static string Format(AutoOffPauseReason reason, int minutes, bool english)
        {
            switch (reason) {
                case AutoOffPauseReason.UserPause:
                    return english ? "Auto off paused" : "自动关屏已暂停";
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
