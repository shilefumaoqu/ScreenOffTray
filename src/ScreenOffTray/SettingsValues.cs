namespace ScreenOffTray.Ui
{
    // Applied field transaction only; no control or drawing dependency.
    internal sealed class SettingsValues
    {
        internal int TimeoutMinutes;
        internal HotkeyBinding Hotkey;
        internal bool HotkeyEdited;
        internal string Language;
        internal bool Startup;
        internal bool AutomaticUpdateChecks=true;
        internal bool PlaybackLimitEnabled=true;
        internal int PlaybackLimitMinutes=60;
        internal string InterfaceTheme="geometry";
    }
}
