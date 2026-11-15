namespace ScreenOffTray.Playback
{
    internal static class PlaybackSettingsPersistence
    {
        internal static void SaveForm(Properties.Settings settings, int minutes, string language,
            string hotkey, bool updates, bool limitEnabled, int limitMinutes)
        {
            int oldMinutes = settings.TimeoutMinutes, oldLimitMinutes = settings.PlaybackLimitMinutes;
            string oldLanguage = settings.Language, oldHotkey = settings.ScreenOffHotkey;
            bool oldUpdates = settings.AutomaticUpdateChecks, oldLimit = settings.PlaybackLimitEnabled;
            try {
                settings.TimeoutMinutes = minutes; settings.Language = language;
                settings.ScreenOffHotkey = hotkey; settings.AutomaticUpdateChecks = updates;
                settings.PlaybackLimitEnabled = limitEnabled; settings.PlaybackLimitMinutes = limitMinutes;
                settings.Save();
            } catch {
                settings.TimeoutMinutes = oldMinutes; settings.Language = oldLanguage;
                settings.ScreenOffHotkey = oldHotkey; settings.AutomaticUpdateChecks = oldUpdates;
                settings.PlaybackLimitEnabled = oldLimit; settings.PlaybackLimitMinutes = oldLimitMinutes;
                throw;
            }
        }

        // Only this field is changed; form drafts never enter this transaction.
        internal static string[] SaveWhitelist(Properties.Settings settings, string[] paths)
        {
            string[] normalized = PlaybackSettingsCodec.Normalize(paths);
            string json = PlaybackSettingsCodec.Serialize(normalized);
            string previous = settings.PlaybackWhitelistPaths;
            try { settings.PlaybackWhitelistPaths = json; settings.Save(); }
            catch { settings.PlaybackWhitelistPaths = previous; throw; }
            return normalized;
        }
    }
}
