using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using ScreenOffTray;
namespace ScreenOffTray.Tests {
 internal static class PreparationRetryTests {
  static int checks;
  static void Check(bool ok,string msg){if(!ok)throw new Exception(msg);checks++;Console.WriteLine("PASS "+msg);}
  static DisplaySetupResult Match(string key){return new DisplaySetupResult{Profile=key,MatchingLive=true,PhysicalWidth=2880,PhysicalHeight=1800,PhysicalDpi=192,VirtualWidth=2880,VirtualHeight=1800,VirtualDpi=192};}
  static void Rejected(Action action,Type type,string msg){try{action();}catch(Exception ex){Check(type.IsInstanceOfType(ex),msg);return;}throw new Exception(msg);}
  sealed class Store:IDisplayEvidenceStore {
   public DisplayEvidence Load(){return new DisplayEvidence{Observed="old",Completed="old",Checked=DateTime.UtcNow.ToString("o")};}
   public void Save(DisplayEvidence value){}
  }
  sealed class Mode:IVirtualModePlatform {
   internal int Reads,Writes,Restarts;internal string Current;internal uint Width,Height,Rotation;internal readonly List<string> Seen=new List<string>();
   public VirtualModeRequirement Read(){Reads++;if(Reads%2==0)throw new InvalidOperationException("fixture stops before any protected/native operation");Seen.Add(Current);return new VirtualModeRequirement{PhysicalKey=Current,Width=Width,Height=Height,Rotation=Rotation,VirtualDevice="vdd",Instance="fixture",DriverKey="fixture",Listed=true};}
   public byte[] ReadConfig(){throw new Exception("No config read for listed modes");}
   public string Commit(byte[] a,byte[] b,VirtualModeRequirement r){Writes++;throw new Exception("Write forbidden");}
   public void Restart(VirtualModeRequirement r){Restarts++;throw new Exception("Restart forbidden");}
   public void Delay(){throw new Exception("Delay not expected");}
  }
  static void Pump(ConcurrentQueue<Action> q,Func<bool> done){var limit=DateTime.UtcNow.AddSeconds(4);while(!done()){Action a;while(q.TryDequeue(out a))a();if(DateTime.UtcNow>limit)throw new Exception("Fixture did not settle");Thread.Sleep(1);}Action last;while(q.TryDequeue(out last))last();}
  static int Main(){try{
   long time=0;int reads=0;var mismatch=Match("profile");mismatch.MatchingLive=false;mismatch.VirtualDpi=96;
   var settled=DisplayAutoSetup.ReadSettledProof(()=>++reads<=3?mismatch:Match("profile"),r=>r!=null&&r.MatchingLive,()=>{},()=>time,delay=>time+=delay);
   Check(settled.MatchingLive && reads==5 && time==600,"temporary mismatch settles using two current matching readings; no APPLY retry");
   time=0;reads=0;var fixedMismatch=DisplayAutoSetup.ReadSettledProof(()=>{reads++;return mismatch;},r=>r.MatchingLive,()=>{},()=>time,delay=>time+=delay);
   Check(!fixedMismatch.MatchingLive && reads==20 && time==3000,"real DPI mismatch stays rejected within three seconds");
   Check(DisplayAutoSetup.MeasurementSummary(mismatch,false).Contains("200%") && DisplayAutoSetup.MeasurementSummary(mismatch,false).Contains("100%"),"measured mismatch retains both actual scaling values");
   time=0;reads=0;settled=DisplayAutoSetup.ReadSettledProof(()=>{if(++reads<=2)throw new AutoSetupUnsupportedException("enumeration pending");return Match("new");},r=>r.MatchingLive,()=>{},()=>time,delay=>time+=delay);
   Check(settled.Profile=="new" && reads==4,"temporary unready enumeration retries current proof without old evidence");
   time=0;reads=0;settled=DisplayAutoSetup.ReadSettledProof(()=>Match(++reads==1?"A":"B"),r=>r.MatchingLive,()=>{},()=>time,delay=>time+=delay);
   Check(reads==3 && settled.Profile=="B","profile changes require two equal current profiles");
   time=0;reads=0;Rejected(()=>DisplayAutoSetup.ReadSettledProof(()=>{reads++;return mismatch;},r=>r.MatchingLive,()=>{if(time>=150)throw new OperationCanceledException();},()=>time,delay=>time+=delay),typeof(OperationCanceledException),"cancellation interrupts settling before next query");Check(reads==1,"cancelled settling makes no late query");
   time=0;Rejected(()=>DisplayAutoSetup.ReadSettledProof(()=>mismatch,r=>r.MatchingLive,()=>{if(time>=150)throw new InvalidOperationException("guardian gone");},()=>time,delay=>time+=delay),typeof(InvalidOperationException),"guardian loss interrupts readback and leaves recovery to owner");
   time=0;reads=0;Rejected(()=>DisplayAutoSetup.ReadSettledProof(()=>++reads==20?Match("late"):mismatch,r=>r.MatchingLive,()=>{},()=>time,delay=>time+=delay),typeof(AutoSetupUnsupportedException),"single matching sample at deadline is insufficient");
   time=0;reads=0;Rejected(()=>DisplayAutoSetup.ReadSettledProof(()=>{reads++;return mismatch;},r=>r.MatchingLive,()=>{if(time>=150)throw new TimeoutException();},()=>time,delay=>time+=delay),typeof(TimeoutException),"outer safety deadline is never extended by settling");
   time=0;reads=0;settled=DisplayAutoSetup.ReadSettledProof(()=>{reads++;return Match("fresh");},r=>r.MatchingLive,()=>{},()=>time,delay=>time+=delay);Check(reads==2 && time==150 && settled.Profile=="fresh","already stable readback completes after one short confirmation");
   time=0;reads=0;Rejected(()=>DisplayAutoSetup.ReadSettledProof(()=>{reads++;time+=1600;return Match("slow");},r=>r.MatchingLive,()=>{},()=>time,delay=>time+=delay),typeof(AutoSetupUnsupportedException),"two matching native results arriving after budget cannot be accepted");
   time=0;reads=0;DisplaySetupResult retained=null;
   Rejected(()=>DisplayAutoSetup.ReadSettledProof(()=>{if(++reads==1)return mismatch;throw new AutoSetupUnsupportedException("enumeration lost");},r=>r.MatchingLive,()=>{},()=>time,delay=>time+=delay,r=>retained=r),typeof(AutoSetupUnsupportedException),"later enumeration failure is not converted into old success");
   Check(retained==mismatch,"mismatch measurements survive a later enumeration exception");
   time=0;reads=0;retained=null;
   Rejected(()=>DisplayAutoSetup.ReadSettledProof(()=>{reads++;return mismatch;},r=>r.MatchingLive,()=>{if(reads>0)throw new OperationCanceledException();},()=>time,delay=>time+=delay,r=>retained=r),typeof(OperationCanceledException),"cancellation immediately after a query rejects the operation");
   Check(retained==mismatch,"measurements survive post-query cancellation without accepting them");
   time=0;reads=0;retained=null;
   Rejected(()=>DisplayAutoSetup.ReadSettledProof(()=>{reads++;time+=1600;return Match("slow");},r=>r.MatchingLive,()=>{},()=>time,delay=>time+=delay,r=>retained=r),typeof(AutoSetupUnsupportedException),"slow matching results are rejected with last measurement retained");
   Check(retained!=null && reads==2,"slow failure preserves measured values for recovery diagnostics");
   time=0;reads=0;var budgeted=DisplayAutoSetup.ReadSettledProof(()=>{reads++;time+=2900;return mismatch;},r=>r.MatchingLive,()=>{},()=>time,delay=>time+=delay);
   Check(reads==1 && time==3000 && !budgeted.MatchingLive,"wait is clipped to remaining budget and no new native query starts at deadline");
   reads=0;int tickIndex=0,lastWait=-1;long[] ticks={0,0,0,0,3500,3500,3500};
   var raced=DisplayAutoSetup.ReadSettledProof(()=>{reads++;return mismatch;},r=>r.MatchingLive,()=>{},()=>ticks[Math.Min(tickIndex++,ticks.Length-1)],delay=>{if(delay<0)throw new Exception("negative wait");lastWait=delay;});
   Check(reads==1 && lastWait==0 && !raced.MatchingLive,"clock advancing between budget reads cannot create a negative or infinite wait");
   var queue=new ConcurrentQueue<Action>();var mode=new Mode();int factories=0;
   using(var state=new DisplaySetupState(a=>queue.Enqueue(a),new Store(),()=>{factories++;throw new Exception("Protected platform must not start");},()=>new DisplaySetupResult{MetricsUnavailable=true,Zh="old failure",En="old failure"})){
    state.Recheck();Pump(queue,()=>state.HasSettledResult);
    Check(state.CanPrepareDisplay,"old metrics failure does not block an explicit fresh attempt");
    foreach(var value in new[]{"2880x1800|200|landscape","1800x2880|150|portrait","2560x1600|125|landscape"}){
     mode.Current=value;mode.Width=value.StartsWith("1800")?1800u:value.StartsWith("2560")?2560u:2880u;mode.Height=mode.Width==1800?2880u:mode.Width==2560?1600u:1800u;mode.Rotation=mode.Width==1800?2u:1u;
     Check(state.PrepareDisplay(()=>mode),"explicit attempt accepted for "+value);Pump(queue,()=>!state.Preparing);Check(mode.Seen[mode.Seen.Count-1]==value,"attempt reads current dimensions/DPI/rotation identity: "+value);
    }
    Check(mode.Seen.Count==3 && mode.Writes==0 && mode.Restarts==0 && factories==0,"three repeated attempts stay fresh and stop before native side effects");
   }
   Console.WriteLine("RESULT checks="+checks+" failures=0; virtual time/fake platform/store only");return 0;
  }catch(Exception ex){Console.WriteLine("FAIL "+ex);return 1;}}
 }
}
