using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ScreenOffTray.Playback;
using ScreenOffTray.Properties;
using ScreenOffTray.SettingsTransport;
using ScreenOffTray.Ui;

namespace ScreenOffTray
{
    internal sealed partial class TrayApplicationContext
    {
        private SettingsPipeHost _settingsHost;
        private bool _uiShown,_startupEnabled,_savingWhitelist,_noticeAwaitingPresentation;
        private int[] _migrationChoices;
        private string _uiFailure;
        private readonly bool _forceStartupGuide;
        private Action<bool,bool> _startupConfirmation;
        private void CheckStartupConfirmation()
        {
            if(_startupConfirmation==null || _exiting)return;
            bool ready=_migrationDone && _settingsFailure==null && _session.Supported && _session.CanStartSession && _setup.IsReady;
            if((ready && (!_forceStartupGuide || _uiShown)) || _uiShown) {
                var report=_startupConfirmation;_startupConfirmation=null;report(ready,_uiShown);return;
            }
            if(_autoClock.ElapsedMilliseconds>=5000 && _settingsHost==null){_initialSetupRequested=true;QueueInitialSetup();}
        }

        private void ObserveSetupPresentation()
        {
            if(_settingsHost!=null) {
                if(_uiShown && _setup.HasSettledResult){_setupPrompts.AcknowledgeVisibleState(_setup.IsReady,SetupPromptPolicy.AttentionKey(_setup.Result,_setup.LastPreparationError));SavePromptHistory();}
                return;
            }
            if(_initialSetupRequested){QueueInitialSetup();return;}
            var notice=_setupPrompts.Observe(_setup.HasSettledResult,_setup.IsReady,
                !_migrationDone || _session.IsBusy || _hotkeyEditing || _exiting || _noticeAwaitingPresentation,
                SetupPromptPolicy.AttentionKey(_setup.Result,_setup.LastPreparationError));
            if(notice==SetupNotice.Introduction || notice==SetupNotice.Attention){_noticeAwaitingPresentation=true;_initialSetupRequested=true;QueueInitialSetup();}
            else SavePromptHistory();
        }
        private void QueueInitialSetup()
        {
            if(_initialSetupQueued || _settingsHost!=null || (!_migrationDone && _migrationChoices==null && _settingsFailure==null) || _exiting || _session.IsBusy)return;
            _initialSetupQueued=true;
            Post(delegate{_initialSetupQueued=false;if(_initialSetupRequested)ShowSettings();});
        }
        private void ShowInitialSetup(){ShowSettings();}
        private void ShowSettings()
        {
            if(_exiting || _session.IsBusy || (!_migrationDone && _migrationChoices==null && _settingsFailure==null))return;
            if(_settingsHost!=null){_settingsHost.Activate();return;}
            SettingsPipeHost host=null;
            host=new SettingsPipeHost(PostSetupCompletion,HandleUiCommand,delegate{UiShown(host);},delegate{UiExited(host);});
            _settingsHost=host;_hotkeyEditing=true;_uiShown=false;_uiFailure=null;_initialSetupRequested=false;
            _session.SettingsKey+=ObserveSettingsKey;
            try {
                host.Start(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ScreenOffTray.Settings.exe"),_text.English?"en":"zh-CN");
                _initialSetupRequested=false;UpdateMenuState();_pendingUpdateNotice=false;
            }catch(Exception ex){
                _uiFailure=ex.Message;OledPrototype.Program.Log("settings_process_start_failed: "+ex);
                if(!host.IsAlive)UiExited(host);
                if(!_exiting)_trayIcon.ShowBalloonTip(5000,_text.Title,_text.Pick("设置窗口未能打开，请重试。","Could not open Settings. Please retry."),ToolTipIcon.Warning);
            }
        }
        private void UiShown(SettingsPipeHost host)
        {
            if(!Object.ReferenceEquals(host,_settingsHost) || _uiShown)return;
            _uiShown=true;_noticeAwaitingPresentation=false;_setupPrompts.MarkIntroductionShown();
            if(_setup.HasSettledResult)_setupPrompts.AcknowledgeVisibleState(_setup.IsReady,SetupPromptPolicy.AttentionKey(_setup.Result,_setup.LastPreparationError));
            SavePromptHistory();UpdateMenuState();
        }
        private void UiExited(SettingsPipeHost host)
        {
            if(!Object.ReferenceEquals(host,_settingsHost))return;
            _settingsHost=null;_uiShown=false;_hotkeyEditing=false;_session.SettingsKey-=ObserveSettingsKey;
            host.Dispose();
            if(_setup.Preparing)_setup.CancelPreparation();
            if(_exiting){ContinueExit();return;}
            if(_setup.HasSettledResult)_setupPrompts.AcknowledgeVisibleState(_setup.IsReady,SetupPromptPolicy.AttentionKey(_setup.Result,_setup.LastPreparationError));
            else _setupPrompts.DeferPendingCheck();
            UpdateMenuState();
        }
        private void ObserveSettingsKey(int key,uint modifiers,bool up)
        {var host=_settingsHost;if(host!=null)host.ForwardKey(key,modifiers,up);}
        private void RefreshSettingsPanel(){PublishUiState();}
        private void RefreshUpdatePanel(){UpdateMenuState();}
        private void PublishUiState()
        {var host=_settingsHost;if(host!=null)host.Publish(CreateUiState());}

        private ViewState CreateUiState()
        {
            var panel=ReadPlaybackPanel();
            bool ready=_setup.HasVerifiedProfile && _displayOffSupported && _settingsFailure==null && _migrationDone;
            string[] whitelist=panel.AppliedOptions==null || panel.AppliedOptions.WhitelistPaths==null?new string[0]:(string[])panel.AppliedOptions.WhitelistPaths.Clone();
            bool playbackBlocks=panel.Timing.PauseReason==AutoOffPauseReason.Playback;
            string theme;
            try { theme=NormalizeTheme(Settings.Default.InterfaceTheme); }
            catch { theme="geometry"; }
            Func<bool,string> heading=en=>_settingsFailure!=null?(en?"Settings need attention":"设置需要处理"):
                _migrationChoices!=null?(en?"Choose previous settings":"请选择旧设置"):
                !_displayOffSupported?(en?"Display off unavailable":"关屏暂不可用"):
                _setup.Preparing?(en?"Preparing displays…":"正在准备显示…"):
                ready?(_setup.Checking?(en?"✓ Ready · refreshing":"✓ 显示已就绪 · 复检中"):(en?"✓ Display ready":"✓ 显示已就绪")):
                _setup.Checking?(en?"Checking displays…":"正在核验显示…"):(en?"Display off is not enabled":"关屏未启用");
            Func<bool,string> detail=en=>_settingsFailure!=null?_settingsFailure:!_displayOffSupported?LocalizeSession(_displayOffUnavailableReason,en):SetupDetail(en);
            return new ViewState{
                Version=_snapshotVersion,StatusZh=heading(false),StatusEn=heading(true),Ready=ready,Checking=_setup.Checking,Preparing=_setup.Preparing,
                CanPrepare=_migrationDone && !_session.IsBusy && !_exiting && !_savingSettings && _settingsFailure==null && !_setup.HasVerifiedProfile && _setup.CanPrepareDisplay,
                CanClose=!_savingSettings && !_setup.Preparing,Saving=_savingSettings,SavingWhitelist=_savingWhitelist,
                DetailZh=detail(false),DetailEn=detail(true),Diagnostics=String.Join("\r\n",new[]{_setup.Diagnostics,panel.SettingsWarning,panel.Audio!=null && !panel.Audio.Known?panel.Audio.Error:null,_uiFailure}).Trim(),
                ProtectionZh=panel.Timing.Blocked || panel.Timing.PlaybackLimitReached?AutoOffStatusText.Format(panel.Timing,_timeoutMinutes,false):"",
                ProtectionEn=panel.Timing.Blocked || panel.Timing.PlaybackLimitReached?AutoOffStatusText.Format(panel.Timing,_timeoutMinutes,true):"",
                AudioKnown=panel.Audio!=null && panel.Audio.Known,
                PlaybackLimitReached=panel.Timing.PlaybackLimitReached,
                UserPaused=_paused,
                Sources=panel.Audio==null || !panel.Audio.Known || panel.Audio.Sources==null?new PlayingSource[0]:panel.Audio.Sources.Select(s=>{
                    bool listed=whitelist.Any(path=>String.Equals(path,s.ExePath,StringComparison.OrdinalIgnoreCase));
                    return new PlayingSource{Path=s.ExePath,Name=s.DisplayName,Whitelisted=listed,Protected=playbackBlocks && listed};
                }).ToArray(),
                Whitelist=whitelist,MigrationChoices=_migrationChoices==null?new int[0]:(int[])_migrationChoices.Clone(),
                UpdateZh=UpdateText(false),UpdateEn=UpdateText(true),UpdateAvailable=_updateResult!=null && _updateResult.Status==UpdateCheckStatus.Available,
                Applied=new FormValues{Minutes=_timeoutMinutes,Hotkey=_session.Hotkey.Serialize(),Language=_text.English?"en":"zh-CN",Startup=_startupEnabled,
                    AutomaticUpdates=_autoUpdatesEnabled,LimitEnabled=_playbackOptions.PlaybackLimitEnabled,LimitMinutes=_playbackOptions.PlaybackLimitMinutes,
                    InterfaceTheme=theme}
            };
        }
        private static string NormalizeTheme(string value)
        {
            return value=="warm" || value=="black" ? value : "geometry";
        }
        private async void HandleUiCommand(Packet request,Action<Packet> completed)
        {
            string error=null,code=null;
            try {
                if(_exiting){completed(new Packet{Error="程序正在退出 / Application is exiting",Code="closing"});return;}
                switch(request.Kind) {
                    case "save":
                        if(request.Values==null){error="Invalid settings";break;}
                        HotkeyBinding binding;string reason;
                        if(!HotkeyBinding.TryParse(request.Values.Hotkey,out binding,out reason)){error=reason;break;}
                        error=await SaveSettingsAsync(new SettingsValues{TimeoutMinutes=request.Values.Minutes,Hotkey=binding,HotkeyEdited=request.Values.HotkeyEdited,Language=request.Values.Language,
                            Startup=request.Values.Startup,AutomaticUpdateChecks=request.Values.AutomaticUpdates,PlaybackLimitEnabled=request.Values.LimitEnabled,PlaybackLimitMinutes=request.Values.LimitMinutes,
                            InterfaceTheme=request.Values.InterfaceTheme});break;
                    case "whitelistAdd":case "whitelistRemove":
                        if(request.Paths==null || request.Paths.Length!=1){error="Invalid allowlist operation";break;}
                        string path=ExeIdentity.Normalize(request.Paths[0]);if(path==null){error="Invalid executable identity";break;}
                        string[] paths=request.Kind=="whitelistAdd"?_playbackOptions.WhitelistPaths.Concat(new[]{path}).ToArray():_playbackOptions.WhitelistPaths.Where(p=>!String.Equals(p,path,StringComparison.OrdinalIgnoreCase)).ToArray();
                        error=await SaveWhitelistAsync(paths);break;
                    case "migrationChoice":
                        if(request.Values==null || _migrationChoices==null || !_migrationChoices.Contains(request.Values.Minutes)){error="请选择一个旧关闭时间 / Select one previous timeout";break;}
                        if(_savingSettings){error="正在保存 / Saving";break;}
                        _savingSettings=true;UpdateMenuState();
                        try{await Task.Run(delegate{SettingsMigration.Finish(request.Values.Minutes);});_migrationChoices=null;_timeoutMinutes=LoadTimeoutMinutes();_migrationDone=true;if(!_exiting)_setup.Recheck();}
                        finally{_savingSettings=false;if(_exiting)ContinueExit();}break;
                    case "prepare":if(!StartAutomaticSetup())error="当前不能开始显示准备，请重新核对状态 / Display preparation could not start; check the current state";break;
                    case "cancelPrepare":_setup.CancelPreparation();break;
                    case "recheck":if(!_setup.Preparing && !_session.IsBusy)_setup.Recheck();else error="Display operation is busy";break;
                    case "refresh":_audioMonitor.RequestRefresh();break;
                    case "checkUpdate":if(_updateResult!=null && _updateResult.Status==UpdateCheckStatus.Available)OpenUpdateRelease();else CheckForUpdates(false);break;
                    case "displaySettings":System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:display"){UseShellExecute=true});break;
                    default:error="Unsupported settings command";code="unsupported_command";break;
                }
            }catch(Exception ex){error=ex.Message;code="operation_failed";OledPrototype.Program.Log("settings_command_failed; kind="+request.Kind+"; "+ex.GetType().Name);}
            if(!_finishingExit && !_session.IsDisposed)UpdateMenuState();completed(new Packet{Error=error,Code=error==null?null:code??"operation_rejected"});
        }
        private string UpdateText(bool en)
        {
            if(!_updates.IsConfigured)return en?"The update source is not configured yet.":"更新源尚未配置。";
            if(_updateChecking)return en?"Checking…":"正在检查…";
            if(_updateResult==null)return "";
            switch(_updateResult.Status){
                case UpdateCheckStatus.Available:return (en?"Version ":"发现新版本 v")+_updateResult.LatestVersion+(en?" is available.":"");
                case UpdateCheckStatus.UpToDate:return en?"You’re up to date.":"已是最新版本。";
                case UpdateCheckStatus.NoRelease:return en?"No stable release is available yet.":"暂未发布可用的正式版本。";
                case UpdateCheckStatus.Failed:return en?"Could not check for updates. Try again later.":"检查失败，请稍后重试。";
                default:return "";
            }
        }
        private void QueueExitPromptHistory()
        {
            if(_noticeAwaitingPresentation && !_setup.IsReady)return;
            bool shown=_setupPrompts.IntroShown;string key=_setupPrompts.LastNoticeKey;
            SerializedSettingsWriter.Default.UpdateAsync(delegate{Settings.Default.SetupIntroShown=shown;Settings.Default.SetupLastNoticeKey=key;})
                .ContinueWith(task=>OledPrototype.Program.Log("exit_prompt_history_failed: "+task.Exception),System.Threading.CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted,TaskScheduler.Default);
        }
        private async void SavePromptHistory()
        {
            if(_savingPromptHistory || (_noticeAwaitingPresentation && !_setup.IsReady))return;
            bool shown=_setupPrompts.IntroShown;string key=_setupPrompts.LastNoticeKey;_savingPromptHistory=true;
            try{await SerializedSettingsWriter.Default.UpdateAsync(delegate{Settings.Default.SetupIntroShown=shown;Settings.Default.SetupLastNoticeKey=key;});}
            catch(Exception ex){if(!_historySaveWarning){_historySaveWarning=true;OledPrototype.Program.Log("setup_prompt_history_save_failed: "+ex.Message);}}
            finally{_savingPromptHistory=false;}
            if(!_exiting && (shown!=_setupPrompts.IntroShown || key!=_setupPrompts.LastNoticeKey))SavePromptHistory();
        }
        private void FinishMigration(SettingsMigrationResult migration)
        {
            if(_exiting)return;
            if(!migration.CanFinish){_settingsFailure=migration.Warning;_initialSetupRequested=true;QueueInitialSetup();UpdateMenuState();return;}
            if(migration.NeedsChoice){_migrationChoices=migration.Choices.ToArray();_initialSetupRequested=true;QueueInitialSetup();UpdateMenuState();return;}
            _timeoutMinutes=LoadTimeoutMinutes();_migrationDone=true;
            if(_initialSetupRequested)QueueInitialSetup();_setup.Recheck();UpdateMenuState();
            if(_autoUpdatesEnabled && _updates.IsConfigured)CheckForUpdates(true);
        }
    }
}
