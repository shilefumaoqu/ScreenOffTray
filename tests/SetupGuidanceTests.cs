using System;
using System.Threading;
using ScreenOffTray;
namespace ScreenOffTray.Tests {
 internal static class SetupGuidanceTests {
  static int checks;
  static void Check(bool ok,string message) { if(!ok) throw new Exception(message); Console.WriteLine("PASS "+message);checks++; }
  sealed class Evidence : IDisplayEvidenceStore {
   internal int Saves,Loads;
   public DisplayEvidence Load() { Loads++; return new DisplayEvidence {Observed="previous",Completed="previous",Checked=DateTime.UtcNow.ToString("o")}; }
   public void Save(DisplayEvidence value) { Saves++; }
  }
  static void Gate(DisplaySetupResult result,bool allowed,string name) {
   var evidence=new Evidence();int factories=0;
   using(var settled=new ManualResetEvent(false))
   using(var state=new DisplaySetupState(a=>a(),evidence,()=> { Interlocked.Increment(ref factories);throw new Exception("Platform must never run in this test."); },()=>result)) {
    state.Changed+=()=> {if(state.HasSettledResult)settled.Set();};state.Recheck();
    Check(settled.WaitOne(3000),name+": fake read-only result settles");
    Check(state.CanPrepare==allowed,name+": preparation gate matches actual prerequisites");
    Check(!state.IsReady && !state.CanComplete,name+": guidance does not enable display off or become scale evidence");
    if(!allowed) {
     int writes=evidence.Saves,loads=evidence.Loads;
     Check(!state.AutoPrepare(),name+": direct preparation entry is also rejected");
     Check(!state.Preparing && factories==0 && evidence.Saves==writes && evidence.Loads==loads,name+": blocked entry creates no work, platform or new configuration writes");
     Check(state.Detail(false).Contains("虚拟屏") && state.Detail(true).Length>0,name+": localized actionable reason reaches the state reader");
    }
   }
  }
  static int Main(string[] args) {
   try {
    if(args.Length==1 && args[0]=="--read-only-live") {
     var actual=DisplaySetupProbe.Probe();
     Console.WriteLine("VirtualModeMissing="+actual.VirtualModeMissing+"; ClonedDisplay="+actual.ClonedDisplay+"; MetricsUnavailable="+actual.MetricsUnavailable+"; PreparationBlocked="+actual.PreparationBlocked);
     Console.WriteLine(actual.Zh);Console.WriteLine(actual.Diagnostics);
     Check(actual.VirtualModeMissing && actual.ClonedDisplay && actual.PreparationBlocked,"current recorded host is classified as clone plus missing mode without reading imaginary virtual DPI");
     Check(!actual.MatchingLive && !actual.Compatible,"live guidance never becomes accepted matching evidence");
     Console.WriteLine("Only queries, validation-free diagnostic windows and result formatting; no state/evidence save, APPLY or product Main.");return 0;
    }
    Check(DisplaySetupProbe.SameCloneDisplays(new[]{"Physical","MttVDD"},new[]{"mttvdd","PHYSICAL"}),"cross-adapter clone proxy routes correlate by exact monitor identity, independent of order and case");
    Check(!DisplaySetupProbe.SameCloneDisplays(new[]{"Physical","MttVDD"},new[]{"Physical","Other"}),"a saved topology for another monitor is not treated as the current clone");
    Check(!DisplaySetupProbe.SameCloneDisplays(new[]{"Physical","MttVDD"},new[]{"Physical","Physical"}),"duplicate saved monitor identities are refused");
    Check(!DisplaySetupProbe.SameCloneDisplays(new[]{"Physical","Physical"},new[]{"Physical","MttVDD"}),"ambiguous live monitor identities are refused");
    Check(!DisplaySetupProbe.SameCloneDisplays(new[]{"Physical","MttVDD"},new[]{"Physical"}) && !DisplaySetupProbe.SameCloneDisplays(new string[0],new string[0]),"different and empty monitor sets do not prove clone status");
    var oldModes=new[]{"800x600","1366x768","1920x1080","2560x1440","3840x2160","1920x1200"};
    var issue=DisplaySetupProbe.CreateVirtualGuidance(2880,1800,oldModes,true,false);
    Check(issue.VirtualModeMissing && issue.ClonedDisplay && !issue.MetricsUnavailable,"recorded cloned display with six old modes is identified without inventing DPI");
    Check(issue.RequiredResolution=="2880x1800" && issue.Zh.Contains("2880 × 1800") && issue.En.Contains("2880 × 1800"),"required native resolution and localized instructions are preserved");
    Gate(issue,false,"missing mode and clone");
    Gate(DisplaySetupProbe.CreateVirtualGuidance(2880,1800,oldModes,false,false),false,"missing mode without clone");
    Gate(DisplaySetupProbe.CreateVirtualGuidance(2880,1800,new[]{"2880x1800"},false,true),false,"unverified monitor mapping");
    var cloned=DisplaySetupProbe.CreateVirtualGuidance(2880,1800,new[]{"2880x1800"},true,false);
    Check(cloned.ClonedDisplay && !cloned.PreparationBlocked && !cloned.MatchingLive && !cloned.Compatible,"clone with a real matching mode may prepare but never proves matching scale");
    Gate(cloned,true,"supported clone");
    Check(DisplaySetupProbe.CreateVirtualGuidance(2880,1800,new[]{"2880x1800"},false,false)==null,"previous normal extended-display decision remains with existing measured comparison");
    Check(DisplaySetupProbe.CreateVirtualGuidance(2880,1800,new string[0],false,false)==null,"an empty mode enumeration alone is not called a confirmed missing resolution");
    var unknown=DisplaySetupProbe.CreateVirtualGuidance(2880,1800,new string[0],false,true);
    Check(!unknown.VirtualModeMissing && unknown.MetricsUnavailable,"unknown metrics stay distinct from an absent mode");
    Console.WriteLine("RESULT checks="+checks+" failures=0; fake platforms/evidence only; no product Main, native display queries, APPLY, hooks or user settings.");return 0;
   } catch(Exception e) {Console.WriteLine("FAIL "+e);return 1;}
  }
 }
}
