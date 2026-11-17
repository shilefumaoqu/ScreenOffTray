// Executes the production TrayUpdates partial with fake persistence/display/installer peers.
using System;
using System.IO;
using System.Threading.Tasks;
using ScreenOffTray.SettingsTransport;
namespace ScreenOffTray
{
    internal sealed class UpdateHandoff : IDisposable
    {internal bool Committed,Disposed;internal void Commit(){Committed=true;}public void Dispose(){Disposed=true;}}
    internal static class UpdateInstall
    {internal static Task<UpdateHandoff> Next;internal static Task<UpdateHandoff> Prepare(UpdatePackage package,string language){return Next;}}
    internal sealed partial class TrayApplicationContext
    {
        private sealed class Session {internal bool IsBusy;}
        private sealed class Setup {internal bool Preparing;}
        private sealed class Text {internal bool English;}
        private readonly Session _session=new Session();private readonly Setup _setup=new Setup();private readonly Text _text=new Text();
        private bool _exiting,_savingSettings,_savingWhitelist;
        private UpdateCheckResult _updateResult;
        private int saves,exits;private Task<string> saveResult=Task.FromResult<string>(null);
        private Task<string> SaveUiValues(FormValues values){saves++;return saveResult;}
        private void RefreshUpdatePanel(){} private void Post(Action action){action();}private bool OpenUpdateRelease(){return false;}
        private void ExitApplication(){if(!_updateHandedOff)throw new Exception("Exit before commit");exits++;_exiting=true;StopUpdateTransfer();}
        private static int checks;
        private static void Check(bool value,string name){if(!value)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
        private static TrayApplicationContext Owner(){return new TrayApplicationContext{_updatePackage=new UpdatePackage{Directory="unused by fake installer"},_updatePhase="ready"};}
        private static async Task Run()
        {
            var empty=new TrayApplicationContext();Check(await empty.ApplyUpdate(new FormValues())!=null && empty.saves==0 && empty.exits==0,"no package cannot save or exit");
            foreach(string busy in new[]{"session","setup","settings","whitelist"}) {
                var owner=Owner();owner._session.IsBusy=busy=="session";owner._setup.Preparing=busy=="setup";owner._savingSettings=busy=="settings";owner._savingWhitelist=busy=="whitelist";
                Check(await owner.ApplyUpdate(new FormValues())!=null && owner.saves==0 && owner.exits==0,busy+" busy rejects update");
            }
            var saving=Owner();var saved=new TaskCompletionSource<string>();saving.saveResult=saved.Task;
            var pending=saving.ApplyUpdate(new FormValues());Check(saving._updateApplying && saving.saves==1,"saving closes update gate before yielding");
            Check(await saving.ApplyUpdate(new FormValues())!=null && saving.saves==1,"duplicate apply never repeats a save or handoff");
            saved.SetResult("save failed");Check(await pending=="save failed" && !saving._updateApplying && saving._updatePhase=="ready" && saving.exits==0,"save failure preserves program and verified package");
            var failure=Owner();var rejected=new TaskCompletionSource<UpdateHandoff>();rejected.SetException(new IOException("helper rejected"));UpdateInstall.Next=rejected.Task;
            Check(await failure.ApplyUpdate(new FormValues())=="helper rejected" && failure.exits==0 && !failure._updateApplying && failure._updatePackage==null && failure._updateResult==null && failure._updatePhase=="failed","rejected package releases cache and allows a fresh update check without exiting");
            var race=Owner();var prepared=new TaskCompletionSource<UpdateHandoff>();UpdateInstall.Next=prepared.Task;
            var racing=race.ApplyUpdate(new FormValues());race._setup.Preparing=true;var handoff=new UpdateHandoff();prepared.SetResult(handoff);
            Check(await racing!=null && !handoff.Committed && handoff.Disposed && race.exits==0,"changed display state after await rejects and releases handoff");
            var success=Owner();handoff=new UpdateHandoff();UpdateInstall.Next=Task.FromResult(handoff);
            Check(await success.ApplyUpdate(new FormValues())==null && handoff.Committed && handoff.Disposed && success.exits==1 && success._updateHandedOff && success._updatePackage!=null,"commit precedes normal exit and preserves installer ownership");
            Console.WriteLine("Checks="+checks+"; actual TrayUpdates control flow, fake collaborators only; no product host, UAC, installation or real settings");
        }
        private static int Main(){try{Run().GetAwaiter().GetResult();return 0;}catch(Exception ex){Console.WriteLine(ex);return 1;}}
    }
}
