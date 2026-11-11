using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using ScreenOffTray.Playback;
using OledPrototype;

namespace ScreenOffTray.Tests
{
    internal static class OptimizationChainTests
    {
        private static int checks;
        private static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
        private sealed class Clock : IAutoSetupClock { internal long Now; public long Milliseconds { get { return Now; } } }
        private sealed class Scheduler : IAutoSetupScheduler {
            internal Action Due;
            public IDisposable Schedule(Action action, int ms) { Due = action; return new Ticket(); }
            private sealed class Ticket : IDisposable { public void Dispose() { } }
        }
        private sealed class Evidence : IDisplayEvidenceStore {
            internal int Writes;
            public DisplayEvidence Load() { return new DisplayEvidence { Observed="p", Completed="p", Checked=DateTime.UtcNow.ToString("o") }; }
            public void Save(DisplayEvidence value) { Writes++; }
        }
        private static void Queries()
        {
            var queue = new ConcurrentQueue<Action>(); var clock = new Clock(); var scheduler = new Scheduler(); var evidence = new Evidence();
            using (var entered = new ManualResetEvent(false)) using (var release = new ManualResetEvent(false))
            using (var returned = new ManualResetEvent(false))
            using (var state = new DisplaySetupState(a=>queue.Enqueue(a), evidence, ()=>{throw new Exception("No display platform permitted");},
                ()=> { entered.Set(); release.WaitOne(); returned.Set(); return new DisplaySetupResult { Profile="late", MatchingLive=true }; }, clock, scheduler)) {
                state.Recheck(); Check(entered.WaitOne(3000), "query enters one fake worker");
                Check(state.Checking && !state.IsReady, "in-flight query really closes start gate (baseline refusal reproduced)");
                clock.Now=30000;
#if BASELINE
                Check(scheduler.Due==null && state.Checking, "baseline has no independently scheduled query deadline");
#else
                scheduler.Due(); Action action; while(queue.TryDequeue(out action)) action();
                Check(!state.Checking && !state.IsReady && state.WaitingForPreviousQuery, "deadline releases UI wait but retains occupied native reader");
                state.Recheck(); Check(state.WaitingForPreviousQuery, "repeat read cannot replace blocked worker");
#endif
                release.Set(); Check(returned.WaitOne(3000), "fake query actually returns");
                SpinWait.SpinUntil(()=>!queue.IsEmpty,3000);
                Action posted; while(queue.TryDequeue(out posted)) posted();
#if !BASELINE
                Check(!state.IsReady && evidence.Writes==0 && state.Result.Diagnostics=="read_only_query_timeout", "late result cannot write evidence or become ready");
#endif
            }
        }
        private static void Requests()
        {
            var classified=new ManualOffRequest(()=>0);
            classified.Observe(InputKind.KeyDown,70,true);Check(classified.HeldGesture().Length==0,"injected hold cannot become the initiating physical gesture");
            classified.Begin(false,true,()=>true,classified.HeldGesture());Check(classified.Observe(InputKind.KeyDown,70,false),"a new physical press cancels despite an earlier virtual hold");
            long now=0; int starts=0; Func<bool> start=()=>{starts++;return true;}; var request=new ManualOffRequest(()=>now);
            Check(request.Begin(false,true,start,new[]{65})==OffRequestResult.Waiting,"manual query wait accepted");
            Check(request.Begin(false,true,start,null)==OffRequestResult.Waiting && starts==0,"duplicate request coalesces");
            Check(!request.Observe(InputKind.KeyDown,65,false),"gesture repeat ignored");
            Check(!request.Observe(InputKind.KeyUp,65,false),"gesture release ignored");
            Check(!request.Observe(InputKind.Move,0,true),"injected movement cannot cancel");
            now=2999;Check(request.Poll(true,false,start)==OffRequestResult.Accepted && starts==1,"ready result starts exactly once before deadline");
            Check(request.Poll(true,false,start)==null && starts==1,"completion cannot run twice");
            request.Begin(false,true,start,null);now+=3000;Check(request.Poll(true,false,start)==OffRequestResult.TimedOut && starts==1,"deadline wins even over late ready result");
            foreach(InputKind kind in new[]{InputKind.KeyDown,InputKind.ButtonDown,InputKind.Move,InputKind.Wheel}) {
                request.Begin(false,true,start,null);Check(request.Observe(kind,kind==InputKind.ButtonDown?257:66,false),"fresh physical intent cancels: "+kind);
                Check(request.Poll(true,false,start)==null,"cancelled intent cannot later start");
            }
        }

#if !BASELINE
        private static void ExitBarrier()
        {
            var flow=new SessionFlow();flow.RequestEnd(true);var barrier=new RecoveryStopBarrier();
            var session=(OledSession)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(OledSession));
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            typeof(OledSession).GetField("flow",flags).SetValue(session,flow);typeof(OledSession).GetField("stopBarrier",flags).SetValue(session,barrier);
            Check(session.IsSafeToExit,"idle session initially reports safe exit");
            var pending=new TaskCompletionSource<bool>();var confirmation=barrier.Observe(()=>pending.Task,null,delegate{});
            Check(!session.IsSafeToExit,"late setup guardian invalidates a previously ready exit");
            pending.SetResult(true);confirmation.GetAwaiter().GetResult();
            Check(session.IsSafeToExit,"confirmed late guardian permits final exit");
            GC.SuppressFinalize(session); // Unconstructed form has never owned an HWND or hook.
        }
        private static void MediaRetry()
        {
            long now=0;int requests=0;
            using(var media=new WinRtMediaStateReader(()=> {requests++; var t=new TaskCompletionSource<object>();t.SetException(new Exception("fake transient"));return t.Task;},o=>new MediaSessionStatus[0],()=>{},()=>now)) {
                media.Read();Check(requests==1,"one initial WinRT request");
                media.Read();Check(requests==1,"failure cannot immediately spin");
                now=5000;media.Read();Check(requests==2,"first retry at five seconds");
                now=35000;media.Read();Check(requests==3,"second retry after thirty seconds");
                now=155000;media.Read();Check(requests==4,"third retry after one hundred twenty seconds");
                now=1000000;media.Read();Check(requests==4,"automatic retry budget exhausted");
                media.RequestRetry();media.Read();Check(requests==5,"manual refresh permits one explicit attempt");
            }
            requests=0;now=0;
            using(var media=new WinRtMediaStateReader(()=>{requests++;var t=new TaskCompletionSource<object>();t.SetException(new Exception("fake"));return t.Task;},o=>new MediaSessionStatus[0],()=>{},()=>now)) {
                media.Read();now=1;media.RequestRetry();media.Read();Check(requests==2,"manual refresh starts immediately inside automatic backoff");
            }
            var never=new TaskCompletionSource<object>();requests=0;
            using(var media=new WinRtMediaStateReader(()=>{requests++;return never.Task;},o=>new MediaSessionStatus[0],()=>{},()=>now)) {
                media.Read();media.RequestRetry();now+=1000000;media.Read();Check(requests==1,"unreturned native request is never replaced");
            }
        }

#endif

#if !BASELINE
        private sealed class Registrar : IHotkeyRegistrar
        {
            internal bool Reject;internal readonly System.Collections.Generic.Dictionary<int,HotkeyBinding> Live=new System.Collections.Generic.Dictionary<int,HotkeyBinding>();
            public bool TryRegister(int id,HotkeyBinding binding,out string error){error=Reject?"Win32=1409":null;if(Reject)return false;Live.Add(id,binding);return true;}
            public bool Unregister(int id){return Live.Remove(id);}
        }
        private static void Hotkeys()
        {
            var native=new Registrar{Reject=true};string error;
            using(var keys=new HotkeyRegistration(native,HotkeyBinding.Default)) {
                Check(!keys.Initialize(out error) && !keys.Registered,"initial occupied shortcut remains unregistered");native.Reject=false;
                HotkeyRegistration.Reservation ticket;
                Check(keys.TryReserve(HotkeyBinding.Default,false,false,out ticket,out error),"same shortcut can be reserved after conflict clears");
                using(ticket)ticket.Commit();Check(keys.Registered && native.Live.Count==1,"registration actually recovers");
                HotkeyBinding next;HotkeyBinding.TryParse("Ctrl+Alt+F9",out next,out error);
                Check(keys.TryReserve(next,false,false,out ticket,out error),"new shortcut reserved before persistence");
                Check(keys.Hotkey.SameAs(HotkeyBinding.Default) && native.Live.Count==2,"old shortcut remains authoritative during asynchronous save");
                ticket.Dispose();Check(keys.Hotkey.SameAs(HotkeyBinding.Default) && native.Live.Count==1,"failed save releases only candidate reservation");
                Check(!keys.TryReserve(next,false,true,out ticket,out error),"held physical pairing blocks a shortcut transaction");
                keys.TryReserve(next,false,false,out ticket,out error);using(ticket)ticket.Commit();
                Check(keys.Hotkey.SameAs(next) && native.Live.Count==1,"successful save retires old shortcut exactly once");
            }
            Check(native.Live.Count==0,"all owned shortcut reservations release on close");
        }
        private static void Writer()
        {
            var values=new System.Collections.Generic.Dictionary<string,object>{{"form",1},{"list","old"}};int saves=0;
            var writer=new SerializedSettingsWriter(()=>new System.Collections.Generic.Dictionary<string,object>(values),old=>{values.Clear();foreach(var pair in old)values.Add(pair.Key,pair.Value);},()=>{saves++;});
            writer.UpdateAsync(()=>{}).Wait();Check(saves==0,"unchanged settings do not write");
            using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false)){
                var first=writer.UpdateAsync(()=>{entered.Set();release.WaitOne();values["form"]=2;});
                Check(entered.WaitOne(3000),"one settings transaction enters worker");
                var second=writer.UpdateAsync(()=>{values["list"]="new";});Check(!second.IsCompleted,"second transaction queues instead of executing concurrently");
                release.Set();Task.WaitAll(first,second);
                Check((int)values["form"]==2 && (string)values["list"]=="new" && saves==2,"field transactions merge without stale-copy overwrite");
                entered.Reset();release.Reset();var block=writer.Enqueue(()=>{entered.Set();release.WaitOne();});
                Check(entered.WaitOne(3000),"accepted settings work starts before shutdown");
                var drained=writer.SealAsync();Check(!drained.IsCompleted,"shutdown waits accepted writes");
                Check(writer.UpdateAsync(()=>values["form"]=99).IsFaulted,"closed writer rejects late changes");
                release.Set();Task.WaitAll(block,drained);Check((int)values["form"]==2,"late change never executes");
            }
            var failed=new SerializedSettingsWriter(()=>new System.Collections.Generic.Dictionary<string,object>(values),old=>{values.Clear();foreach(var pair in old)values.Add(pair.Key,pair.Value);},()=>{if((int)values["form"]==9)throw new Exception("fake write failure");});
            try{failed.UpdateAsync(()=>values["form"]=9).Wait();throw new Exception("failure not observed");}catch(AggregateException){Check((int)values["form"]==2,"failed write restores old settings");}
            var badRollback=new SerializedSettingsWriter(()=>new System.Collections.Generic.Dictionary<string,object>(values),old=>{values.Clear();foreach(var pair in old)values.Add(pair.Key,pair.Value);},()=>{throw new Exception("fake disk unavailable");});
            try{badRollback.UpdateAsync(()=>values["form"]=10).GetAwaiter().GetResult();throw new Exception("failure not observed");}catch(AggregateException){Check((int)values["form"]==2,"rollback failure is explicit and retains previous memory state");}
            var restoreFailure=new SerializedSettingsWriter(()=>new System.Collections.Generic.Dictionary<string,object>(values),old=>{throw new Exception("restore failed");},()=>{throw new Exception("save failed");});
            try{restoreFailure.UpdateAsync(()=>values["form"]=99).GetAwaiter().GetResult();throw new Exception("restore failure not observed");}catch(AggregateException error){Check(error.InnerExceptions.Count==2,"memory-restore failure remains an explicit unsafe rollback result");}
        }

