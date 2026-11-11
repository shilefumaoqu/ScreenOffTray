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
    internal sealed partial class TrayApplicationContext : ApplicationContext
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
        private readonly ManualOffRequest _manualOff;
        private AudioSnapshot _lastAudio;
        private int _audioChangeQueued;
        private long _snapshotVersion;
        private RuntimeSnapshot _runtimeSnapshot;
        private bool _savingSettings, _savingPromptHistory, _drainingSettings, _settingsDrained, _sessionExitReady;
        private AutoOffTimingResult _autoTimingState = new AutoOffTimingResult(0, AutoOffPauseReason.AudioUnknown);
        private readonly IAudioMonitor _audioMonitor;
        private PlaybackOptions _playbackOptions;
        private string _playbackWhitelistWarning, _playbackLimitWarning;
        private AutoOffPauseReason? _lastLoggedAutoPause;
        private bool _finishingExit;
        private bool _displayOffSupported { get { return _session.Supported; } }
        private string _displayOffUnavailableReason { get { return _session.UnavailableReason; } }
        private UiText _text;
        private bool _initialSetupRequested, _initialSetupQueued;
        private readonly SetupPromptPolicy _setupPrompts;
        private bool _migrationDone, _hotkeyEditing, _historySaveWarning;
        private string _hotkeyLoadWarning;
        private string _settingsFailure;
        private long _nextSetupCheck;
        private int _timeoutMinutes;
        private bool _paused, _triggeredForIdlePeriod, _keepAwakeRequested, _exiting;
        private uint _lastPhysicalInputVersion;
        private readonly UpdateChecker _updates;
        private System.Threading.CancellationTokenSource _updateCancellation;
        private UpdateCheckResult _updateResult;
        private bool _autoUpdatesEnabled = true, _updateChecking, _automaticUpdateCheck, _pendingUpdateNotice;

        internal TrayApplicationContext(bool initialSetupRequested = false,Action<bool,bool> startupConfirmation=null)
        {
            _initialSetupRequested = initialSetupRequested;_forceStartupGuide=initialSetupRequested;_startupConfirmation=startupConfirmation;
            _autoTiming = new AutoOffTiming(delegate { return _autoClock.ElapsedMilliseconds; });
            _manualOff = new ManualOffRequest(delegate { return _autoClock.ElapsedMilliseconds; });
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
            _session = new OledSession(delegate { return _migrationDone && !_exiting && !_savingSettings && _settingsFailure==null && !_hotkeyEditing && _setup != null && _setup.IsReady; }, binding, ReadAutomaticProtection);
            _setup = new DisplaySetupState(Post, PostSetupCompletion);
            _setup.RecoveryStopConfirmationAvailable += _session.TrackRecoveryStop;
            _session.ManualRequested = TurnOffDisplaysNow;
            _session.PulseObserved = delegate { if(_exiting)TryFinishExit();else PollManualRequest(); };
            _session.PhysicalObserved = delegate(InputKind kind,int id,bool injected) {
                if(_manualOff.Observe(kind,id,injected)) Post(delegate { CompleteManualRequest(OffRequestResult.Cancelled); });
            };
            ((AudioMonitor)_audioMonitor).Changed += AudioChanged;
            _trayIcon = new NotifyIcon { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application, Visible = true };
            RebuildMenu();
            _session.Changed += SessionChanged;
            _session.AutomaticProtectionObserved += AutomaticProtectionObserved;
            _session.Problem += delegate(string message) { _trayIcon.ShowBalloonTip(5000, _text.Title, LocalizeSession(message), ToolTipIcon.Warning); };
            _session.ExitReady += delegate {_sessionExitReady=true;TryFinishExit();};
            _setup.Changed += SetupChanged;
            SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
            _timer = new Timer { Interval = 5000 };
            _timer.Tick += delegate {
                RefreshAutomaticTiming();
                if (!_session.IsBusy && !_setup.Preparing && !_exiting && _migrationDone && _autoClock.ElapsedMilliseconds >= _nextSetupCheck) {
                    _nextSetupCheck = _autoClock.ElapsedMilliseconds+15000; _setup.Recheck();
                }
                CheckIdleState();
                UpdateMenuState();
            };
            _timer.Tick += delegate { NotifyAvailableUpdate(); };
            RefreshAutomaticTiming(); _timer.Start(); UpdateMenuState();
            if (_hotkeyLoadWarning != null) Post(delegate { _trayIcon.ShowBalloonTip(5000, _text.Title, _hotkeyLoadWarning, ToolTipIcon.Warning); });
            Task.Run(delegate {
                SettingsMigrationResult migration = SettingsMigration.Begin();
                bool startupEnabled=false;
                try {startupEnabled=ReadStartupForSave(false).Enabled;}
                catch(Exception ex){migration.CanFinish=false;migration.Warning="无法读取开机启动状态 / Startup status unavailable: "+ex.Message;}
                Post(delegate { _startupEnabled=startupEnabled;FinishMigration(migration); });
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
        private void RebuildMenu()
        {
            var previous = _view;
            _view = new TrayMenu(_text, TurnOffDisplaysNow, TogglePause, ShowSettings, ExitApplication, 1F);
            _view.Menu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs args) {
                if (!_session.IsBusy) RefreshAutomaticTiming();
                if (!_session.IsBusy && !_setup.Preparing && _migrationDone && _autoClock.ElapsedMilliseconds >= _nextSetupCheck) {
                    _nextSetupCheck = _autoClock.ElapsedMilliseconds+15000; _setup.Recheck();
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
            PollManualRequest();
            if (_exiting) { RefreshSettingsPanel(); if (!_setup.Preparing) ContinueExit(); return; }
            // A refresh temporarily prevents a new session, but does not invalidate
            // an already verified profile or release its existing idle power hold.
            if (!_setup.HasVerifiedProfile) ReleaseIdlePowerRequest();
            RefreshAutomaticTiming();
            UpdateMenuState();
            RefreshSettingsPanel();
            ObserveSetupPresentation();
            if (!_exiting && !_session.IsBusy) CheckIdleState();
        }
        private void DisplaySettingsChanged(object sender, EventArgs e)
        {
            Post(delegate {
                if (_session.IsBusy || _setup.Preparing || _exiting) return;
                _setup.Invalidate(); if (_migrationDone) _setup.Recheck();
            });
        }
        private string SetupDetail(bool english)
        {
            // Keep a failed read-only check actionable instead of replacing its
            // cause with the generic "prepare first" sentence. Recovery and
            // cancellation messages keep their existing priority.
            string detail;
            if (_setup.HasSettledResult && !_setup.IsReady && _setup.CanPrepareDisplay && !_setup.Preparing &&
                !_setup.WaitingForPreviousQuery && String.IsNullOrEmpty(_setup.LastPreparationError) &&
                _setup.Result != null && String.IsNullOrEmpty(_setup.Result.Profile))
                detail = LocalizeSession(_setup.Result.Detail(english),english);
            else detail = _setup.Detail(english);
            if (_setup.CanPrepareDisplay && !_setup.IsReady && String.IsNullOrEmpty(_setup.LastPreparationError))
                detail += new UiText(english ? "en" : "zh-CN").Pick("\r\n每次准备都会重新识别，并按需匹配虚拟屏缩放；期间可能闪屏，补充模式时可能需要管理员确认。", "\r\nEach attempt rechecks the displays and matches virtual scaling if needed. The screen may flicker; adding a mode may require administrator confirmation.");
            return detail;
        }
        private bool StartAutomaticSetup()
        {
            try {
                if (_savingSettings || _settingsFailure!=null || _session.IsBusy || _exiting || !_migrationDone || _setup.Preparing) return false;
                ReleaseIdlePowerRequest();return _setup.PrepareDisplay();
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
        private void AudioChanged()
        {
            if(System.Threading.Interlocked.Exchange(ref _audioChangeQueued,1)!=0)return;
            Post(delegate { System.Threading.Interlocked.Exchange(ref _audioChangeQueued,0);
                if(_exiting)return; RefreshAutomaticTiming(); UpdateMenuState();
                PublishUiState();
            });
        }
        private void PollManualRequest()
        {
            if(_exiting) { _manualOff.Cancel(); return; }
            var result=_manualOff.Poll(_migrationDone && _setup.IsReady && !_hotkeyEditing,_setup.Checking,delegate { return TryStartDisplayOffSession(false); });
            if(result.HasValue) CompleteManualRequest(result.Value);
        }
        private void CompleteManualRequest(OffRequestResult result)
        {
            if(result==OffRequestResult.Accepted) _triggeredForIdlePeriod=true;
            else if(result==OffRequestResult.Cancelled)
                _trayIcon.ShowBalloonTip(3000,_text.Title,_text.Pick("关屏已取消：检测到新的实体操作。","Display off cancelled by new physical input."),ToolTipIcon.Info);
            else if(result==OffRequestResult.TimedOut || result==OffRequestResult.Rejected)
                _trayIcon.ShowBalloonTip(4000,_text.Title,_text.Pick("关屏未开始：显示核验未完成或当前不可用，请稍后重试。", "Display off did not start. Display verification is incomplete or unavailable; please retry."),ToolTipIcon.Warning);
            UpdateMenuState();
        }
        private void RefreshAutomaticTiming()
        {
            if (_exiting || _session.IsBusy) return;
            uint physicalIdle = _session.PhysicalIdleMilliseconds;
            _lastAudio = _audioMonitor.Latest;
            _autoTimingState = _autoTiming.EvaluatePlayback(physicalIdle,
                PlaybackProtection.Classify(_lastAudio, _playbackOptions, physicalIdle, _paused));
            LogAutomaticTimingState();
        }
        private PlaybackProtectionState ReadAutomaticProtection()
        {
            uint idle=_session.PhysicalIdleMilliseconds;_lastAudio=_audioMonitor.Latest;
            var state=PlaybackProtection.Classify(_lastAudio,_playbackOptions,idle,_paused);
            _autoTimingState=_autoTiming.EvaluatePlayback(idle,state);
            return state;
        }
        private PlaybackPanelState ReadPlaybackPanel()
        {
            return _runtimeSnapshot==null ? new PlaybackPanelState {Audio=_lastAudio,AppliedOptions=_playbackOptions.Copy(),Timing=_autoTimingState} : _runtimeSnapshot.PlaybackView();
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
        private async Task<string> SaveWhitelistAsync(string[] paths)
        {
            if(_savingWhitelist)return "白名单正在保存，请稍候 / Allowlist is saving";
            _savingWhitelist=true;UpdateMenuState();
            try{return await SaveWhitelistTransactionAsync(paths);}
            finally{_savingWhitelist=false;if(!_finishingExit)UpdateMenuState();}
        }
        private async Task<string> SaveWhitelistTransactionAsync(string[] paths)
        {
            if(!_playbackOptions.WhitelistKnown)return "白名单无法读取，原数据已保留，请修复后重试。 / Allowlist could not be read; existing data was preserved.";
            if (_session.IsBusy || _setup.Preparing || _exiting || _settingsFailure != null)
                return "当前无法保存，请等待显示操作完成。 / Wait for display work to finish before saving.";
            try {
                string[] normalized = PlaybackSettingsCodec.Normalize(paths);
                string json=PlaybackSettingsCodec.Serialize(normalized);
                await SerializedSettingsWriter.Default.UpdateAsync(delegate {Settings.Default.PlaybackWhitelistPaths=json;});
                if(_exiting)return null;
                PlaybackOptions next = _playbackOptions.Copy(); next.WhitelistPaths = normalized; next.WhitelistKnown = true;
                _playbackOptions = next; _playbackWhitelistWarning = null;
                _triggeredForIdlePeriod = false; RefreshAutomaticTiming(); UpdateMenuState();
                return null;
            } catch (Exception ex) { if(ex is AggregateException){_settingsFailure=ex.Message;UpdateMenuState();} return "白名单未保存 / Whitelist was not saved" + "\r\n" + ex.Message; }
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
            if (!_session.CanStartSession || _settingsFailure!=null || _savingSettings) { ReleaseIdlePowerRequest(); return; }
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
            if (_triggeredForIdlePeriod) return;
            uint idleMilliseconds = _session.EffectiveIdle(Math.Min(
                _autoTimingState.IdleMilliseconds, _session.PhysicalIdleMilliseconds));
            if (!_keepAwakeRequested && ShouldPrepareKeepAwake(idleMilliseconds, _timeoutMinutes)) _keepAwakeRequested = NativeMethods.TryKeepSystemAwake();
            if (ShouldTrigger(_paused, _triggeredForIdlePeriod, idleMilliseconds, _timeoutMinutes, false) && TryStartDisplayOffSession(true)) _triggeredForIdlePeriod = true;
        }
        private bool TryStartDisplayOffSession(bool automatic)
        { return !_exiting && !_savingSettings && _settingsFailure==null && !_hotkeyEditing && _displayOffSupported && _migrationDone && _setup.IsReady && _session.CanStartSession && _session.TryBegin(automatic); }
        private void ReleaseIdlePowerRequest()
        {
            if (_session.IsBusy) return;
            if (_keepAwakeRequested) { NativeMethods.AllowSystemSleep(); _keepAwakeRequested = false; }
        }
        private void SessionChanged()
        {
            if (!_session.IsBusy) {
                ReleaseIdlePowerRequest(); _triggeredForIdlePeriod = false;
                if (!_exiting && _migrationDone) { _setup.Invalidate(); _setup.Recheck(); }
            }
            UpdateMenuState();
        }
        private void TurnOffDisplaysNow()
        {
            if (_session.IsBusy || _exiting || _hotkeyEditing) return;
            if (!_displayOffSupported) { MessageBox.Show(LocalizeSession(_displayOffUnavailableReason), _text.Title, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            _lastPhysicalInputVersion = _session.PhysicalInputVersion;
            CompleteManualRequest(_manualOff.Begin(_migrationDone && _setup.IsReady && !_hotkeyEditing,
                _migrationDone && _setup.Checking && !_hotkeyEditing,delegate { return TryStartDisplayOffSession(false); },_manualOff.HeldGesture()));
        }
        internal static bool NeedsShortcutReservation(HotkeyBinding applied,HotkeyBinding requested,bool registered,bool hooksAvailable,bool edited)
        {return !applied.SameAs(requested) || (edited && !registered && hooksAvailable);}
        private async Task<string> SaveSettingsAsync(SettingsValues values)
        {
            if(_savingSettings)return "正在保存，请稍候 / Saving; please wait.";
            _savingSettings=true;UpdateMenuState();
            try {return await SaveSettingsTransactionAsync(values);}
            finally {_savingSettings=false;if(!_finishingExit)UpdateMenuState();if(_exiting)ContinueExit();}
        }
        private async Task<string> SaveSettingsTransactionAsync(SettingsValues values)
        {
            string interfaceTheme=values == null ? "geometry" : (values.InterfaceTheme ?? "geometry").Trim().ToLowerInvariant();
            if (interfaceTheme != "geometry" && interfaceTheme != "warm" && interfaceTheme != "black") interfaceTheme="geometry";
            if (values != null) values.InterfaceTheme=interfaceTheme;
            if (values == null || values.Hotkey == null || values.TimeoutMinutes < MinimumTimeoutMinutes || values.TimeoutMinutes > MaximumTimeoutMinutes ||
                values.PlaybackLimitMinutes < 1 || values.PlaybackLimitMinutes > 1440 ||
                (values.Language != "zh-CN" && values.Language != "en")) return "请检查分钟数和快捷键。 / Check the timeout and shortcut.";
            if (!_migrationDone || _session.IsBusy || _setup.Preparing || _exiting || _settingsFailure != null) return "当前无法保存，请等待显示操作完成。 / Wait for display work to finish before saving.";
            Exception transactionFailure=null;
            HotkeyRegistration.Reservation reservation=null;
            bool startupWritten=false; object rollbackStartup=null; RegistryValueKind rollbackKind=RegistryValueKind.String;
            try {
                var startup=await Task.Run(delegate {return ReadStartupForSave(values.Startup);});
                if(_exiting)return "程序正在退出，设置未保存 / Application is exiting; settings were not saved";
                if(!startup.ReplaceAllowed)return "现有开机启动项归属不明，未覆盖。 / The existing startup entry is not owned by this app and was left intact.";
                bool startupChanged=startup.Changed;object oldStartup=startup.Value;RegistryValueKind oldKind=startup.Kind;
                rollbackStartup=oldStartup;rollbackKind=oldKind;
                string reason;
                if(NeedsShortcutReservation(_session.Hotkey,values.Hotkey,_session.HotkeyRegistered,_session.HooksAvailable,values.HotkeyEdited) && !_session.TryReserveHotkey(values.Hotkey,out reservation,out reason))
                    return (_session.HotkeyRegistered?"快捷键未更改，原快捷键仍有效 / Hotkey unchanged: ":"当前快捷键尚未注册，请重选组合 / Shortcut is unregistered; choose another combination: ")+reason;
                await SerializedSettingsWriter.Default.UpdateAsync(delegate {
                    try {
                        if(startupChanged) {WriteStartup(oldStartup,oldKind,values.Startup);startupWritten=true;}
                        Settings.Default.TimeoutMinutes=values.TimeoutMinutes;Settings.Default.Language=values.Language;
                        Settings.Default.ScreenOffHotkey=values.Hotkey.Serialize();Settings.Default.AutomaticUpdateChecks=values.AutomaticUpdateChecks;
                        Settings.Default.PlaybackLimitEnabled=values.PlaybackLimitEnabled;Settings.Default.PlaybackLimitMinutes=values.PlaybackLimitMinutes;
                        Settings.Default.InterfaceTheme=values.InterfaceTheme;
                    } catch { throw; }
                });
                if(reservation!=null)reservation.Commit();
                _session.RefreshHotkeyAvailability();
                if (_timeoutMinutes != values.TimeoutMinutes) _autoTiming.ResetCountdown();
                _timeoutMinutes = values.TimeoutMinutes; _startupEnabled=values.Startup;
                if(_text.English!=(values.Language=="en")){_text=new UiText(values.Language);RebuildMenu();}
                _triggeredForIdlePeriod = false;
                PlaybackOptions nextPlayback = _playbackOptions.Copy();
                nextPlayback.PlaybackLimitEnabled = values.PlaybackLimitEnabled;
                nextPlayback.PlaybackLimitMinutes = values.PlaybackLimitMinutes;
                _playbackOptions = nextPlayback; _playbackLimitWarning = null;
                RefreshAutomaticTiming();
                _autoUpdatesEnabled = values.AutomaticUpdateChecks;
                if (!_autoUpdatesEnabled) { _pendingUpdateNotice = false; if (_automaticUpdateCheck) CancelUpdateCheck(); }
                UpdateMenuState(); return null;
            } catch (Exception ex) {transactionFailure=ex;}
            finally {if(reservation!=null)reservation.Dispose();}
            if(transactionFailure is AggregateException) _settingsFailure=transactionFailure.Message;
            if(startupWritten) {
                try {await SerializedSettingsWriter.Default.Enqueue(delegate {RestoreStartup(values.Startup,rollbackStartup,rollbackKind);});}
                catch(Exception rollback) {_settingsFailure=rollback.Message;return "设置和开机启动回退失败 / Settings/startup rollback failed: "+rollback.Message;}
            }
            return "设置未保存 / Settings were not saved"+"\r\n"+transactionFailure.Message;
        }
        private sealed class StartupSnapshot
        {internal object Value;internal RegistryValueKind Kind=RegistryValueKind.String;internal bool Enabled,Changed,ReplaceAllowed;}
        private static StartupSnapshot ReadStartupForSave(bool requested)
        {
            var value=new StartupSnapshot();
            using(var key=Registry.CurrentUser.OpenSubKey(SettingsMigration.RunKey,false)) {
                value.Value=key==null?null:key.GetValue(SettingsMigration.RunName,null,RegistryValueOptions.DoNotExpandEnvironmentNames);
                if(value.Value!=null)value.Kind=key.GetValueKind(SettingsMigration.RunName);
            }
            string path;bool enabled=SettingsMigration.TryExecutable(value.Value as string,out path) && String.Equals(path,Application.ExecutablePath,StringComparison.OrdinalIgnoreCase);
            value.Enabled=enabled;value.Changed=requested!=enabled;
            value.ReplaceAllowed=!value.Changed || value.Value==null || (value.Value is string && SettingsMigration.IsOwnedCommand((string)value.Value,Application.ExecutablePath));
            return value;
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
        { return LocalizeSession(value,_text.English); }
        private static string LocalizeSession(string value,bool english)
        {
            if (String.IsNullOrEmpty(value)) return "";
            string[] parts = value.Split(new[] { " / " }, StringSplitOptions.None);
            if (parts.Length > 1) return english ? parts[parts.Length - 1] : parts[0];
            return english ? "Display operation needs attention. " + value : value;
        }
        private void UpdateMenuState()
        {
            if(_finishingExit || _session.IsDisposed || _view==null)return;
            string status = !_exiting && _manualOff.Pending ? _text.Pick("正在核验显示条件…", "Verifying display conditions…") : _exiting ? (_setup.Preparing ? _text.Pick("退出中 · 正在取消准备", "Exiting · cancelling setup") : LocalizeSession(_session.StateText)) :
                !_session.CanStartSession && !_session.IsBusy ? LocalizeSession(_session.StateText) :
                !_displayOffSupported ? _text.Pick("关屏不可用", "Display off unavailable") :
                _session.IsBusy ? LocalizeSession(_session.StateText) :
                _setup.Preparing ? _text.Pick("正在自动准备显示…", "Preparing display settings…") :
                _settingsFailure != null ? _text.Pick("设置无法读取 · 关屏已停用", "Settings unavailable · display off disabled") :
                !_migrationDone ? _text.Pick("正在迁移设置 · 等待选择", "Migrating settings / awaiting choice") :
                !_setup.HasVerifiedProfile ? _text.Pick("关屏未启用", "Display off disabled") :
                _hotkeyEditing && !_paused ? _text.Pick("设置中 · 自动关屏暂缓", "Settings open · auto off paused") :
                AutoOffStatusText.Format(_autoTimingState, _timeoutMinutes, _text.English);
            _runtimeSnapshot=new RuntimeSnapshot(++_snapshotVersion,status,_setup.IsReady,_setup.Checking,_setup.Preparing,
                _lastAudio,_playbackOptions,_autoTimingState,_session.PhysicalIdleMilliseconds,
                String.Join("\r\n",new[]{_playbackWhitelistWarning,_playbackLimitWarning,((AudioMonitor)_audioMonitor).AuxiliaryWarning}).Trim());
            _view.Update(status, _paused,
                _displayOffSupported && _migrationDone && _setup.IsReady && _session.CanStartSession && !_exiting && !_hotkeyEditing,
                _displayOffSupported && !_setup.Preparing && !_exiting,
                (_migrationDone || _migrationChoices!=null || _settingsFailure!=null) && !_session.IsBusy && (!_setup.Preparing || _settingsHost!=null) && !_exiting, _exiting && !_session.ExitRetryAvailable);
            string tooltip = _text.Title + ": " + status;
            _trayIcon.Text = tooltip.Substring(0, Math.Min(63, tooltip.Length));
            PublishUiState();CheckStartupConfirmation();
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
            _exiting = true; _manualOff.Cancel(); _timer.Stop(); CancelUpdateCheck();
            QueueExitPromptHistory();
            // Cancellation/restoration must start before any settings IO wait.
            _session.RequestEnd(true);
            if (_setup.Preparing) {
                _setup.CancelPreparation();
                if (_setup.Preparing) { UpdateMenuState(); return; }
            }
            ContinueExit();
        }
        private async void ContinueExit()
        {
            if (_settingsDrained) {TryFinishExit();return;}
            if (_savingSettings || _drainingSettings || _setup.Preparing) return;
            _drainingSettings=true;
            UpdateMenuState();
            try {await SerializedSettingsWriter.Default.SealAsync();}
            catch(Exception ex) {OledPrototype.Program.Log("exit_settings_drain_failed: "+ex);_drainingSettings=false;return;}
            _drainingSettings=false;_settingsDrained=true;
            try { _setup.Dispose(); } catch (Exception ex) { OledPrototype.Program.Log("exit_setup_dispose_failed: " + ex); }
            try { ReleaseIdlePowerRequest(); } catch (Exception ex) { OledPrototype.Program.Log("exit_idle_request_release_failed: " + ex); }
            TryFinishExit();
        }
        private void TryFinishExit()
        {if(_exiting && _sessionExitReady && _session.IsSafeToExit && _settingsDrained && !_savingSettings && !_setup.Preparing)FinishExit();}
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
                new ExitCleanupStep("settings_process", delegate {
                    var host=_settingsHost;_settingsHost=null;_session.SettingsKey-=ObserveSettingsKey;
                    if(host!=null){host.Close();host.Dispose();}
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
                if(!_exiting && !automatic && _updateCancellation==cancellation){_updateResult=new UpdateCheckResult(UpdateCheckStatus.Failed,null,null,"Could not check updates");RefreshUpdatePanel();}
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
        private void NotifyAvailableUpdate()
        {
            if (!_pendingUpdateNotice || _exiting || !_autoUpdatesEnabled || _updateResult == null) return;
            if (_settingsHost != null && _uiShown) { _pendingUpdateNotice = false; return; }
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
                _uiFailure=_text.Pick("无法打开浏览器，请稍后重试。","Could not open your browser. Try again later.");PublishUiState();
            }
        }
    }
}
