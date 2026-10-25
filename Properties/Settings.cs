using System.Configuration;

namespace ScreenOffTray.Properties
{
    internal sealed class Settings : ApplicationSettingsBase
    {
        private static readonly Settings DefaultInstance =
            (Settings)Synchronized(new Settings());

        public static Settings Default
        {
            get { return DefaultInstance; }
        }

        [UserScopedSetting]
        [DefaultSettingValue("10")]
        public int TimeoutMinutes
        {
            get { return (int)this["TimeoutMinutes"]; }
            set { this["TimeoutMinutes"] = value; }
        }

        [UserScopedSetting, DefaultSettingValue("zh-CN")]
        public string Language { get { return (string)this["Language"]; } set { this["Language"] = value; } }

        [UserScopedSetting, DefaultSettingValue("Ctrl+Alt+F10")]
        public string ScreenOffHotkey { get { return (string)this["ScreenOffHotkey"]; } set { this["ScreenOffHotkey"] = value; } }

        [UserScopedSetting, DefaultSettingValue("True")]
        public bool AutomaticUpdateChecks { get { return (bool)this["AutomaticUpdateChecks"]; } set { this["AutomaticUpdateChecks"] = value; } }

        [UserScopedSetting, DefaultSettingValue("False")]
        public bool MigrationComplete { get { return (bool)this["MigrationComplete"]; } set { this["MigrationComplete"] = value; } }

        [UserScopedSetting, DefaultSettingValue("")]
        public string DisplayProfile { get { return (string)this["DisplayProfile"]; } set { this["DisplayProfile"] = value; } }

        [UserScopedSetting, DefaultSettingValue("")]
        public string DisplayProfileCheckedUtc { get { return (string)this["DisplayProfileCheckedUtc"]; } set { this["DisplayProfileCheckedUtc"] = value; } }

        [UserScopedSetting, DefaultSettingValue("")]
        public string CompletedDisplayProfile { get { return (string)this["CompletedDisplayProfile"]; } set { this["CompletedDisplayProfile"] = value; } }

        [UserScopedSetting, DefaultSettingValue("False")]
        public bool SetupIntroShown { get { return (bool)this["SetupIntroShown"]; } set { this["SetupIntroShown"] = value; } }

        [UserScopedSetting, DefaultSettingValue("")]
        public string SetupLastNoticeKey { get { return (string)this["SetupLastNoticeKey"]; } set { this["SetupLastNoticeKey"] = value; } }
    }
}