#endif
        private static int Main()
        {
            try { Queries(); Requests();
#if !BASELINE
                MediaRetry();ExitBarrier(); Writer(); Hotkeys();
                var source=new AudioSnapshot{Known=true,Sources=new[]{new AudioSource{ExePath=@"C:\app.exe",DisplayName="App",ProcessIds=new[]{42}}}};
                var options=new PlaybackOptions{WhitelistPaths=new[]{@"C:\app.exe"}};
                var snapshot=new RuntimeSnapshot(7,"ready",true,false,false,source,options,new AutoOffTimingResult(0,AutoOffPauseReason.None),12,"");
                source.Sources[0].DisplayName="mutated";options.WhitelistPaths[0]=@"C:\other.exe";
                var view=snapshot.PlaybackView();view.Audio.Sources[0].ProcessIds[0]=99;view.AppliedOptions.WhitelistPaths[0]="changed";
                Check(snapshot.Version==7 && snapshot.PlaybackView().Audio.Sources[0].DisplayName=="App" && snapshot.PlaybackView().Audio.Sources[0].ProcessIds[0]==42,"published snapshot is detached from producer and view mutations");
                Check(snapshot.PlaybackView().AppliedOptions.WhitelistPaths[0]==@"C:\app.exe","view draft cannot mutate applied allowlist");
#endif
                Console.WriteLine("Checks="+checks+"; hooks=0; apply=0; settingsWrites=0");return 0;
            } catch(Exception ex) { Console.Error.WriteLine(ex);return 1; }
        }
    }
}
