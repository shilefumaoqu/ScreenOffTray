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
using ScreenOffTray.Playback;

namespace ScreenOffTray
{
    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private const int DefaultTimeoutMinutes = 10, MinimumTimeoutMinutes = 1, MaximumTimeoutMinutes = 1440;
        private const uint KeepAwakeLeadMilliseconds = 30000U;
        private readonly NotifyIcon _trayIcon;
        private readonly IntPtr _hostDpiContext;
        private readonly OledSession _session;
        private readonly DisplaySetupState _setup;
        private TrayMenu _view;
        private readonly Timer _timer;
        private readonly ExitCleanup _exitCleanup = new ExitCleanup();
        private readonly Stopwatch _autoClock = Stopwatch.StartNew();
        private readonly AutoOffTiming _autoTiming;
        private AutoOffTimingResult _autoTimingState = new AutoOffTimingResult(0, AutoOffPauseReason.AudioUnknown);
        private readonly IAudioMonitor _audioMonitor;
        private PlaybackOptions _playbackOptions;
        private string _playbackWhitelistWarning, _playbackLimitWarning;
        private AutoOffPauseReason? _lastLoggedAutoPause;
        private bool _finishingExit;
        private bool _displayOffSupported { get { return _session.Supported; } }
        private string _displayOffUnavailableReason { get { return _session.UnavailableReason; } }
        private UiText _text;
        private SettingsDialog _settingsDialog;
        private FirstRunSetupDialog _firstRunDialog;
        private bool _initialSetupRequested, _initialSetupQueued;
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

