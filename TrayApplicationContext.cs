using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using ScreenOffTray.Properties;
using ScreenOffTray.Ui;
using OledPrototype;

namespace ScreenOffTray
{
    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private const int DefaultTimeoutMinutes = 10, MinimumTimeoutMinutes = 1, MaximumTimeoutMinutes = 1440;
        private const uint KeepAwakeLeadMilliseconds = 30000U;
        private readonly NotifyIcon _trayIcon;
        private readonly OledSession _session;
        private readonly DisplaySetupState _setup;
        private TrayMenu _view;
        private readonly Timer _timer;
        private readonly ExitCleanup _exitCleanup = new ExitCleanup();
        private readonly Stopwatch _autoClock = Stopwatch.StartNew();
        private readonly AutoOffTiming _autoTiming;
        private AutoOffTimingResult _autoTimingState = new AutoOffTimingResult(0, AutoOffPauseReason.StateUnknown);
        private AutoOffPauseReason? _lastLoggedAutoPause;
        private bool _finishingExit;
        private bool _displayOffSupported { get { return _session.Supported; } }
        private string _displayOffUnavailableReason { get { return _session.UnavailableReason; } }
        private UiText _text;
        private SettingsDialog _settingsDialog;
        private readonly SetupPromptPolicy _setupPrompts;
        private readonly SetupPromptHistoryWriter _promptHistory = new SetupPromptHistoryWriter();
        private bool _migrationDone, _hotkeyEditing, _exitSentToSession, _historySaveWarning;
        private string _hotkeyLoadWarning;
        private string _settingsFailure;
        private DateTime _nextSetupCheck;
        private int _timeoutMinutes;
        private bool _paused, _triggeredForIdlePeriod, _keepAwakeRequested, _exiting;
        private uint _lastPhysicalInputVersion;
        private int? _pendingTimeoutMinutes;
        private readonly UpdateChecker _updates;
        private System.Threading.CancellationTokenSource _updateCancellation;
        private UpdateCheckResult _updateResult;
        private bool _autoUpdatesEnabled = true, _updateChecking, _automaticUpdateCheck, _pendingUpdateNotice;

