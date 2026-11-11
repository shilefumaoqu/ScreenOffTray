using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using ScreenOffTray.Playback;
using ScreenOffTray.SettingsTransport;

namespace ScreenOffTray.Tests
{
    // Only this test entry is reachable. Generated copies deny Main, the real
    // controller, formal settings, hooks, power requests, all display writes.
    internal static class OptimizationIsolationProbe
    {
        private static string output,uiPath,mode;
        private static int duration,cycles,opened,closed,shown,statusDrops,interrupted;
        private static readonly Stopwatch clock=Stopwatch.StartNew();
        private static double closeAt,nextOpen,nextPublish,nextCheck,nextStatus,lastClose=-1000;
        private static bool closing,stopping;
        private static Control dispatcher;
        private static Process watchedUi;private static bool currentShown;private static double shownAt;
        private static ApplicationContext context;
        private static AudioMonitor audio;
        private static DisplaySetupState display;
        private static SettingsPipeHost ui;
        private static long version,audioReads,displayReads,displayFailures;
        private static StreamWriter events;
        private static ViewState state;
        private static readonly JavaScriptSerializer json=new JavaScriptSerializer();
        private static readonly AutoOffTiming timing=new AutoOffTiming(()=>clock.ElapsedMilliseconds);
        private sealed class Evidence:IDisplayEvidenceStore
        {public DisplayEvidence Load(){return new DisplayEvidence();}public void Save(DisplayEvidence value){} }
        [STAThread] private static int Main(string[] args)
        {
            if(args.Length==1 && args[0]=="--self-test")return SelfTest();
            if(args.Length!=4)return 2;
            output=Path.GetFullPath(args[0]);mode=args[1];duration=Int32.Parse(args[2]);cycles=Int32.Parse(args[3]);
            if((mode!="combined" && mode!="settings") || duration<20 || duration>7200 || cycles<0 || cycles>21)return 2;
            int result=0;
            try {
                if(!DesktopName().StartsWith("ScreenOffMemory-",StringComparison.Ordinal))throw new Exception("Private non-input desktop required");
                if(typeof(OledPrototype.Native).GetField("MemoryIsolationGuard")==null)throw new Exception("Guarded copy required");
                uiPath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ui","ScreenOffTray.Settings.exe");
                if(!File.Exists(uiPath))throw new FileNotFoundException(uiPath);
                Directory.CreateDirectory(output);events=new StreamWriter(Path.Combine(output,"events.csv"),false,Encoding.UTF8){AutoFlush=true};
                events.WriteLine("seconds,event,opened,closed");
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);Application.EnableVisualStyles();
                using(dispatcher=new Control())using(var tick=new System.Windows.Forms.Timer{Interval=100}) {
                    var handle=dispatcher.Handle;context=new ApplicationContext();
                    if(mode=="combined") {
                        audio=new AudioMonitor();audio.Changed+=()=>Interlocked.Increment(ref audioReads);
                        display=new DisplaySetupState(Post,new Evidence(),()=>{throw new Exception("Native preparation forbidden");},ReadDisplay);
                    }
                    string system=Environment.GetFolderPath(Environment.SpecialFolder.System);
                    var fixture=Directory.GetFiles(system,"*.exe").OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).Take(10).ToArray();
                    state=new ViewState{Ready=true,CanClose=true,AudioKnown=true,StatusZh="✓ 显示已就绪",StatusEn="✓ Display ready",Whitelist=fixture,
                        Applied=new FormValues{Minutes=3,Hotkey="Ctrl+Alt+F10",Language="zh-CN",AutomaticUpdates=true,LimitEnabled=true,LimitMinutes=60}};
                    nextOpen=1;
                    tick.Tick+=delegate {
                        double seconds=clock.Elapsed.TotalSeconds;
                        if(File.Exists(Path.Combine(output,"stop.flag")) || seconds>=duration)stopping=true;
                        if(!stopping && seconds>=nextCheck && display!=null){nextCheck=seconds+15;display.Recheck();}
                        if(!stopping && seconds>=nextPublish){nextPublish=seconds+5;Publish();}
                        if(!stopping && ui==null && opened<cycles && seconds>=nextOpen)Open();
                        if(ui!=null && !closing && currentShown && seconds>=closeAt){closing=true;ui.Close();Event("close_requested");}
                        if(ui!=null && !currentShown && seconds-nextOpen>15)throw new Exception("UI did not present within 15 seconds");
                        if(stopping && ui!=null && !closing){closing=true;ui.Close();}
                        if(seconds>=nextStatus){nextStatus=seconds+1;Status();}
                        if(stopping && ui==null){tick.Stop();Status();context.ExitThread();}
                    };
                    tick.Start();Application.Run(context);
                }
                if(!File.Exists(Path.Combine(output,"stop.flag")) && closed!=cycles)throw new Exception("Incomplete settings lifecycle cycles");
                Event("completed");File.WriteAllText(Path.Combine(output,"result.json"),json.Serialize(new{Status=File.Exists(Path.Combine(output,"stop.flag"))?"stopped":"completed",Seconds=clock.Elapsed.TotalSeconds,Opened=opened,Shown=shown,Closed=closed,Interrupted=interrupted}));
            }catch(Exception ex){if(output!=null)File.WriteAllText(Path.Combine(output,"failure.txt"),ex.ToString());result=1;}
            finally {
                if(ui!=null){ui.Close();ui.Dispose();}if(watchedUi!=null)watchedUi.Dispose();if(display!=null)display.Dispose();
                if(audio!=null){audio.Dispose();var worker=(Thread)typeof(AudioMonitor).GetField("worker",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(audio);bool ended=worker.Join(3000);if(!ended)result=2;if(output!=null)File.WriteAllText(Path.Combine(output,"audio-cleanup.txt"),"workerEnded="+ended);}
                if(events!=null)events.Dispose();
            }
            return result;
        }
        private static void Post(Action action){if(dispatcher!=null && !dispatcher.IsDisposed)dispatcher.BeginInvoke(action);}
        private static void Open()
        {
            opened++;closing=false;currentShown=false;SettingsPipeHost owner=null;
            owner=new SettingsPipeHost(Post,delegate(Packet packet,Action<Packet> done){
                if(packet.Kind=="save"){File.WriteAllText(Path.Combine(output,"fake-settings.json"),json.Serialize(packet.Values));done(new Packet());}
                else done(new Packet{Error="Test owner rejects real operations"});
            },delegate{if(ui!=owner)return;if(currentShown)throw new Exception("Duplicate presentation");currentShown=true;shown++;shownAt=clock.Elapsed.TotalSeconds;closeAt=shownAt+5;Event("shown");},delegate {
                if(ui!=owner)return;
                bool qualified=currentShown && closing && clock.Elapsed.TotalSeconds-shownAt>=5 && watchedUi!=null && watchedUi.HasExited && watchedUi.ExitCode==0;
                if(qualified)closed++;else {interrupted++;if(!stopping)throw new Exception("UI cycle failed presentation, requested-close, dwell or normal-exit check");}
                if(watchedUi!=null){watchedUi.Dispose();watchedUi=null;}lastClose=clock.Elapsed.TotalSeconds;owner.Dispose();ui=null;closing=false;Event("exited");
                nextOpen=clock.Elapsed.TotalSeconds+10;
                
                if(mode=="combined")nextOpen=duration-150;
            },value=>{});
            ui=owner;state.Applied.Language=opened%2==0?"en":"zh-CN";owner.Start(uiPath,state.Applied.Language);
            var created=(Process)typeof(SettingsPipeHost).GetField("child",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(owner);
            watchedUi=Process.GetProcessById(created.Id);var hold=watchedUi.Handle;
            if(!OwnedProcessIdentity.Matches(watchedUi,created.StartTime.ToUniversalTime().Ticks,uiPath))throw new Exception("UI identity mismatch");
            owner.Publish(json.Deserialize<ViewState>(json.Serialize(state)));Event("started");Status();
        }
        private static void Publish()
        {
            var snapshot=audio==null?new AudioSnapshot{Known=true,Sources=new AudioSource[0]}:audio.Latest;
            // No input inspection; a synthetic idle epoch exercises only pure timing.
            var protection=PlaybackProtection.Classify(snapshot,new PlaybackOptions{WhitelistPaths=state.Whitelist},(uint)Math.Min(uint.MaxValue,clock.ElapsedMilliseconds),false);
            var decision=timing.EvaluatePlayback((uint)Math.Min(uint.MaxValue,clock.ElapsedMilliseconds),protection);
            state.Version=++version;state.AudioKnown=snapshot.Known;
            state.Sources=(snapshot.Sources??new AudioSource[0]).Select(x=>new PlayingSource{Path=x.ExePath,Name=x.DisplayName}).ToArray();
            state.ProtectionZh=decision.Blocked?"自动关屏暂停（隔离判断）":"自动关屏计时（隔离判断）";state.ProtectionEn=decision.Blocked?"Paused (isolated decision)":"Counting (isolated decision)";
            // Each exported DTO is a distinct object: the host retains its latest.
            if(ui!=null)ui.Publish(json.Deserialize<ViewState>(json.Serialize(state)));
        }
        private static DisplaySetupResult ReadDisplay()
        {
            IntPtr old=GetThreadDesktop(GetCurrentThreadId()),target=OpenDesktop("Default",0,false,0x83);
            try {
                if(target==IntPtr.Zero || !SetThreadDesktop(target))throw new System.ComponentModel.Win32Exception();
                return DisplaySetupProbe.Probe(false);
            }catch(Exception ex){if(Interlocked.Increment(ref displayFailures)==1)try{string detail=ex.ToString();File.WriteAllText(Path.Combine(output,"first-display-failure.txt"),detail.Substring(0,Math.Min(4096,detail.Length)));}catch(IOException){}throw;}
            finally{Interlocked.Increment(ref displayReads);if(target!=IntPtr.Zero){SetThreadDesktop(old);CloseDesktop(target);}}
        }
        private static void Status()
        {
            Process child=ui==null?null:(Process)typeof(SettingsPipeHost).GetField("child",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(ui);
            int pid=0;long ticks=0;try{if(child!=null && !child.HasExited){pid=child.Id;ticks=child.StartTime.ToUniversalTime().Ticks;}}catch{}
            double elapsed=clock.Elapsed.TotalSeconds;
            var value=new{Seconds=elapsed,Phase=ui!=null?"settings":elapsed<300?"warmup":elapsed-lastClose<120?"cooldown":"background",UiPid=pid,UiTicks=ticks,UiPath=uiPath,
                LastCloseSeconds=lastClose,Opened=opened,Closed=closed,Shown=shown,AudioReads=Interlocked.Read(ref audioReads),DisplayReads=Interlocked.Read(ref displayReads),DisplayFailures=Interlocked.Read(ref displayFailures),
                AudioKnown=state.AudioKnown,Sources=state.Sources==null?0:state.Sources.Length,StatusDrops=statusDrops,Managed=GC.GetTotalMemory(false),Gc0=GC.CollectionCount(0),Gc1=GC.CollectionCount(1),Gc2=GC.CollectionCount(2)};
            try {File.WriteAllText(Path.Combine(output,"live.tmp"),json.Serialize(value));if(File.Exists(Path.Combine(output,"live.json")))File.Replace(Path.Combine(output,"live.tmp"),Path.Combine(output,"live.json"),null);else File.Move(Path.Combine(output,"live.tmp"),Path.Combine(output,"live.json"));}catch(IOException){statusDrops++;}
        }
        private static void Event(string name){if(events!=null)events.WriteLine(clock.Elapsed.TotalSeconds.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)+","+name+","+opened+","+closed);}
        private static int SelfTest()
        {
            int count=0;Action<Action> reject=action=>{bool denied=false;try{action();}catch(InvalidOperationException){denied=true;}catch(TargetInvocationException ex){denied=ex.InnerException is InvalidOperationException;}if(!denied)throw new Exception("Missing guard");count++;};
            reject(()=>{var x=Properties.Settings.Default;});reject(()=>OledPrototype.Native.RegisterHotKey(IntPtr.Zero,0,0,0));reject(()=>OledPrototype.Native.SetWindowsHookEx(0,null,IntPtr.Zero,0));
            reject(()=>OledPrototype.Native.SetThreadExecutionState(0));reject(()=>OledPrototype.Native.SetDisplayConfig(0,IntPtr.Zero,0,IntPtr.Zero,0x80));
            reject(()=>OledPrototype.DisplayBackend.Native.SetDisplayConfig(0,null,0,null,0x80));reject(()=>new OledPrototype.DisplayBackend().TurnOff());reject(()=>new OledPrototype.DisplayBackend().Restore());
            reject(()=>OledPrototype.DisplayBackend.Recover("test"));reject(()=>new OledPrototype.DisplayBackend().SaveRecovery("test"));
            reject(()=>typeof(ScreenOffTray.Program).GetMethod("Main",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{new string[0]}));
            Console.WriteLine("PASS "+count+" hard guards; no real configuration/input/display mutations");return 0;
        }
        private static string DesktopName(){var name=new StringBuilder(256);int needed;if(!GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()),2,name,512,out needed))throw new System.ComponentModel.Win32Exception();return name.ToString();}
        [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]private static extern IntPtr GetThreadDesktop(uint id);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool GetUserObjectInformation(IntPtr value,int index,StringBuilder text,int length,out int needed);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr OpenDesktop(string name,uint flags,bool inherit,uint access);
        [DllImport("user32.dll",SetLastError=true)]private static extern bool SetThreadDesktop(IntPtr desktop);
        [DllImport("user32.dll")]private static extern bool CloseDesktop(IntPtr desktop);
    }
}
