using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OledPrototype;
using ScreenOffTray;

namespace ScreenOffTray {
 // Preparation-only adapter. Ordinary display rechecks never construct or use
 // the guarded write capability. The inner platform retains recovery ownership.
 internal sealed class DpiMatchedAutoSetup:IAutoSetupPlatform,IAutoSetupRecoveryCompletion {
  private readonly IAutoSetupPlatform inner;
  private readonly Func<Action,IDpiPlatform> createDpi;
  private AutoSetupPlan plan;
  private string summary="not_started";
  internal DpiMatchedAutoSetup(IAutoSetupPlatform inner,Func<Action,IDpiPlatform> createDpi=null){
   this.inner=inner;this.createDpi=createDpi??(check=>new NativeDpiPlatform(check));
  }
  public AutoSetupPlan PlanCheck(){return plan=inner.PlanCheck();}
  public DisplayBackend.Snapshot Capture(){return inner.Capture();}
  public void Validate(DisplayBackend.Snapshot snapshot){inner.Validate(snapshot);}
  public void Apply(DisplayBackend.Snapshot expected,DisplayBackend.Snapshot desired,Action before){
   inner.Apply(expected,desired,before);
   DpiMatching.AdjustAfterExtendedApply(plan,desired,()=>DisplayAutoCcd.VerifyExtended(plan,inner.Capture()),before,delegate{
    var timer=Stopwatch.StartNew();summary="started";
    // The native setter also verifies this attempt's exact extended scope after
    // its final DPI read. Recheck the deadline/guardian after that CCD query.
    Action scopeCheck=delegate{before();DisplayAutoCcd.VerifyExtended(plan,inner.Capture());before();};
    try{
     var result=DpiMatching.Match(createDpi(scopeCheck),before);
     summary="verified; changed="+result.Changed+"; physical_dpi="+result.TargetDpi+"; virtual_dpi="+result.After.VirtualDpi+"; elapsed_ms="+timer.ElapsedMilliseconds;
    }catch(OperationCanceledException){summary="cancelled; elapsed_ms="+timer.ElapsedMilliseconds;throw;}
    catch(TimeoutException){summary="deadline; elapsed_ms="+timer.ElapsedMilliseconds;throw;}
    catch(Exception ex){summary="failed; elapsed_ms="+timer.ElapsedMilliseconds+"; "+ex;
     throw new AutoSetupUnsupportedException("虚拟屏缩放自动匹配未通过，请查看诊断详情；不会调整实体屏。 / Automatic virtual DPI matching failed; see diagnostics. Physical scaling is unchanged.",ex);
    }
   });
  }
  public DisplaySetupResult Probe(){return inner.Probe();}
  public string SaveRecovery(DisplayBackend.Snapshot snapshot){return inner.SaveRecovery(snapshot);}
  public IAutoSetupGuardian StartGuardian(string path){return inner.StartGuardian(path);}
  public void Record(string path,string detail){inner.Record(path,detail+"\r\nvirtual_dpi_matching="+summary);}
  public Task<bool> CompleteRecoveryAsync(Task<bool> stopped,bool safe){var completion=inner as IAutoSetupRecoveryCompletion;return completion==null?Task.FromResult(false):completion.CompleteRecoveryAsync(stopped,safe);}
 }
 internal sealed class DpiReading {
  internal string PhysicalDevice,VirtualDevice,PhysicalSource,VirtualSource;
  internal uint Width,Height,VirtualWidth,VirtualHeight,PhysicalDpi,VirtualDpi;
  internal int Min,Current,Max,MetadataError;internal bool Cloned,ExactVirtual;
  internal DisplayBackend.PathInfo VirtualPath;
  internal string Identity {get{return PhysicalDevice+"|"+VirtualDevice+"|"+PhysicalSource+"|"+VirtualSource+"|"+Width+"x"+Height;}}
 }
 internal interface IDpiPlatform {DpiReading Read();void SetVirtual(DpiReading expected,int relative);void Delay(int ms);}
 internal sealed class DpiMatchResult {internal bool Changed;internal uint TargetDpi;internal DpiReading Before,After;}
 internal static class DpiMatching {
  internal static readonly int[] Percentages={100,125,150,175,200,225,250,300,350,400,450,500};
  // Mutations finish before ReadSettledProof starts its read-only budget.
  // This attempt's exact Extended object excludes physical-only and recovery.
  internal static void AdjustAfterExtendedApply(AutoSetupPlan plan,DisplayBackend.Snapshot desired,Action verify,Action check,Action match){
   if(plan==null || !Object.ReferenceEquals(desired,plan.Extended))return;
   check();verify();check();match();check();verify();check();
  }
  internal static int RelativeFor(DpiReading r) {return OffsetFor(r,true);}
  private static int OffsetFor(DpiReading r,bool requireCurrentMeasurement) {
   if(r==null || r.MetadataError!=0 || !r.ExactVirtual || r.Cloned || String.IsNullOrEmpty(r.PhysicalSource) || r.PhysicalSource==r.VirtualSource || String.IsNullOrEmpty(r.PhysicalDevice) || r.PhysicalDevice==r.VirtualDevice)throw new InvalidOperationException("Independent physical and verified MttVDD sources required.");
   if(r.Width==0 || r.Height==0 || r.Width!=r.VirtualWidth || r.Height!=r.VirtualHeight)throw new InvalidOperationException("Match resolutions before setting virtual DPI.");
   long maxIndex=(long)r.Max-r.Min,recommended=-(long)r.Min,current=(long)r.Current-r.Min;
   if(r.Min>0 || r.Max<0 || maxIndex<0 || maxIndex>=Percentages.Length || recommended<0 || recommended>maxIndex || current<0 || current>maxIndex)throw new InvalidOperationException("Unsupported DPI metadata; no setter call.");
   if(requireCurrentMeasurement && (long)r.VirtualDpi*100 != (long)Percentages[current]*96)throw new InvalidOperationException("DPI metadata and measured HWND DPI disagree; no setter call.");
   int index=-1;for(int i=0;i<Percentages.Length;i++)if((long)r.PhysicalDpi*100==(long)Percentages[i]*96)index=i;
   if(index<0 || index>maxIndex)throw new InvalidOperationException("Actual physical DPI is custom or outside the virtual display's supported range.");
   return checked(index+r.Min);
  }
  internal static void SameIdentity(DpiReading expected,DpiReading live,bool strict=true) {
   OffsetFor(live,strict);
   if(expected.Identity!=live.Identity || expected.PhysicalDpi!=live.PhysicalDpi || expected.Min!=live.Min || expected.Max!=live.Max)throw new InvalidOperationException("Display identity, physical DPI or supported range changed.");
  }
  internal static DpiMatchResult Match(IDpiPlatform platform,Action check,Func<long> milliseconds=null) {
   check();var first=platform.Read();check();int desired=RelativeFor(first);
   var result=new DpiMatchResult{Before=first,After=first,TargetDpi=first.PhysicalDpi};
   if(first.VirtualDpi==first.PhysicalDpi)return result;
   var current=platform.Read();check();SameIdentity(first,current);
   if(current.Current!=first.Current || current.VirtualDpi!=first.VirtualDpi)throw new InvalidOperationException("Virtual scaling changed before submission.");
   platform.SetVirtual(current,desired);result.Changed=true;
   var timer=Stopwatch.StartNew();
   var proof=DisplayAutoSetup.ReadSettledProof(delegate {
    var after=platform.Read();result.After=after;DpiMatching.SameIdentity(first,after,false);
    return new DisplaySetupResult{Profile=after.Identity+"|"+after.PhysicalDpi,MatchingLive=after.VirtualDpi==first.PhysicalDpi && after.Current==desired};
   },r=>r.MatchingLive,check,milliseconds??delegate{return timer.ElapsedMilliseconds;},platform.Delay);
   if(proof==null || !proof.MatchingLive)throw new InvalidOperationException("Virtual DPI was submitted once but measured readback did not confirm it; display off remains disabled.");
   return result;
  }
 }
 // The default constructor is read-only. Only guarded preparation supplies the
 // capability that is checked immediately before the native virtual-only setter.
 internal sealed class NativeDpiPlatform:IDpiPlatform {
  private readonly Action beforeSubmit;
  internal NativeDpiPlatform(Action beforeSubmit=null){this.beforeSubmit=beforeSubmit;}
  [StructLayout(LayoutKind.Sequential)]internal struct GetPacket{internal DisplayBackend.DeviceHeader Header;internal int Min,Current,Max;}
  [StructLayout(LayoutKind.Sequential)]internal struct SetPacket{internal DisplayBackend.DeviceHeader Header;internal int Relative;}
  [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo")]private static extern int GetScale(ref GetPacket value);
  [DllImport("user32.dll",EntryPoint="DisplayConfigSetDeviceInfo")]private static extern int SetScale(ref SetPacket value);
  private readonly AutoSetupNative native=new AutoSetupNative();
  internal static string Source(DisplayBackend.PathInfo path){return path.Source.Adapter.High.ToString(CultureInfo.InvariantCulture)+":"+path.Source.Adapter.Low+":"+path.Source.Id;}
  internal DpiReading Inspect(bool requireMetadata=true) {
   var before=native.Capture();int physical=-1,virtualIndex=-1;
   for(int i=0;i<before.Paths.Length;i++){
    if(DisplaySetupProbe.IsExactMtt(before.AdapterPaths[i])){if(virtualIndex>=0)throw new InvalidOperationException("More than one MttVDD route.");virtualIndex=i;}
    else if(DisplayBackend.IsPhysical(before.Paths[i].Target.Technology,before.AdapterPaths[i])){if(physical>=0)throw new InvalidOperationException("More than one physical route.");physical=i;}
    else throw new InvalidOperationException("Another active display route exists.");
   }
   if(physical<0 || virtualIndex<0 || before.Paths.Length!=2)throw new InvalidOperationException("A single active physical display and active MttVDD are required; no changes made.");
   var proof=DisplaySetupProbe.Probe();
   var v=before.Paths[virtualIndex];var packet=new GetPacket{Header=new DisplayBackend.DeviceHeader{Type=unchecked((uint)-3),Size=(uint)Marshal.SizeOf(typeof(GetPacket)),Adapter=v.Source.Adapter,Id=v.Source.Id}};
   int error=GetScale(ref packet);if(error!=0 && requireMetadata)throw new Win32Exception(error,"DPI metadata query is unavailable for this current source; Win32="+error+"; cloned="+proof.ClonedDisplay);
   var p=DisplayAutoCcd.Source(before,before.Paths[physical]);var vs=DisplayAutoCcd.Source(before,v);
   var result=new DpiReading{PhysicalDevice=before.DevicePaths[physical],VirtualDevice=before.DevicePaths[virtualIndex],PhysicalSource=Source(before.Paths[physical]),VirtualSource=Source(v),Width=p.Width,Height=p.Height,VirtualWidth=vs.Width,VirtualHeight=vs.Height,PhysicalDpi=proof.PhysicalDpi,VirtualDpi=proof.VirtualDpi,Cloned=proof.ClonedDisplay,ExactVirtual=DisplayAutoCcd.SameLuid(v.Source.Adapter,v.Target.Adapter),Min=packet.Min,Current=packet.Current,Max=packet.Max,MetadataError=error,VirtualPath=v};
   if(proof.PhysicalWidth!=p.Width || proof.PhysicalHeight!=p.Height || (!proof.ClonedDisplay && (proof.VirtualWidth!=vs.Width || proof.VirtualHeight!=vs.Height)))throw new InvalidOperationException("Probe and CCD dimensions disagree.");
   DisplayBackend.VerifySnapshot(before,native.Capture());return result;
  }
  public DpiReading Read(){return Inspect();}
  public void SetVirtual(DpiReading expected,int relative) {
   SubmitValidated(expected,relative,Read,beforeSubmit,delegate(SetPacket packet){return SetScale(ref packet);});
  }
  // Shared by native execution and pure boundary tests. The capability check
  // must follow the final potentially-blocking identity read, not precede it.
  internal static void SubmitValidated(DpiReading expected,int relative,Func<DpiReading> read,Action beforeSubmit,Func<SetPacket,int> submit) {
   if(beforeSubmit==null)throw new InvalidOperationException("No guarded write capability; inspection is read-only.");
   var current=read();DpiMatching.SameIdentity(expected,current);
   if(current.Current!=expected.Current || current.VirtualDpi!=expected.VirtualDpi || DpiMatching.RelativeFor(current)!=relative)throw new InvalidOperationException("DPI input changed before native submission.");
   // Only the freshly verified independent MttVDD source is passed to -4.
   var packet=new SetPacket{Header=new DisplayBackend.DeviceHeader{Type=unchecked((uint)-4),Size=(uint)Marshal.SizeOf(typeof(SetPacket)),Adapter=current.VirtualPath.Source.Adapter,Id=current.VirtualPath.Source.Id},Relative=relative};
   beforeSubmit();
   int error=submit(packet);if(error!=0)throw new Win32Exception(error,"Virtual DPI submission failed; no retry setter is issued.");
  }
  public void Delay(int ms){Thread.Sleep(ms);}
 }
}