        internal TrayApplicationContext(bool initialSetupRequested = false)
        {
            _initialSetupRequested = initialSetupRequested;
            _hostDpiContext = SettingsDpiContext.CaptureCurrent();
            _autoTiming = new AutoOffTiming(delegate { return _autoClock.ElapsedMilliseconds; });
            string language = "zh-CN";
            try { language = Settings.Default.Language; } catch (Exception ex) { _settingsFailure = ex.Message; }
            _text = new UiText(language);
            _updates = UpdateChecker.FromConfiguration();
            try { _autoUpdatesEnabled = Settings.Default.AutomaticUpdateChecks; } catch { _autoUpdatesEnabled = false; }
            _timeoutMinutes = LoadTimeoutMinutes();
            _playbackOptions = LoadPlaybackOptions();
            _audioMonitor = new AudioMonitor();
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
            _session = new OledSession(delegate { return _migrationDone && !_exiting && !_hotkeyEditing && _setup != null && _setup.IsReady; }, binding, ReadAutomaticProtection);
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
            _migrationDone = true;
            if (_initialSetupRequested) QueueInitialSetup();
            _setup.Recheck(); UpdateMenuState();
            if (_autoUpdatesEnabled && _updates.IsConfigured) CheckForUpdates(true);
        }
        private void RebuildMenu()
        {
            var previous = _view;
            _view = new TrayMenu(_text, TurnOffDisplaysNow, TogglePause, ShowSettings, ExitApplication, 1F);
            _view.Menu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs args) {
                if (!_session.IsBusy) RefreshAutomaticTiming();
                if (!_session.IsBusy && !_setup.Preparing && _migrationDone && DateTime.UtcNow >= _nextSetupCheck) {
                    _nextSetupCheck = DateTime.UtcNow.AddSeconds(15); _setup.Recheck();
                }
                UpdateMenuState();
                // Opening the tray only exposes quick actions. The Settings item
                // opens the independent Settings window only when requested.
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
            if (_exiting) { RefreshSettingsPanel(); if (!_setup.Preparing) ContinueExit(); return; }
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
            // Keep a failed read-only check actionable instead of replacing its
            // cause with the generic "prepare first" sentence. Recovery and
            // cancellation messages keep their existing priority.
            if (_setup.HasSettledResult && !_setup.IsReady && _setup.CanPrepare && !_setup.Preparing &&
                !_setup.WaitingForPreviousQuery && String.IsNullOrEmpty(_setup.LastPreparationError) &&
                _setup.Result != null && String.IsNullOrEmpty(_setup.Result.Profile))
                return LocalizeSession(_setup.Result.Detail(_text.English));
            return _setup.Detail(_text.English);
        }
        private void ObserveSetupPresentation()
        {
            if (_firstRunDialog != null && !_firstRunDialog.IsDisposed) {
                if (_setup.HasSettledResult) {
                    _setupPrompts.AcknowledgeVisibleState(_setup.IsReady, SetupPromptPolicy.AttentionKey(_setup.Result, _setup.LastPreparationError));
                    SavePromptHistory();
                }
                return;
            }
            if (_initialSetupRequested) { QueueInitialSetup(); return; }
            if (_settingsDialog != null && _settingsDialog.Visible && _setup.HasSettledResult) {
                _setupPrompts.AcknowledgeVisibleState(_setup.IsReady, SetupPromptPolicy.AttentionKey(_setup.Result, _setup.LastPreparationError));
                SavePromptHistory(); return;
            }
            SetupNotice notice = _setupPrompts.Observe(_setup.HasSettledResult, _setup.IsReady,
                !_migrationDone || _session.IsBusy || _hotkeyEditing || _exiting,
                SetupPromptPolicy.AttentionKey(_setup.Result, _setup.LastPreparationError));
            if (notice == SetupNotice.Introduction || notice == SetupNotice.Attention) {
                _initialSetupRequested = true; QueueInitialSetup();
            } else SavePromptHistory();
        }
        private void QueueInitialSetup()
        {
            if (_initialSetupQueued || _firstRunDialog != null || !_migrationDone || _exiting ||
                _session.IsBusy || _setup.Preparing || _settingsDialog != null) return;
            _initialSetupQueued = true;
            Post(delegate {
                _initialSetupQueued = false;
                if (_initialSetupRequested && !_exiting && !_session.IsBusy && !_setup.Preparing && _settingsDialog == null)
                    ShowInitialSetup();
            });
        }
        private void ShowInitialSetup()
        {
            if (_exiting || !_migrationDone || _session.IsBusy) return;
            if (_firstRunDialog != null && !_firstRunDialog.IsDisposed) {
                using (SettingsDpiContext.EnterUi()) {
                    if (_firstRunDialog.WindowState == FormWindowState.Minimized) _firstRunDialog.WindowState = FormWindowState.Normal;
                    _firstRunDialog.Show(); _firstRunDialog.Activate();
                }
                return;
            }
            FirstRunSetupDialog dialog = null;
            try {
                using (SettingsDpiContext.EnterUi()) {
                    dialog = new FirstRunSetupDialog(_text,
                        delegate { InHostDpi(StartAutomaticSetup); },
                        delegate { InHostDpi(_setup.CancelPreparation); },
                        delegate { InHostDpi(delegate { if (!_setup.Preparing && !_session.IsBusy && !_exiting) _setup.Recheck(); }); },
                        delegate { InHostDpi(delegate { Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true }); }); },
                        delegate { InHostDpi(delegate { Process.Start(new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "帮助.html")) { UseShellExecute = true }); }); },
                        _trayIcon.Icon);
                }
                _firstRunDialog = dialog; _hotkeyEditing = true;
                dialog.FormClosed += InitialSetupClosed;
                dialog.Disposed += InitialSetupDisposed;
                RefreshSettingsPanel(); UpdateMenuState();
                using (SettingsDpiContext.EnterUi()) {
                    // Bring this first-use task forward once without retaining
                    // an always-on-top window over the user's other applications.
                    dialog.TopMost = true;
                    try { dialog.Show(); dialog.Activate(); }
                    finally { if (!dialog.IsDisposed) dialog.TopMost = false; }
                }
                _initialSetupRequested = false;
                _setupPrompts.MarkIntroductionShown();
                if (_setup.HasSettledResult) _setupPrompts.AcknowledgeVisibleState(_setup.IsReady,
                    SetupPromptPolicy.AttentionKey(_setup.Result, _setup.LastPreparationError));
                SavePromptHistory();
                OledPrototype.Program.Log("initial_setup_window_opened; ready=" + _setup.IsReady);
            } catch (Exception ex) {
                _initialSetupRequested = false;
                OledPrototype.Program.Log("initial_setup_window_failed: " + ex);
                try { if (dialog != null) using (SettingsDpiContext.EnterUi()) dialog.Dispose(); }
                finally { ReleaseInitialSetup(dialog); }
                if (!_exiting) _trayIcon.ShowBalloonTip(5000, _text.Title,
                    _text.Pick("首次配置窗口未能打开。请从托盘打开设置，完成显示准备。", "Could not open initial setup. Open Settings from the tray to finish display preparation."), ToolTipIcon.Warning);
            }
        }
        private void InitialSetupClosed(object sender, FormClosedEventArgs e) { ReleaseInitialSetup(sender as FirstRunSetupDialog); }
        private void InitialSetupDisposed(object sender, EventArgs e) { ReleaseInitialSetup(sender as FirstRunSetupDialog); }
        private void ReleaseInitialSetup(FirstRunSetupDialog dialog)
        {
            InHostDpi(delegate {
                if (dialog == null || !Object.ReferenceEquals(dialog, _firstRunDialog)) return;
                dialog.FormClosed -= InitialSetupClosed; dialog.Disposed -= InitialSetupDisposed;
                _firstRunDialog = null; _hotkeyEditing = _settingsDialog != null;
                if (!_exiting) {
                    if (_setup.HasSettledResult) _setupPrompts.AcknowledgeVisibleState(_setup.IsReady,
                        SetupPromptPolicy.AttentionKey(_setup.Result, _setup.LastPreparationError));
                    else _setupPrompts.DeferPendingCheck();
                    SavePromptHistory(); UpdateMenuState();
                }
            });
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
            SettingsDialog target = _settingsDialog;
            FirstRunSetupDialog initial = _firstRunDialog;
            if ((target == null || target.IsDisposed) && (initial == null || initial.IsDisposed)) return;
            var state = new SetupPanelState {
                Visible = !_setup.HasVerifiedProfile || _setup.Preparing,
                Checking = _setup.Checking, Preparing = _setup.Preparing, Ready = _setup.IsReady,
                CanPrepare = _migrationDone && !_session.IsBusy && !_exiting && _setup.CanPrepare,
                Detail = SetupDetail(), Diagnostics = _setup.Diagnostics };
            using (SettingsDpiContext.EnterUi()) {
                if (target != null && !target.IsDisposed) target.RefreshSetup(state);
                if (initial != null && !initial.IsDisposed) initial.RefreshSetup(new FirstRunSetupState {
                    Checking = state.Checking || (!_setup.HasSettledResult && !state.Preparing && _setup.Result == null),
                    Preparing = state.Preparing, Ready = state.Ready, CanPrepare = state.CanPrepare,
                    Detail = state.Detail, Diagnostics = state.Diagnostics,
                    Evidence = _setup.Result == null ? "" : _setup.Result.Zh + "\n" + _setup.Result.En });
            }
        }
        private void InHostDpi(Action action)
        { using (SettingsDpiContext.EnterHost(_hostDpiContext)) action(); }
        private T InHostDpi<T>(Func<T> action)
        { using (SettingsDpiContext.EnterHost(_hostDpiContext)) return action(); }
        private void ShowSettings()
        {
            if (_exiting || _session.IsBusy || !_migrationDone) return;
            if (_initialSetupRequested || (_firstRunDialog != null && !_firstRunDialog.IsDisposed)) { ShowInitialSetup(); return; }
            if (_settingsDialog != null && !_settingsDialog.IsDisposed) {
                using (SettingsDpiContext.EnterUi()) {
                    if (_settingsDialog.WindowState == FormWindowState.Minimized) _settingsDialog.WindowState = FormWindowState.Normal;
                    _settingsDialog.Show(); _settingsDialog.Activate();
                }
                return;
            }
            SettingsDialog dialog = null;
            try {
                var values = new SettingsValues { TimeoutMinutes = _timeoutMinutes, Hotkey = _session.Hotkey, Language = _text.English ? "en" : "zh-CN", Startup = IsStartupEnabled(), AutomaticUpdateChecks = _autoUpdatesEnabled,
                    PlaybackLimitEnabled = _playbackOptions.PlaybackLimitEnabled, PlaybackLimitMinutes = _playbackOptions.PlaybackLimitMinutes };
                string version = UpdateChecker.GetCurrentVersion().ToString(3);
                // Only the view is PMv2. Every synchronous callback re-enters the
                // original host context before touching settings or backend work.
                using (SettingsDpiContext.EnterUi()) {
                    dialog = new SettingsDialog(_text, values,
                        delegate(SettingsValues next) { return InHostDpi(delegate { return SaveSettings(next); }); },
                        delegate { InHostDpi(StartAutomaticSetup); },
                        delegate { InHostDpi(_setup.CancelPreparation); },
                        delegate { InHostDpi(delegate { Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true }); }); },
                        delegate { InHostDpi(delegate { CheckForUpdates(false); }); },
                        delegate { InHostDpi(OpenUpdateRelease); }, version,
                        delegate { return InHostDpi<PlaybackPanelState>(ReadPlaybackPanel); },
                        delegate { InHostDpi(delegate { _audioMonitor.RequestRefresh(); }); },
                        delegate(string[] paths) { return InHostDpi(delegate { return SaveWhitelist(paths); }); });
                }
                _settingsDialog = dialog; _hotkeyEditing = true;
                _session.SettingsKey += ObserveSettingsKey;
                dialog.FormClosed += SettingsWindowClosed;
                dialog.Disposed += SettingsWindowDisposed;
                using (SettingsDpiContext.EnterUi()) dialog.ConfigureWindow(delegate {
                    InHostDpi(delegate { if (!_setup.Preparing && !_session.IsBusy && !_exiting) _setup.Recheck(); });
                }, _trayIcon.Icon);
                UpdateMenuState(); RefreshSettingsPanel(); RefreshUpdatePanel(); _pendingUpdateNotice = false;
                // A separate modeless window keeps the tray message loop available.
                // The editor gate stays held until this specific window closes.
                using (SettingsDpiContext.EnterUi()) dialog.Show();
                _setupPrompts.MarkIntroductionShown();
                if (_setup.HasSettledResult) _setupPrompts.AcknowledgeVisibleState(_setup.IsReady, SetupPromptPolicy.AttentionKey(_setup.Result, _setup.LastPreparationError));
                SavePromptHistory();
            } catch (Exception ex) {
                OledPrototype.Program.Log("settings_window_open_failed: " + ex);
                try { if (dialog != null) using (SettingsDpiContext.EnterUi()) dialog.Dispose(); }
                finally { ReleaseSettingsWindow(dialog); }
                if (!_exiting) _trayIcon.ShowBalloonTip(5000, _text.Title, _text.Pick("设置窗口未能打开，请重试。", "Could not open Settings. Please try again."), ToolTipIcon.Warning);
            }
        }
        private void SettingsWindowClosed(object sender, FormClosedEventArgs e)
        { ReleaseSettingsWindow(sender as SettingsDialog); }
        private void SettingsWindowDisposed(object sender, EventArgs e)
        { ReleaseSettingsWindow(sender as SettingsDialog); }
        private void ReleaseSettingsWindow(SettingsDialog dialog)
        { InHostDpi(delegate { ReleaseSettingsWindowInHost(dialog); }); }
        private void ReleaseSettingsWindowInHost(SettingsDialog dialog)
        {
            if (dialog == null || !Object.ReferenceEquals(dialog, _settingsDialog)) return;
            dialog.FormClosed -= SettingsWindowClosed;
            dialog.Disposed -= SettingsWindowDisposed;
            _session.SettingsKey -= ObserveSettingsKey;
            _settingsDialog = null; _hotkeyEditing = _firstRunDialog != null;
            if (!_exiting) {
                try {
                    string language = Settings.Default.Language;
                    if (_text.English != (language == "en")) { _text = new UiText(language); RebuildMenu(); }
                } catch (Exception ex) { OledPrototype.Program.Log("settings_window_language_refresh_failed: " + ex); }
                UpdateMenuState(); ObserveSetupPresentation();
            }
        }
        private void ObserveSettingsKey(int key, uint modifiers, bool up)
        {
            SettingsDialog target = _settingsDialog;
            if (target == null || target.IsDisposed) return;
            Post(delegate {
                if (_settingsDialog == target && !target.IsDisposed)
                    using (SettingsDpiContext.EnterUi()) target.ObservePhysicalKey(key, modifiers, up);
            });
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
            uint physicalIdle = _session.PhysicalIdleMilliseconds;
            _autoTimingState = _autoTiming.EvaluatePlayback(physicalIdle,
                PlaybackProtection.Classify(_audioMonitor.Latest, _playbackOptions, physicalIdle, _paused));
            LogAutomaticTimingState();
        }
        private PlaybackProtectionState ReadAutomaticProtection()
        {
            return PlaybackProtection.Classify(_audioMonitor.Latest, _playbackOptions,
                _session.PhysicalIdleMilliseconds, _paused);
        }
        private PlaybackPanelState ReadPlaybackPanel()
        {
            return new PlaybackPanelState { Audio = _audioMonitor.Latest, Timing = _autoTimingState,
                PhysicalIdleMilliseconds = _session.PhysicalIdleMilliseconds,
                AppliedOptions = _playbackOptions.Copy(),
                SettingsWarning = String.Join("\r\n", new[] { _playbackWhitelistWarning, _playbackLimitWarning }).Trim() };
        }
        private PlaybackOptions LoadPlaybackOptions()
        {
            var options = new PlaybackOptions();
            try {
                options.PlaybackLimitEnabled = Settings.Default.PlaybackLimitEnabled;
                int minutes = Settings.Default.PlaybackLimitMinutes;
                if (minutes < 1 || minutes > 1440) throw new InvalidDataException("播放保护上限无效 / Invalid playback limit.");
                options.PlaybackLimitMinutes = minutes;
            } catch (Exception ex) { _playbackLimitWarning = ex.Message; }
            try { options.WhitelistPaths = PlaybackSettingsCodec.Deserialize(Settings.Default.PlaybackWhitelistPaths); }
            catch (Exception ex) {
                options.WhitelistKnown = false;
                _playbackWhitelistWarning = "白名单读取失败，自动关屏已暂停；原设置已保留 / Whitelist unavailable. Auto off is paused; saved data preserved: " + ex.Message;
            }
            return options;
        }
        private string SaveWhitelist(string[] paths)
        {
            if (_session.IsBusy || _setup.Preparing || _exiting || _settingsFailure != null)
                return _text.Pick("当前无法保存，请等待显示操作完成。", "Wait for display work to finish before saving.");
            try {
                string[] normalized = PlaybackSettingsPersistence.SaveWhitelist(Settings.Default, paths);
                PlaybackOptions next = _playbackOptions.Copy(); next.WhitelistPaths = normalized; next.WhitelistKnown = true;
                _playbackOptions = next; _playbackWhitelistWarning = null;
                _triggeredForIdlePeriod = false; RefreshAutomaticTiming(); UpdateMenuState();
                return null;
            } catch (Exception ex) { return _text.Pick("白名单未保存：", "Whitelist was not saved: ") + ex.Message; }
        }
        private void AutomaticProtectionObserved(AutoOffPauseReason reason)
        {
            // Preserve a protection seen after the last timer sample. It may
            // have ended already by the next sample, but must still restart N.
            if (reason != AutoOffPauseReason.Playback && reason != AutoOffPauseReason.AudioUnknown &&
                reason != AutoOffPauseReason.PlaybackSettingsUnknown && reason != AutoOffPauseReason.UserPause) return;
            _autoTimingState = _autoTiming.EvaluatePlayback(_session.PhysicalIdleMilliseconds,
                new PlaybackProtectionState(_paused ? AutoOffPauseReason.UserPause : reason, false, null));
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
                values.PlaybackLimitMinutes < 1 || values.PlaybackLimitMinutes > 1440 ||
                (values.Language != "zh-CN" && values.Language != "en")) return _text.Pick("请检查分钟数和快捷键。", "Check the timeout and shortcut.");
            if (_session.IsBusy || _setup.Preparing || _exiting || _settingsFailure != null) return _text.Pick("当前无法保存，请等待显示操作完成。", "Wait for display work to finish before saving.");
            try {
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
                        PlaybackSettingsPersistence.SaveForm(Settings.Default, values.TimeoutMinutes, values.Language,
                            values.Hotkey.Serialize(), values.AutomaticUpdateChecks,
                            values.PlaybackLimitEnabled, values.PlaybackLimitMinutes);
                    } catch {
                        if (startupWritten) RestoreStartup(values.Startup, oldStartup, oldKind);
                        throw;
                    }
                }, out reason);
                if (!changed) return reason != null && reason.Contains("1409") ? _text.Pick("快捷键已被占用，请换一个组合。原快捷键仍有效。", "That shortcut is in use. Choose another combination; the previous shortcut is still active.") : LocalizeSession(reason);
                if (_timeoutMinutes != values.TimeoutMinutes) _autoTiming.ResetCountdown();
                _timeoutMinutes = values.TimeoutMinutes; _triggeredForIdlePeriod = false;
                PlaybackOptions nextPlayback = _playbackOptions.Copy();
                nextPlayback.PlaybackLimitEnabled = values.PlaybackLimitEnabled;
                nextPlayback.PlaybackLimitMinutes = values.PlaybackLimitMinutes;
                _playbackOptions = nextPlayback; _playbackLimitWarning = null;
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
                _hotkeyEditing && !_paused ? _text.Pick("设置中 · 自动关屏暂缓", "Settings open · auto off paused") :
                AutoOffStatusText.Format(_autoTimingState, _timeoutMinutes, _text.English);
            _view.Update(status, _paused,
                _displayOffSupported && _migrationDone && _setup.IsReady && _session.CanStartSession && !_exiting && !_hotkeyEditing,
                _displayOffSupported && !_setup.Preparing && !_exiting,
                _migrationDone && !_session.IsBusy && (!_setup.Preparing || _settingsDialog != null || _firstRunDialog != null) && !_exiting, _exiting && !_session.ExitRetryAvailable);
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
                new ExitCleanupStep("audio_monitor", delegate { _audioMonitor.Dispose(); }),
                new ExitCleanupStep("timer", delegate { _timer.Dispose(); }),
                new ExitCleanupStep("setup", delegate { _setup.Dispose(); }),
                new ExitCleanupStep("initial_setup_window", delegate {
                    FirstRunSetupDialog dialog = _firstRunDialog;
                    if (dialog != null) {
                        RefreshSettingsPanel();
                        if (!dialog.IsDisposed) using (SettingsDpiContext.EnterUi()) dialog.Close();
                    }
                }),
                new ExitCleanupStep("settings_window", delegate {
                    SettingsDialog dialog = _settingsDialog;
                    if (dialog != null) {
                        // Publish the actual settled setup state before closing:
                        // the view must not wait on an obsolete preparation flag.
                        RefreshSettingsPanel();
                        if (!dialog.IsDisposed) using (SettingsDpiContext.EnterUi()) dialog.Close();
                    }
                }),
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
                    using (SettingsDpiContext.EnterUi()) _settingsDialog.RefreshUpdate(false,
                        _text.Pick("检查失败，请稍后重试。", "Could not check for updates. Try again later."), false, true);
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
            using (SettingsDpiContext.EnterUi()) _settingsDialog.RefreshUpdate(_updateChecking, detail, available, failed);
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
                if (_settingsDialog != null) using (SettingsDpiContext.EnterUi()) _settingsDialog.RefreshUpdate(false,
                    _text.Pick("无法打开浏览器，请稍后重试。", "Could not open your browser. Try again later."), true, true);
            }
        }
    }
}
