using System;
using System.Threading;
using System.Threading.Tasks;
using ScreenOffTray.SettingsTransport;

namespace ScreenOffTray
{
    internal sealed partial class TrayApplicationContext
    {
        private UpdatePackage _updatePackage;
        private CancellationTokenSource _downloadCancellation;
        private string _updatePhase="",_downloadError;
        private int _downloadPercent;
        private bool _updateApplying,_updateHandedOff;

        private async void DownloadUpdate()
        {
            if(_exiting || _updateApplying || _downloadCancellation!=null || _updatePackage!=null)return;
            if(_updateResult==null || !_updateResult.CanDownload){OpenUpdateRelease();return;}
            var cancellation=new CancellationTokenSource();_downloadCancellation=cancellation;
            _downloadError=null;_downloadPercent=0;_updatePhase="downloading";RefreshUpdatePanel();
            // One queued progress callback at a time; the UI never retains the download history.
            DownloadProgress pending=null;int queued=0;
            try {
                var package=await UpdateDownload.Fetch(_updateResult,delegate(DownloadProgress value) {
                    Interlocked.Exchange(ref pending,value);
                    if(Interlocked.Exchange(ref queued,1)!=0)return;
                    Post(delegate {
                        var latest=Interlocked.Exchange(ref pending,null);Interlocked.Exchange(ref queued,0);
                        if(_exiting || _downloadCancellation!=cancellation || cancellation.IsCancellationRequested || latest==null)return;
                        _updatePhase=latest.Stage;_downloadPercent=(int)(100*latest.Received/Math.Max(1,latest.Total));RefreshUpdatePanel();
                    });
                },cancellation.Token);
                if(_exiting || cancellation.IsCancellationRequested){package.Cleanup();return;}
                _updatePackage=package;_updatePhase="ready";_downloadPercent=100;
            }catch(Exception ex) {
                _updatePhase=cancellation.IsCancellationRequested?"cancelled":"failed";
                _downloadError=cancellation.IsCancellationRequested?null:ex.Message;
            }finally {
                _downloadCancellation=null;cancellation.Dispose();if(!_exiting)RefreshUpdatePanel();
            }
        }
        private void CancelDownload()
        {if(_downloadCancellation!=null)_downloadCancellation.Cancel();}

        private async Task<string> ApplyUpdate(FormValues values)
        {
            if(_updatePackage==null || _updateApplying || _exiting || _savingSettings || _savingWhitelist || _setup.Preparing || _session.IsBusy)
                return "当前不能安装更新，请等待保存和显示操作完成 / Wait for saving and display operations before updating";
            _updateApplying=true;_updatePhase="applying";RefreshUpdatePanel();
            try {
                string error=await SaveUiValues(values);if(error!=null)return error;
                using(var handoff=await UpdateInstall.Prepare(_updatePackage,_text.English?"en":"zh-CN")) {
                    if(_exiting || _session.IsBusy || _setup.Preparing || _savingSettings || _savingWhitelist)
                        return "状态已改变，本次更新未执行，请重试 / State changed; update was not applied. Retry.";
                    handoff.Commit();_updateHandedOff=true;ExitApplication();return null;
                }
            }catch(Exception ex){
                // A rejected release/installer must not trap the user in a ready-package retry loop.
                _downloadError=ex.Message;_updatePackage.Cleanup();_updatePackage=null;_updateResult=null;return ex.Message;
            }
            finally{if(!_updateHandedOff){_updateApplying=false;_updatePhase=_updatePackage==null?"failed":"ready";RefreshUpdatePanel();}}
        }
        private void StopUpdateTransfer()
        {
            CancelDownload();
            if(!_updateHandedOff && _updatePackage!=null){_updatePackage.Cleanup();_updatePackage=null;}
        }
    }
}