        internal TrayApplicationContext()
        {
            _autoTiming = new AutoOffTiming(delegate { return _autoClock.ElapsedMilliseconds; });
            string language = "zh-CN";
            try { language = Settings.Default.Language; } catch (Exception ex) { _settingsFailure = ex.Message; }
            _text = new UiText(language);
            _updates = UpdateChecker.FromConfiguration();
            try { _autoUpdatesEnabled = Settings.Default.AutomaticUpdateChecks; } catch { _autoUpdatesEnabled = false; }
            _timeoutMinutes = LoadTimeoutMinutes();
            HotkeyBinding binding = HotkeyBinding.Default;
            try {
                HotkeyBinding saved; string reason;
                if (HotkeyBinding.TryParse(Settings.Default.ScreenOffHotkey, out saved, out reason)) binding = saved;
                else _hotkeyLoadWarning = _text.Pick("保存的快捷键无效，本次使用 Ctrl+Alt+F10；请在“关屏快捷键”中重新选择。", "The saved shortcut is invalid. Ctrl+Alt+F10 is used for this run; choose a new shortcut in Display-off hotkey.");
            } catch (Exception ex) { _settingsFailure = ex.Message; }
            bool introShown = false, previouslyPrepared = false; string lastNotice = "";
            try {
                introShown = Settings.Default.SetupIntroShown; lastNotice = Settings.Default.SetupLastNoticeKey;
                previouslyPrepared = !String.IsNullOrEmpty(Settings.Default.CompletedDisplayProfile);
            } catch (Exception ex) { _settingsFailure = ex.Message; }
            _setupPrompts = new SetupPromptPolicy(introShown, lastNotice, previouslyPrepared);
            // Cheap gate only. Native readiness work never runs inside the hook.
            _session = new OledSession(delegate { return _migrationDone && !_exiting && !_hotkeyEditing && _setup != null && _setup.IsReady; }, binding);
            _setup = new DisplaySetupState(Post, PostSetupCompletion);
            _setup.RecoveryStopConfirmationAvailable += _session.TrackRecoveryStop;
            _trayIcon = new NotifyIcon { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application, Visible = true };
            RebuildMenu();
            _session.Changed += SessionChanged;
            _session.AutomaticProtectionObserved += AutomaticProtectionObserved;
            _session.Problem += delegate(string message) { _trayIcon.ShowBalloonTip(5000, _text.Title, LocalizeSession(message), ToolTipIcon.Warning); };
            _session.ExitReady += FinishExit;
            _setup.Changed += SetupChanged;
            SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
            _timer = new Timer { Interval = 5000 };
            _timer.Tick += delegate {
                RefreshAutomaticTiming();
                if (!_session.IsBusy && !_setup.Preparing && !_exiting && _migrationDone && DateTime.UtcNow >= _nextSetupCheck) {
                    _nextSetupCheck = DateTime.UtcNow.AddSeconds(15); _setup.Recheck();
                }
                CheckIdleState();
                UpdateMenuState();
            };
            _timer.Tick += delegate { NotifyAvailableUpdate(); };
            RefreshAutomaticTiming(); _timer.Start(); UpdateMenuState();
            if (_hotkeyLoadWarning != null) Post(delegate { _trayIcon.ShowBalloonTip(5000, _text.Title, _hotkeyLoadWarning, ToolTipIcon.Warning); });
            Task.Run(delegate {
                SettingsMigrationResult migration = SettingsMigration.Begin();
                Post(delegate { FinishMigration(migration); });
                OledPrototype.Program.Log("tray_started; timeout_minutes=" + _timeoutMinutes + "; input_hooks_registered=" + _session.Supported);
            });
        }
        private void Post(Action action)
        {
            if ((_exiting && (_setup == null || !_setup.Preparing)) || _session.IsDisposed || !_session.IsHandleCreated) return;
            try { _session.BeginInvoke(action); } catch (InvalidOperationException) { }
        }
        private void PostSetupCompletion(Action action)
        {
            if (_session.IsDisposed || !_session.IsHandleCreated)
                throw new InvalidOperationException("Setup completion dispatcher unavailable.");
            // Only setup lifecycle callbacks use this path during exit. Their
            // generation/disposal checks still run inside DisplaySetupState.
            _session.BeginInvoke(new Action(delegate {
                try { action(); }
                catch (Exception ex) {
                    OledPrototype.Program.Log("setup_completion_callback_failed: " + ex);
                    if (_exiting && !_setup.Preparing) ContinueExit();
                }
            }));
        }
        private void FinishMigration(SettingsMigrationResult migration)
        {
            if (migration.Warning != null) MessageBox.Show(migration.Warning, _text.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (!migration.CanFinish) { _settingsFailure = migration.Warning; UpdateMenuState(); return; }
            if (migration.NeedsChoice) {
                MessageBox.Show(_text.Pick("发现不同的旧关闭时间：", "Different previous timeouts were found: ") + String.Join(", ", migration.Choices) +
                    _text.Pick(" 分钟。请在下一窗口选择要保留的时间；取消会暂停关屏，下次启动可重选。", " min. Choose the timeout to keep in the next dialog. Cancel keeps display off paused until the next launch."),
                    _text.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                using (var dialog = new TimeoutDialog(_text, _timeoutMinutes)) {
                    if (dialog.ShowDialog() != DialogResult.OK) { UpdateMenuState(); return; }
                    Settings.Default.TimeoutMinutes = dialog.Minutes;
                    try { SettingsMigration.Finish(); } catch (Exception ex) { ShowSaveError(ex); return; }
                }
            }
            _timeoutMinutes = LoadTimeoutMinutes();
            _migrationDone = true; _setup.Recheck(); UpdateMenuState();
            if (_autoUpdatesEnabled && _updates.IsConfigured) CheckForUpdates(true);
        }
        private void RebuildMenu()
        {
            var previous = _view;
            _view = new TrayMenu(_text, TurnOffDisplaysNow, TogglePause, ShowSettings, ExitApplication, 1F);
            _view.Menu.Opening += delegate {
                if (!_session.IsBusy) RefreshAutomaticTiming();
                if (!_session.IsBusy && !_setup.Preparing && _migrationDone && DateTime.UtcNow >= _nextSetupCheck) {
                    _nextSetupCheck = DateTime.UtcNow.AddSeconds(15); _setup.Recheck();
                }
                UpdateMenuState();
            };
            _trayIcon.ContextMenuStrip = _view.Menu;
            // Dispose after the active language submenu click has unwound.
            if (previous != null) Post(delegate { previous.Dispose(); });
        }
        private void TogglePause()
        {
            _paused = !_paused;
            _autoTiming.ResetCountdown(); RefreshAutomaticTiming();
            if (_paused) { _session.RequestEnd(false); ReleaseIdlePowerRequest(); }
            UpdateMenuState();
        }
        private void SetupChanged()
        {
            if (_exiting) { if (!_setup.Preparing) ContinueExit(); return; }
            // A refresh temporarily prevents a new session, but does not invalidate
            // an already verified profile or release its existing idle power hold.
            if (!_setup.HasVerifiedProfile) ReleaseIdlePowerRequest();
            RefreshSettingsPanel();
            UpdateMenuState();
            ObserveSetupPresentation();
        }
        private void DisplaySettingsChanged(object sender, EventArgs e)
        {
            Post(delegate {
                if (_session.IsBusy || _setup.Preparing || _exiting) return;
                _setup.Invalidate(); if (_migrationDone) _setup.Recheck();
            });
        }
        private string SetupDetail()
        {
            return _setup.Detail(_text.English);
        }
        private void ObserveSetupPresentation()
        {
            if (_settingsDialog != null && _settingsDialog.Visible && _setup.HasSettledResult) {
                _setupPrompts.AcknowledgeVisibleState(_setup.IsReady, SetupPromptPolicy.AttentionKey(_setup.Result, _setup.LastPreparationError));
                SavePromptHistory(); return;
            }
            SetupNotice notice = _setupPrompts.Observe(_setup.HasSettledResult, _setup.IsReady,
                !_migrationDone || _session.IsBusy || _hotkeyEditing || _exiting,
                SetupPromptPolicy.AttentionKey(_setup.Result, _setup.LastPreparationError));
            SavePromptHistory();
            if (notice == SetupNotice.Introduction) {
                Post(delegate { if (!_exiting && !_session.IsBusy && !_setup.IsReady && _setup.HasSettledResult) ShowSettings(); });
            } else if (notice == SetupNotice.Attention) {
                _trayIcon.ShowBalloonTip(5000, _text.Title,
                    _text.Pick("关屏未启用，请从托盘的“设置”处理显示配置。", "Display off is disabled. Open Settings from the tray to review your displays."), ToolTipIcon.Warning);
            }
        }
        private void SavePromptHistory()
        {
            try {
                bool matches = Settings.Default.SetupIntroShown == _setupPrompts.IntroShown && Settings.Default.SetupLastNoticeKey == _setupPrompts.LastNoticeKey;
                _promptHistory.Save(matches, delegate {
                    Settings.Default.SetupIntroShown = _setupPrompts.IntroShown;
                    Settings.Default.SetupLastNoticeKey = _setupPrompts.LastNoticeKey;
                }, delegate { Settings.Default.Save(); });
            } catch (Exception ex) {
                if (!_historySaveWarning) { _historySaveWarning = true; OledPrototype.Program.Log("setup_prompt_history_save_failed: " + ex.Message); }
            }
        }
        private void RefreshSettingsPanel()
        {
            if (_settingsDialog == null) return;
            _settingsDialog.RefreshSetup(new SetupPanelState {
                Visible = !_setup.HasVerifiedProfile || _setup.Preparing,
                Checking = _setup.Checking, Preparing = _setup.Preparing, Ready = _setup.IsReady,
                CanPrepare = _migrationDone && !_session.IsBusy && !_exiting && _setup.CanPrepare,
                Detail = SetupDetail(), Diagnostics = _setup.Diagnostics });
        }
        private void ShowSettings()
        {
            if (_exiting || _session.IsBusy || !_migrationDone) return;
            if (_settingsDialog != null) { _settingsDialog.Activate(); return; }
            _hotkeyEditing = true; UpdateMenuState();
            _session.SettingsKey += ObserveSettingsKey;
            // Explicitly opening the panel also counts as seeing the introduction.
            _setupPrompts.MarkIntroductionShown();
            if (_setup.HasSettledResult) _setupPrompts.AcknowledgeVisibleState(_setup.IsReady, SetupPromptPolicy.AttentionKey(_setup.Result, _setup.LastPreparationError));
            SavePromptHistory();
            try {
                using (var dialog = new SettingsDialog(_text,
                    new SettingsValues { TimeoutMinutes = _timeoutMinutes, Hotkey = _session.Hotkey, Language = _text.English ? "en" : "zh-CN", Startup = IsStartupEnabled(), AutomaticUpdateChecks = _autoUpdatesEnabled },
                    SaveSettings, StartAutomaticSetup, _setup.CancelPreparation,
                    delegate { Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true }); },
                    delegate { CheckForUpdates(false); }, OpenUpdateRelease, UpdateChecker.GetCurrentVersion().ToString(3))) {
                    _settingsDialog = dialog; RefreshSettingsPanel(); RefreshUpdatePanel(); _pendingUpdateNotice = false; dialog.ShowDialog();
                }
            } finally {
                _session.SettingsKey -= ObserveSettingsKey;
                _settingsDialog = null; _hotkeyEditing = false;
                if (!_exiting) {
                    string language = Settings.Default.Language;
                    if (_text.English != (language == "en")) { _text = new UiText(language); RebuildMenu(); }
                    UpdateMenuState(); ObserveSetupPresentation();
                }
            }
        }
        private void ObserveSettingsKey(int key, uint modifiers, bool up)
        {
            SettingsDialog target = _settingsDialog;
            if (target == null || target.IsDisposed) return;
            Post(delegate { if (_settingsDialog == target && !target.IsDisposed) target.ObservePhysicalKey(key, modifiers, up); });
        }
        private void StartAutomaticSetup()
        {
            try {
                if (_session.IsBusy || _exiting || !_migrationDone || _setup.Preparing) return;
                ReleaseIdlePowerRequest(); _setup.AutoPrepare();
            } finally {
                // A fresh read-only check can reject a start after the button's
                // snapshot. Always acknowledge the actual state to clear the
                // panel's optimistic pending flag; do not invent recovery success.
                RefreshSettingsPanel(); UpdateMenuState();
            }
        }
        internal static uint TickDelta(uint now, uint previous) { return unchecked(now - previous); }
        internal static bool ShouldTrigger(bool paused, bool alreadyTriggered, uint idleMilliseconds, int timeoutMinutes, bool displayRequired)
        {
            if (paused || alreadyTriggered || displayRequired || timeoutMinutes < MinimumTimeoutMinutes || timeoutMinutes > MaximumTimeoutMinutes) return false;
            return idleMilliseconds >= (uint)timeoutMinutes * 60U * 1000U;
        }
        internal static bool ShouldPrepareKeepAwake(uint idleMilliseconds, int timeoutMinutes)
        {
            if (timeoutMinutes < MinimumTimeoutMinutes || timeoutMinutes > MaximumTimeoutMinutes) return false;
            return idleMilliseconds >= (uint)timeoutMinutes * 60U * 1000U - KeepAwakeLeadMilliseconds;
        }
        internal static bool CanPollIdle(bool supported, bool sessionBusy, bool exiting) { return supported && !sessionBusy && !exiting; }
        private void RefreshAutomaticTiming()
        {
            if (_exiting || _session.IsBusy) return;
            uint executionState;
            bool known = NativeMethods.TryGetSystemExecutionState(out executionState);
            _autoTimingState = _autoTiming.Evaluate(_session.PhysicalIdleMilliseconds, _paused,
                known && (executionState & NativeMethods.ES_DISPLAY_REQUIRED) != 0U, known);
            LogAutomaticTimingState();
        }
        private void AutomaticProtectionObserved(AutoOffPauseReason reason)
        {
            // Preserve a protection seen after the last timer sample. It may
            // have ended already by the next sample, but must still restart N.
            if (reason != AutoOffPauseReason.DisplayRequired && reason != AutoOffPauseReason.StateUnknown) return;
            _autoTimingState = _autoTiming.Evaluate(_session.PhysicalIdleMilliseconds, _paused,
                reason == AutoOffPauseReason.DisplayRequired, reason != AutoOffPauseReason.StateUnknown);
            ReleaseIdlePowerRequest(); LogAutomaticTimingState(); UpdateMenuState();
        }
        private void LogAutomaticTimingState()
        {
            // Only transitions are logged, never individual key/mouse activity.
            if (_lastLoggedAutoPause != _autoTimingState.PauseReason) {
                _lastLoggedAutoPause = _autoTimingState.PauseReason;
                OledPrototype.Program.Log("automatic_off_state; reason=" + _autoTimingState.PauseReason);
            }
        }
        private void CheckIdleState()
        {
            // Session owns recovery while preparing/off/restoring/draining.
            if (_hotkeyEditing || _setup.Preparing || !CanPollIdle(_displayOffSupported, _session.IsBusy, _exiting)) return;
            if (!_session.CanStartSession) { ReleaseIdlePowerRequest(); return; }
            if (!_migrationDone || !_setup.IsReady) {
                if (!_migrationDone || !_setup.HasVerifiedProfile) ReleaseIdlePowerRequest();
                return;
            }
            uint physicalVersion = _session.PhysicalInputVersion;
            if (_lastPhysicalInputVersion != physicalVersion) {
                _lastPhysicalInputVersion = physicalVersion;
                ReleaseIdlePowerRequest(); _triggeredForIdlePeriod = false;
            }
            if (_autoTimingState.Blocked) { ReleaseIdlePowerRequest(); return; }
            if (_triggeredForIdlePeriod || _pendingTimeoutMinutes.HasValue) return;
            uint idleMilliseconds = _session.EffectiveIdle(Math.Min(
                _autoTimingState.IdleMilliseconds, _session.PhysicalIdleMilliseconds));
            if (!_keepAwakeRequested && ShouldPrepareKeepAwake(idleMilliseconds, _timeoutMinutes)) _keepAwakeRequested = NativeMethods.TryKeepSystemAwake();
            if (ShouldTrigger(_paused, _triggeredForIdlePeriod, idleMilliseconds, _timeoutMinutes, false) && TryStartDisplayOffSession(true)) _triggeredForIdlePeriod = true;
        }
        private bool TryStartDisplayOffSession(bool automatic)
        { return !_exiting && !_hotkeyEditing && !_pendingTimeoutMinutes.HasValue && _displayOffSupported && _migrationDone && _setup.IsReady && _session.CanStartSession && _session.TryBegin(automatic); }
        private void ReleaseIdlePowerRequest()
        {
            if (_session.IsBusy) return;
            if (_keepAwakeRequested) { NativeMethods.AllowSystemSleep(); _keepAwakeRequested = false; }
        }
        private void SessionChanged()
        {
            if (!_session.IsBusy) {
                ReleaseIdlePowerRequest(); _triggeredForIdlePeriod = false;
                if (!_exiting && _pendingTimeoutMinutes.HasValue) {
                    int minutes = _pendingTimeoutMinutes.Value; _pendingTimeoutMinutes = null; SaveTimeoutMinutes(minutes);
                }
                if (!_exiting && _migrationDone) { _setup.Invalidate(); _setup.Recheck(); }
            }
            UpdateMenuState();
        }
        private void TurnOffDisplaysNow()
        {
            if (_session.IsBusy || _exiting) return;
            if (!_displayOffSupported) { MessageBox.Show(LocalizeSession(_displayOffUnavailableReason), _text.Title, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (!_migrationDone || !_setup.IsReady) { ShowSettings(); return; }
            _lastPhysicalInputVersion = _session.PhysicalInputVersion;
            if (TryStartDisplayOffSession(false)) _triggeredForIdlePeriod = true;
            else MessageBox.Show(_text.Pick("关屏未开始：请松开启动时按住的键鼠，并等待当前会话结束。", "Display off did not start. Release held keys and mouse buttons, and wait for the current session to end."), _text.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        private void SetTimeoutMinutes(int minutes)
        {
            if (minutes < MinimumTimeoutMinutes || minutes > MaximumTimeoutMinutes) return;
            _pendingTimeoutMinutes = minutes; ReleaseIdlePowerRequest(); _session.RequestEnd(false);
        }
        private void SaveTimeoutMinutes(int minutes)
        {
            try {
                Settings.Default.TimeoutMinutes = minutes; Settings.Default.Save(); _timeoutMinutes = minutes;
                _autoTiming.ResetCountdown(); RefreshAutomaticTiming();
                _triggeredForIdlePeriod = false; UpdateMenuState();
            } catch (Exception ex) { ShowSaveError(ex); }
        }
        private void ShowSaveError(Exception ex)
        { MessageBox.Show(_text.Pick("保存设置失败：", "Could not save settings: ") + ex.Message, _text.Title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        private string SaveSettings(SettingsValues values)
        {
            if (values == null || values.Hotkey == null || values.TimeoutMinutes < MinimumTimeoutMinutes || values.TimeoutMinutes > MaximumTimeoutMinutes ||
                (values.Language != "zh-CN" && values.Language != "en")) return _text.Pick("请检查分钟数和快捷键。", "Check the timeout and shortcut.");
            if (_session.IsBusy || _setup.Preparing || _exiting || _settingsFailure != null) return _text.Pick("当前无法保存，请等待显示操作完成。", "Wait for display work to finish before saving.");
            try {
                int oldMinutes = Settings.Default.TimeoutMinutes;
                bool oldAutomaticUpdates = Settings.Default.AutomaticUpdateChecks;
                string oldLanguage = Settings.Default.Language, oldHotkey = Settings.Default.ScreenOffHotkey;
                bool startupChanged = values.Startup != IsStartupEnabled();
                object oldStartup = null; RegistryValueKind oldKind = RegistryValueKind.String;
                using (var key = Registry.CurrentUser.OpenSubKey(SettingsMigration.RunKey, false)) {
                    oldStartup = key == null ? null : key.GetValue(SettingsMigration.RunName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (oldStartup != null) oldKind = key.GetValueKind(SettingsMigration.RunName);
                }
                if (startupChanged && oldStartup != null && (!(oldStartup is string) || !SettingsMigration.IsOwnedCommand((string)oldStartup, Application.ExecutablePath)))
                    return _text.Pick("现有开机启动项归属不明，未覆盖。", "The existing startup entry is not owned by this app and was left intact.");
                string reason;
                bool changed = _session.TryChangeHotkey(values.Hotkey, delegate {
                    bool startupWritten = false;
                    try {
                        if (startupChanged) { WriteStartup(oldStartup, oldKind, values.Startup); startupWritten = true; }
                        Settings.Default.TimeoutMinutes = values.TimeoutMinutes; Settings.Default.Language = values.Language;
                        Settings.Default.ScreenOffHotkey = values.Hotkey.Serialize();
                        Settings.Default.AutomaticUpdateChecks = values.AutomaticUpdateChecks; Settings.Default.Save();
                    } catch {
                        Settings.Default.TimeoutMinutes = oldMinutes; Settings.Default.Language = oldLanguage; Settings.Default.ScreenOffHotkey = oldHotkey;
                        Settings.Default.AutomaticUpdateChecks = oldAutomaticUpdates;
                        if (startupWritten) RestoreStartup(values.Startup, oldStartup, oldKind);
                        throw;
                    }
                }, out reason);
                if (!changed) return reason != null && reason.Contains("1409") ? _text.Pick("快捷键已被占用，请换一个组合。原快捷键仍有效。", "That shortcut is in use. Choose another combination; the previous shortcut is still active.") : LocalizeSession(reason);
                if (_timeoutMinutes != values.TimeoutMinutes) _autoTiming.ResetCountdown();
                _timeoutMinutes = values.TimeoutMinutes; _triggeredForIdlePeriod = false;
                RefreshAutomaticTiming();
                _autoUpdatesEnabled = values.AutomaticUpdateChecks;
                if (!_autoUpdatesEnabled) { _pendingUpdateNotice = false; if (_automaticUpdateCheck) CancelUpdateCheck(); }
                UpdateMenuState(); return null;
            } catch (Exception ex) { return _text.Pick("设置未保存：", "Settings were not saved: ") + ex.Message; }
        }
        private static void WriteStartup(object expected, RegistryValueKind expectedKind, bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(SettingsMigration.RunKey)) {
                object current = key.GetValue(SettingsMigration.RunName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (!Object.Equals(current, expected) || (current != null && key.GetValueKind(SettingsMigration.RunName) != expectedKind))
                    throw new InvalidOperationException("开机启动项已变化，请重新打开设置 / Startup entry changed; reopen Settings.");
                if (enabled) key.SetValue(SettingsMigration.RunName, "\"" + Application.ExecutablePath + "\"", RegistryValueKind.String);
                else key.DeleteValue(SettingsMigration.RunName, false);
            }
        }
        private static void RestoreStartup(bool writtenEnabled, object oldValue, RegistryValueKind oldKind)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(SettingsMigration.RunKey)) {
                object expected = writtenEnabled ? "\"" + Application.ExecutablePath + "\"" : null;
                object current = key.GetValue(SettingsMigration.RunName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (!Object.Equals(current, expected) || (current != null && key.GetValueKind(SettingsMigration.RunName) != RegistryValueKind.String))
                    throw new InvalidOperationException("启动项已被外部更改，未回写 / Startup entry changed externally and was left intact.");
                if (oldValue == null) key.DeleteValue(SettingsMigration.RunName, false);
                else key.SetValue(SettingsMigration.RunName, oldValue, oldKind);
            }
        }
        private int LoadTimeoutMinutes()
        {
            try { int value = Settings.Default.TimeoutMinutes; if (value >= MinimumTimeoutMinutes && value <= MaximumTimeoutMinutes) return value; } catch { }
            return DefaultTimeoutMinutes;
        }
        private string LocalizeSession(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            string[] parts = value.Split(new[] { " / " }, StringSplitOptions.None);
            if (parts.Length > 1) return _text.English ? parts[parts.Length - 1] : parts[0];
            return _text.English ? "Display operation needs attention. " + value : value;
        }
        private void UpdateMenuState()
        {
            string status = _exiting ? (_setup.Preparing ? _text.Pick("退出中 · 正在取消准备", "Exiting · cancelling setup") : LocalizeSession(_session.StateText)) :
                !_session.CanStartSession && !_session.IsBusy ? LocalizeSession(_session.StateText) :
                !_displayOffSupported ? _text.Pick("关屏不可用", "Display off unavailable") :
                _session.IsBusy ? LocalizeSession(_session.StateText) :
                _setup.Preparing ? _text.Pick("正在自动准备显示…", "Preparing display settings…") :
                _settingsFailure != null ? _text.Pick("设置无法读取 · 关屏已停用", "Settings unavailable · display off disabled") :
                !_migrationDone ? _text.Pick("正在迁移设置 · 等待选择", "Migrating settings / awaiting choice") :
                !_setup.HasVerifiedProfile ? _text.Pick("关屏未启用", "Display off disabled") :
                AutoOffStatusText.Format(_autoTimingState.PauseReason, _timeoutMinutes, _text.English);
            _view.Update(status, _paused,
                _displayOffSupported && _migrationDone && _setup.IsReady && _session.CanStartSession && !_exiting && !_hotkeyEditing,
                _displayOffSupported && !_setup.Preparing && !_exiting,
                _migrationDone && !_session.IsBusy && !_setup.Preparing && !_exiting, _exiting && !_session.ExitRetryAvailable);
            string tooltip = _text.Title + ": " + status;
            _trayIcon.Text = tooltip.Substring(0, Math.Min(63, tooltip.Length));
        }
        private bool IsStartupEnabled()
        {
            try {
                using (var key = Registry.CurrentUser.OpenSubKey(SettingsMigration.RunKey, false)) {
                    string command = key == null ? null : key.GetValue(SettingsMigration.RunName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    string path; return SettingsMigration.TryExecutable(command, out path) && String.Equals(path, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
                }
            } catch { return false; }
        }
        private void ExitApplication()
        {
            if (_exiting) { _session.RetryExit(); return; }
            OledPrototype.Program.Log("tray_exit_requested; setup_preparing=" + _setup.Preparing + "; session_busy=" + _session.IsBusy);
            _exiting = true; _timer.Stop(); CancelUpdateCheck();
            if (_setup.Preparing) {
                _setup.CancelPreparation();
                if (_setup.Preparing) { UpdateMenuState(); return; }
            }
            ContinueExit();
        }
        private void ContinueExit()
        {
            if (_exitSentToSession) return;
            _exitSentToSession = true;
            try { _setup.Dispose(); } catch (Exception ex) { OledPrototype.Program.Log("exit_setup_dispose_failed: " + ex); }
            try { ReleaseIdlePowerRequest(); } catch (Exception ex) { OledPrototype.Program.Log("exit_idle_request_release_failed: " + ex); }
            _session.RequestEnd(true);
        }
        private async void FinishExit()
        {
            if (_session.IsBusy || _finishingExit) return;
            _finishingExit = true;
            try {
                _exitCleanup.Run(new[] {
                new ExitCleanupStep("display_event", delegate { SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged; }),
                new ExitCleanupStep("setup_recovery_event", delegate { _setup.RecoveryStopConfirmationAvailable -= _session.TrackRecoveryStop; }),
                new ExitCleanupStep("updates", delegate { _updates.Dispose(); }),
                new ExitCleanupStep("timer", delegate { _timer.Dispose(); }),
                new ExitCleanupStep("setup", delegate { _setup.Dispose(); }),
                new ExitCleanupStep("settings_window", delegate { if (_settingsDialog != null) _settingsDialog.Close(); }),
                new ExitCleanupStep("session", delegate { _session.Dispose(); }),
                new ExitCleanupStep("tray_hide", delegate { _trayIcon.Visible = false; }),
                new ExitCleanupStep("tray_icon", delegate { _trayIcon.Dispose(); }),
                new ExitCleanupStep("tray_menu", delegate { _view.Dispose(); })
                }, delegate { OledPrototype.Program.Log("exit_cleanup_completed"); }, OledPrototype.Program.Log);
                Task maintenance = Task.WhenAll(_session.PendingMaintenance, _setup.PendingMaintenance);
                Task finished = await Task.WhenAny(maintenance, Task.Delay(500));
                if (finished == maintenance) await maintenance;
                else {
                    OledPrototype.Program.Log("exit_snapshot_maintenance_pending; recovery_material_preserved");
                    ObserveMaintenanceFault(maintenance);
                }
                // Await a bounded flush while the message loop stays responsive.
                await OledPrototype.Program.FlushLogAsync(250);
            } catch (Exception ex) { OledPrototype.Program.Log("exit_finalization_failed: " + ex); }
            finally {
                try { ExitThread(); }
                catch (Exception ex) {
                    OledPrototype.Program.Log("exit_message_loop_failed: " + ex);
                    try { Application.ExitThread(); } catch (Exception fallback) { OledPrototype.Program.Log("exit_message_loop_fallback_failed: " + fallback); }
                }
            }
        }

        private static void ObserveMaintenanceFault(Task maintenance)
        {
            // The bounded exit wait has expired; observe faults without extending it.
            maintenance.ContinueWith(delegate(Task failed) { OledPrototype.Program.Log("exit_maintenance_failed: " + failed.Exception); },
                System.Threading.CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

        private async void CheckForUpdates(bool automatic)
        {
            if (_exiting || _updateChecking || (automatic && !_autoUpdatesEnabled)) return;
            var cancellation = new System.Threading.CancellationTokenSource();
            _updateCancellation = cancellation; _updateChecking = true; _automaticUpdateCheck = automatic;
            _updateResult = null; _pendingUpdateNotice = false; RefreshUpdatePanel();
            try {
                var result = await _updates.CheckAsync(cancellation.Token);
                if (_exiting || cancellation.IsCancellationRequested || _updateCancellation != cancellation) return;
                _updateResult = automatic && result.Status != UpdateCheckStatus.Available ? null : result;
                _updateChecking = false; RefreshUpdatePanel();
                _pendingUpdateNotice = automatic && result.Status == UpdateCheckStatus.Available;
                NotifyAvailableUpdate();
            } catch (OperationCanceledException) {
                // Cancellation never delays display recovery or exit.
            } catch (Exception) {
                if (!_exiting && !automatic && _updateCancellation == cancellation && _settingsDialog != null)
                    _settingsDialog.RefreshUpdate(false, _text.Pick("检查失败，请稍后重试。", "Could not check for updates. Try again later."), false, true);
            } finally {
                if (_updateCancellation == cancellation) { _updateCancellation = null; _updateChecking = false; }
                cancellation.Dispose();
            }
        }
        private void CancelUpdateCheck()
        {
            var cancellation = _updateCancellation;
            _updateCancellation = null; _updateChecking = false; _updateResult = null;
            if (cancellation != null) cancellation.Cancel();
        }
        private void RefreshUpdatePanel()
        {
            if (_settingsDialog == null || _settingsDialog.IsDisposed) return;
            string detail = ""; bool available = false, failed = false;
            if (_updateChecking) detail = _text.Pick("正在检查…", "Checking…");
            else if (_updateResult != null) {
                switch (_updateResult.Status) {
                    case UpdateCheckStatus.NotConfigured: detail = _text.Pick("更新源尚未配置。", "The update source is not configured yet."); break;
                    case UpdateCheckStatus.UpToDate: detail = _text.Pick("已是最新版本。", "You’re up to date."); break;
                    case UpdateCheckStatus.Available:
                        available = true;
                        detail = _text.Pick("发现新版本 v", "Version ") + _updateResult.LatestVersion.ToString() + _text.Pick("", " is available.");
                        break;
                    case UpdateCheckStatus.NoRelease: detail = _text.Pick("暂未发布可用的正式版本。", "No stable release is available yet."); break;
                    case UpdateCheckStatus.Failed:
                        failed = true; detail = _text.Pick("检查失败，请稍后重试。", "Could not check for updates. Try again later."); break;
                }
            }
            _settingsDialog.RefreshUpdate(_updateChecking, detail, available, failed);
        }
        private void NotifyAvailableUpdate()
        {
            if (!_pendingUpdateNotice || _exiting || !_autoUpdatesEnabled || _updateResult == null) return;
            if (_settingsDialog != null && _settingsDialog.Visible) { _pendingUpdateNotice = false; return; }
            if (_session.IsBusy || _setup.Preparing) return;
            _pendingUpdateNotice = false;
            _trayIcon.ShowBalloonTip(5000, "ScreenOffTray",
                _text.Pick("发现新版本，可从“设置”查看并下载。", "A new version is available. Open Settings to review and download it."), ToolTipIcon.Info);
        }
        private void OpenUpdateRelease()
        {
            if (_exiting || _updateResult == null || _updateResult.Status != UpdateCheckStatus.Available || _updateResult.ReleasePage == null) return;
            try { Process.Start(new ProcessStartInfo(_updateResult.ReleasePage.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception) {
                if (_settingsDialog != null) _settingsDialog.RefreshUpdate(false,
                    _text.Pick("无法打开浏览器，请稍后重试。", "Could not open your browser. Try again later."), true, true);
            }
        }
    }
}
