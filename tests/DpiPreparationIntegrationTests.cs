using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OledPrototype;
using ScreenOffTray;

namespace ScreenOffTray.Tests {
 internal static class DpiPreparationIntegrationTests {
  static int checks;
  static void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
  sealed class Clock:IAutoSetupClock {internal long Now;public long Milliseconds{get{return Now;}}}
  sealed class Scheduler:IAutoSetupScheduler {
   sealed class Empty:IDisposable{public void Dispose(){}}
   public IDisposable Schedule(Action action,int milliseconds){return new Empty();}
  }
  sealed class Guard:IAutoSetupGuardian {
   internal bool Alive=true;internal int Stops,Relinquished;
   public bool IsAlive{get{return Alive;}}
   public void Arm(){} public void Pulse(){} public void Stop(){Stops++;}
   public Task<bool> StopAsync(){Stops++;return Task.FromResult(true);}
   public void Relinquish(){Relinquished++;}
  }
  sealed class Scene:IAutoSetupPlatform,IAutoSetupRecoveryCompletion {
   internal readonly Clock Clock=new Clock();internal readonly Guard Guard=new Guard();
   internal readonly AutoSetupPlan Plan;internal DisplayBackend.Snapshot Current;
   internal readonly List<string> Events=new List<string>();
   internal int Applies,Probes,Archives,Rollbacks,Saves;internal string Recorded;
   internal bool SaveOk=true,CommitOk=true;
   internal DpiReading Reading=new DpiReading{PhysicalDevice="physical",VirtualDevice="virtual",PhysicalSource="10:1",VirtualSource="20:1",Width=1920,Height=1080,VirtualWidth=1920,VirtualHeight=1080,PhysicalDpi=120,VirtualDpi=168,Min=0,Current=3,Max=3,ExactVirtual=true};
   internal Scene(){
    var physical=new DisplayBackend.PathInfo{Flags=1};
    physical.Source.Adapter.Low=10;physical.Source.Id=1;physical.Source.ModeIndex=0;
    physical.Target.Adapter.Low=10;physical.Target.Id=1;physical.Target.ModeIndex=1;physical.Target.Rotation=1;physical.Target.Scaling=1;physical.Target.Available=1;
    var original=new DisplayBackend.Snapshot{Paths=new[]{physical},Modes=new[]{
     new DisplayBackend.ModeInfo{InfoType=1,Id=1,Adapter=physical.Source.Adapter,Source=new DisplayBackend.SourceMode{Width=1920,Height=1080,PixelFormat=4}},
     new DisplayBackend.ModeInfo{InfoType=2,Id=1,Adapter=physical.Target.Adapter}},DevicePaths=new[]{"physical"},AdapterPaths=new[]{"physical-adapter"}};
    var virtualPath=new DisplayBackend.PathInfo{Flags=1};virtualPath.Source.Adapter.Low=20;virtualPath.Source.Id=1;
    virtualPath.Target.Adapter.Low=20;virtualPath.Target.Id=2;virtualPath.Target.ModeIndex=DisplayBackend.Invalid;virtualPath.Target.Technology=17;virtualPath.Target.Rotation=1;virtualPath.Target.Scaling=1;virtualPath.Target.Available=1;
    Plan=DisplayAutoCcd.Build(original,new DisplayBackend.Plan{Paths=new[]{virtualPath},Modes=new[]{new DisplayBackend.ModeInfo{InfoType=1,Id=1,Adapter=virtualPath.Source.Adapter,Source=original.Modes[0].Source}},DevicePath="virtual",AdapterPath="virtual-adapter"});
    Current=original;
   }
   public AutoSetupPlan PlanCheck(){return Plan;}
   public DisplayBackend.Snapshot Capture(){return Current;}
   public void Validate(DisplayBackend.Snapshot snapshot){}
   public void Apply(DisplayBackend.Snapshot expected,DisplayBackend.Snapshot desired,Action before){
    DisplayBackend.VerifySnapshot(expected,Current);before();Applies++;
    if(Object.ReferenceEquals(desired,Plan.Extended)){
     Events.Add("extend");var paths=(DisplayBackend.PathInfo[])desired.Paths.Clone();var modes=new DisplayBackend.ModeInfo[desired.Modes.Length+1];Array.Copy(desired.Modes,modes,desired.Modes.Length);
     paths[1].Target.ModeIndex=(uint)desired.Modes.Length;
     modes[modes.Length-1]=new DisplayBackend.ModeInfo{InfoType=2,Id=paths[1].Target.Id,Adapter=paths[1].Target.Adapter};
     Current=new DisplayBackend.Snapshot{Paths=paths,Modes=modes,DevicePaths=desired.DevicePaths,AdapterPaths=desired.AdapterPaths};
    }else{Events.Add(Object.ReferenceEquals(desired,Plan.Original)?"original":"physical");Current=desired;}
   }
   public DisplaySetupResult Probe(){
    Probes++;Clock.Now+=20;bool physical=Current.Paths.Length==1;Events.Add(physical?"proof-physical":"proof-extended");
    return new DisplaySetupResult{Profile="profile-"+Reading.PhysicalDpi,PhysicalOnly=physical,Compatible=physical,MatchingLive=!physical && Reading.PhysicalDpi==Reading.VirtualDpi,
     PhysicalWidth=1920,PhysicalHeight=1080,PhysicalDpi=Reading.PhysicalDpi,VirtualWidth=1920,VirtualHeight=1080,VirtualDpi=Reading.VirtualDpi};
   }
   public string SaveRecovery(DisplayBackend.Snapshot original){Events.Add("snapshot");return "fake-recovery-no-file";}
   public IAutoSetupGuardian StartGuardian(string path){Events.Add("guardian");return Guard;}
   public void Record(string path,string detail){Recorded=detail;}
   public Task<bool> CompleteRecoveryAsync(Task<bool> stopped,bool safe){Archives++;return Task.FromResult(stopped.Result && safe);}
  }
  sealed class Dpi:IDpiPlatform {
   internal readonly Scene Scene;internal readonly Action Gate;internal int Sets,Reads;internal bool NoAck;internal Action BeforeSet;internal int FirstReadDelay;
   internal Dpi(Scene scene,Action gate){Scene=scene;Gate=gate;}
   public DpiReading Read(){
    if(++Reads==1)Scene.Clock.Now+=FirstReadDelay;
    var r=Scene.Reading;return new DpiReading{PhysicalDevice=r.PhysicalDevice,VirtualDevice=r.VirtualDevice,PhysicalSource=r.PhysicalSource,VirtualSource=r.VirtualSource,Width=r.Width,Height=r.Height,VirtualWidth=r.VirtualWidth,VirtualHeight=r.VirtualHeight,PhysicalDpi=r.PhysicalDpi,VirtualDpi=r.VirtualDpi,Min=r.Min,Current=r.Current,Max=r.Max,ExactVirtual=r.ExactVirtual,Cloned=r.Cloned};
   }
   public void SetVirtual(DpiReading expected,int relative){
    if(BeforeSet!=null)BeforeSet();Gate();Sets++;Scene.Events.Add("dpi");if(!NoAck){Scene.Reading.Current=relative;Scene.Reading.VirtualDpi=Scene.Reading.PhysicalDpi;}
   }
   public void Delay(int milliseconds){Scene.Clock.Now+=milliseconds;}
  }
  static AutoSetupOutcome Run(Scene scene,Action<Dpi,AutoSetupAttempt> configure,out Dpi dpi){
   Dpi created=null;using(var attempt=new AutoSetupAttempt(0,scene.Clock,new Scheduler())){
    var platform=new DpiMatchedAutoSetup(scene,gate=>{created=new Dpi(scene,gate);if(configure!=null)configure(created,attempt);return created;});
    var result=DisplayAutoSetup.Run(platform,attempt,a=>a(),(live,final)=>{scene.Saves++;return scene.SaveOk;},()=>scene.CommitOk,()=>{scene.Rollbacks++;return true;});
    dpi=created;return result;
   }
  }
  static int Main(){try{
   Check(DisplaySetupState.CreateDefaultAutoPlatform() is DpiMatchedAutoSetup,"actual production factory selects the preparation-only DPI adapter without starting product Main");
   var readOnly=new Scene();int factories=0;var readOnlyAdapter=new DpiMatchedAutoSetup(readOnly,gate=>{factories++;throw new Exception("Read-only probe must not create a write-capable platform");});
   readOnlyAdapter.Probe();readOnlyAdapter.Probe();Check(factories==0 && readOnly.Applies==0,"ordinary Probe remains read-only and never creates DPI write capability");
   Dpi dpi;var good=new Scene();var result=Run(good,(d,a)=>d.FirstReadDelay=3200,out dpi);
   Check(result.Success && result.Live.MatchingLive && result.Final.PhysicalOnly && result.Final.Compatible,"real preparation state machine reaches committed readiness with fake DPI and display platforms");
   Check(dpi.Sets==1 && good.Reading.VirtualDpi==120 && good.Probes==4,"one virtual-only setter precedes two extended and two physical-only proofs");
   Check(good.Events.IndexOf("snapshot")<good.Events.IndexOf("guardian") && good.Events.IndexOf("guardian")<good.Events.IndexOf("extend") && good.Events.IndexOf("extend")<good.Events.IndexOf("dpi") && good.Events.IndexOf("dpi")<good.Events.IndexOf("proof-extended"),"snapshot and guardian own recovery before adjustment; slow adjustment is outside read-only proof budget");
   Check(good.Guard.Stops==1 && good.Guard.Relinquished==0 && result.ArchiveConfirmation.Result && good.Archives==1,"adapter forwards successful guardian stop and owned snapshot completion exactly once");
   Check(good.Recorded.Contains("virtual_dpi_matching=verified") && good.Recorded.Contains("physical_dpi=120"),"final bounded preparation diagnostic includes actual scale outcome");
   foreach(uint physical in new[]{120u,144u}){
    var fresh=new Scene();fresh.Reading.PhysicalDpi=physical;result=Run(fresh,null,out dpi);
    Check(result.Success && dpi.Sets==1 && fresh.Reading.VirtualDpi==physical,"separate explicit attempt reads and matches current physical DPI "+physical);
   }
   var same=new Scene();same.Reading.VirtualDpi=120;same.Reading.Current=1;result=Run(same,null,out dpi);
   Check(result.Success && dpi.Sets==0,"already matching scaling is still measured but never rewritten");
   foreach(string failure in new[]{"cancel","deadline","guardian","layout"}){
    var scene=new Scene();result=Run(scene,(d,a)=>d.BeforeSet=()=>{
     if(failure=="cancel")a.Cancel();if(failure=="deadline")scene.Clock.Now=30000;if(failure=="guardian")scene.Guard.Alive=false;if(failure=="layout")scene.Current=scene.Plan.PhysicalOnly;
    },out dpi);
    Check(!result.Success && dpi.Sets==0 && result.OriginalVerified && !result.RecoveryUnresolved,failure+" at final submission gate prevents write and confirms original layout");
    Check(scene.Saves==0 && scene.Rollbacks==0 && scene.Guard.Stops==1,failure+" does not save readiness or bypass guardian cleanup");
   }
   var noAck=new Scene();result=Run(noAck,(d,a)=>d.NoAck=true,out dpi);
   Check(!result.Success && dpi.Sets==1 && result.OriginalVerified && noAck.Probes==0,"unconfirmed adjustment is never repeated or passed to stable proof; original layout recovers");
   var failedSave=new Scene{SaveOk=false};result=Run(failedSave,null,out dpi);
   Check(!result.Success && result.OriginalVerified && failedSave.Rollbacks==1 && dpi.Sets==1,"failed evidence save rolls back settings and layout without adjusting DPI during recovery");
   var rejectedCommit=new Scene{CommitOk=false};result=Run(rejectedCommit,null,out dpi);
   Check(!result.Success && result.Live.MatchingLive && result.Final.Compatible && result.OriginalVerified && rejectedCommit.Rollbacks==1 && dpi.Sets==1,"late commit cancellation retains original recovery and does not treat proof as product success");
   var foreign=new Scene();result=Run(foreign,(d,a)=>d.BeforeSet=()=>{
    var current=foreign.Current;foreign.Current=new DisplayBackend.Snapshot{Paths=current.Paths,Modes=current.Modes,AdapterPaths=current.AdapterPaths,DevicePaths=new[]{"another-physical","virtual"}};
   },out dpi);
   Check(!result.Success && dpi.Sets==0 && result.RecoveryUnresolved && !result.OriginalVerified,"foreign display identity prevents DPI write and is not overwritten as a claimed recovery");
   Check(foreign.Guard.Relinquished==1 && foreign.Guard.Stops==0 && !result.ArchiveConfirmation.Result,"unresolved scope change retains recovery responsibility and fault material");
   Console.WriteLine("RESULT checks="+checks+" failures=0; real state machine with fake clock/platform/store only; no native setter/APPLY/hooks/product Main.");return 0;
  }catch(Exception ex){Console.WriteLine("FAIL "+ex);return 1;}}
 }
}
