using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using ScreenOffTray.Playback;
using ScreenOffTray.Ui;

namespace ScreenOffTray.Tests
{
    // Test-only entry. Never invokes the product Main or tray coordinator.
    internal static class MemoryIsolationProbe
    {
        internal static long AudioReads, AudioUnknowns, MediaReads, MediaUnknowns, DisplayReads, ReadFailures, MediaNameEnumerations;
        internal static int MediaSessions=-1, MediaPlaying=-1, MediaPaused=-1;
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static readonly List<WeakReference> windows = new List<WeakReference>();
        private static readonly string[] groups = { "base", "audio", "media", "playback", "display", "settings", "combined", "attribution" };
        private static string output, group;
        private static int seconds, cycles, opened, closed, releaseFailures;
        private static double closeAt = -1000, nextWindow;
        private static SettingsDialog window;
        private static IAudioMonitor audio;
        private static WinRtMediaStateReader media;
        private static Thread displayWorker;
        private static readonly ManualResetEvent stop = new ManualResetEvent(false);
        private static IntPtr workingSetBuffer;
        private static int workingSetBufferBytes;
        private static StreamWriter samples;
        private static StreamWriter lifecycle;
        private static long paints, layouts;
        private static string[] fixturePaths;

        [STAThread] private static int Main(string[] args)
        {
            int result=0;
            try {
                if (args.Length == 1 && args[0] == "--self-test") return SelfTest();
                if (args.Length != 4) throw new ArgumentException("output group seconds cycles");
                output = Path.GetFullPath(args[0]); group = args[1];
                seconds = Int32.Parse(args[2]); cycles = Int32.Parse(args[3]);
                if (Array.IndexOf(groups, group) < 0 || seconds < 10 || seconds > 87000 || cycles < 0 || cycles > 21)
                    throw new ArgumentException("Bounded test arguments required");
                Directory.CreateDirectory(output);
                // A generated, isolated copy contains the hard native/configuration guards.
                if (typeof(OledPrototype.Native).GetField("MemoryIsolationGuard") == null)
                    throw new InvalidOperationException("Guarded diagnostic build required");
                File.WriteAllText(Path.Combine(output, "identity.txt"), Process.GetCurrentProcess().Id + "\n" +
                    Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("o") + "\n" + Assembly.GetExecutingAssembly().Location);
                var desktopName = new System.Text.StringBuilder(256); int required;
                if (!GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()),2,desktopName,512,out required) ||
                    !desktopName.ToString().StartsWith("ScreenOffMemory-",StringComparison.Ordinal))
                    throw new InvalidOperationException("Launcher must select a private non-input desktop before CLR/STA startup");
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                ResizeWorkingSetBuffer(256*1024);
                samples = new StreamWriter(Path.Combine(output, "samples.csv"), false, System.Text.Encoding.UTF8);
                samples.AutoFlush = true;
                lifecycle = new StreamWriter(Path.Combine(output,"lifecycle.csv"),false,System.Text.Encoding.UTF8) { AutoFlush=true };
                lifecycle.WriteLine("seconds,event,wallMs,cpuMs,paints,layouts");
                var paths = new List<string>(Directory.GetFiles(Environment.GetFolderPath(Environment.SpecialFolder.System),"*.exe"));
                paths.Sort(StringComparer.OrdinalIgnoreCase); fixturePaths=paths.GetRange(0,Math.Min(10,paths.Count)).ToArray();
                samples.WriteLine("seconds,phase,privateResident,workingSet,privateCommit,cpuMs,handles,threads,gdi,user,readBytes,writeBytes,managedEstimate,gc0,gc1,gc2,audioReads,displayReads,displayFailures,audioKnown,sources,mediaSessions,mediaPlaying,mediaPaused,windowOpen,opened,closed,weakAlive,releaseFailures,dpi,queryMaxMs,audioUnknowns,mediaReads,mediaUnknowns,mediaNameEnumerations");
                var context = new ApplicationContext();
                using (var menu = new ContextMenuStrip()) using (var tray = new NotifyIcon { Icon = SystemIcons.Application, ContextMenuStrip = menu, Visible = false })
                using (var tick = new System.Windows.Forms.Timer { Interval = 100 }) {
                    menu.Items.Add("Isolated test", null, delegate { });
                    if (group == "audio") audio = new AudioMonitor(delegate { return new CoreAudioReader(); }, 5000, 3000);
                    if (group == "media") {
                        media = new WinRtMediaStateReader(null);
                        audio = new AudioMonitor(delegate { return ReadMedia(); }, delegate { media.Dispose(); }, 5000, 3000);
                    }
                    if (group == "playback" || group == "combined" || group == "attribution") audio = new AudioMonitor();
                    if (group == "display" || group == "combined" || group == "attribution") {
                        displayWorker = new Thread(DisplayLoop) { IsBackground = true, Name = "IsolatedReadOnlyDisplay" };
                        displayWorker.Start();
                    }
                    bool settings = group == "settings";
                    nextWindow = settings ? 0 : 5;
                    double nextSample = 0;
                    var rolling = new Queue<CpuPoint>(); // at most seven samples
                    double highWorkingSetSince = -1;
                    string end = "duration";
                    tick.Tick += delegate {
                        double elapsed = clock.Elapsed.TotalSeconds;
                        if (settings || group == "combined" || group == "attribution") RunWindows(elapsed, settings);
                        string inventory=Path.Combine(output,"inventory.flag");
                        if(File.Exists(inventory)) { File.Delete(inventory); DumpImageInventory(); }
                        if (elapsed >= nextSample) {
                            nextSample = elapsed + 10;
                            Sample(elapsed, rolling, ref highWorkingSetSince, ref end);
                        }
                        if (File.Exists(Path.Combine(output, "stop.flag"))) end = "requested";
                        bool lifecycleComplete = settings && closed == cycles && window == null && elapsed-closeAt >= 600;
                        if (end == "duration" && LifecycleTimedOut(settings,lifecycleComplete,elapsed,seconds)) { end="lifecycle-timeout"; result=3; }
                        if (lifecycleComplete || elapsed >= seconds || end != "duration") {
                            tick.Stop();
                            File.WriteAllText(Path.Combine(output, "end-reason.txt"), end);
                            if (window != null) CloseWindow();
                            context.ExitThread();
                        }
                    };
                    tick.Start(); Application.Run(context);
                }
            } catch (Exception ex) {
                if (output != null) File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString());
                Console.Error.WriteLine(ex); result=1;
            } finally {
                stop.Set();
                if (window != null) { window.Dispose(); window = null; }
                bool workerReleased = true;
                if (audio != null) {
                    audio.Dispose();
                    var thread = (Thread)typeof(AudioMonitor).GetField("worker",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(audio);
                    workerReleased &= thread.Join(3000);
                }
                if (displayWorker != null) workerReleased &= displayWorker.Join(1000);
                if (output != null) File.WriteAllText(Path.Combine(output,"cleanup.txt"),"workers_completed="+workerReleased+"; window_open="+(window!=null)+"; process exit releases private desktop and framework message windows");
                if (samples != null) samples.Dispose();
                if (lifecycle != null) lifecycle.Dispose();
                if (workingSetBuffer != IntPtr.Zero) Marshal.FreeHGlobal(workingSetBuffer);
                // The supervisor closes its owned desktop after this process exits.
                if(!workerReleased)result=2;
            }
            return result;
        }

        private static int SelfTest()
        {
            if (typeof(OledPrototype.Native).GetField("MemoryIsolationGuard") == null) throw new Exception("Missing guards");
            int checks = 0;
            Action<Action> rejects = action => { bool denied = false; try { action(); } catch (InvalidOperationException) { denied = true; } if (!denied) throw new Exception("Unsafe operation not blocked"); checks++; };
            rejects(() => { var x = Properties.Settings.Default; });
            rejects(() => OledPrototype.Native.RegisterHotKey(IntPtr.Zero, 0, 0, 0));
            rejects(() => OledPrototype.Native.SetThreadExecutionState(0));
            rejects(() => OledPrototype.Native.SetWindowsHookEx(0, null, IntPtr.Zero, 0));
            rejects(() => OledPrototype.Native.SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, 0x80));
            rejects(() => OledPrototype.DisplayBackend.Native.SetDisplayConfig(0, null, 0, null, 0x80));
            rejects(() => new OledPrototype.DisplayBackend().TurnOff());
            rejects(() => new OledPrototype.DisplayBackend().Restore());
            rejects(() => OledPrototype.DisplayBackend.Recover("not-a-file"));
            rejects(() => new OledPrototype.DisplayBackend().SaveRecovery("not-a-file"));
            var test = new Queue<CpuPoint>(); double ws = -1; string reason = "duration";
            Guard(513L*1024*1024, 0, 0, 0, test, ref ws, ref reason);
            if (reason != "private-commit-limit") throw new Exception("Commit guard"); checks++;
            reason = "duration"; Guard(0,257L*1024*1024,0,0,test,ref ws,ref reason);
            Guard(0,257L*1024*1024,60,0,test,ref ws,ref reason);
            if (reason != "working-set-limit") throw new Exception("Working set guard"); checks++;
            reason = "duration"; test.Clear(); ws = -1;
            Guard(0,0,0,0,test,ref ws,ref reason);
            Guard(0,0,60,60000*Environment.ProcessorCount*0.02,test,ref ws,ref reason);
            if (reason != "cpu-limit") throw new Exception("CPU guard"); checks++;
            Console.WriteLine("PASS " + checks + " isolation and resource guard checks; no real display/input/configuration operations");
            if(!LifecycleTimedOut(true,false,1200,1200) || LifecycleTimedOut(true,true,1200,1200) || LifecycleTimedOut(false,false,1200,1200) || LifecycleTimedOut(true,false,1199,1200)) throw new Exception("Lifecycle deadline classification failed");
            Console.WriteLine("PASS 4 lifecycle deadline classification checks");
            if(NextWorkingSetBufferSize(256*1024,24)!=512*1024 || NextWorkingSetBufferSize(2*1024*1024,24)!=4*1024*1024)throw new Exception("Buffer growth bounds");
            foreach(int error in new[]{5,24}) {bool rejected=false;try{NextWorkingSetBufferSize(4*1024*1024,error);}catch(System.ComponentModel.Win32Exception){rejected=true;}if(!rejected)throw new Exception("Buffer error/maximum not enforced");}
            Console.WriteLine("PASS 4 bounded working-set buffer growth checks");
            return 0;
        }
        private static bool LifecycleTimedOut(bool settings,bool complete,double elapsed,int limit) { return settings && !complete && elapsed>=limit; }

        private static AudioSnapshot ReadMedia()
        {
            MediaStateSnapshot state = media.Read();
            int playing = 0, paused = 0;
            foreach (var item in state.Sessions) { if (item.State == MediaSessionPlayback.Playing) playing++; if (item.State == MediaSessionPlayback.Paused) paused++; }
            MediaSessions = state.Sessions.Length; MediaPlaying = playing; MediaPaused = paused;
            return new AudioSnapshot { Known = state.Known, Sources = new AudioSource[0], SampleUtc = DateTime.UtcNow };
        }
        private static void DisplayLoop()
        {
            // VALIDATE requires the interactive desktop even without APPLY. Only
            // this read-only MTA worker binds to Default; UI stays on the private
            // desktop. No input/switch-desktop rights are requested.
            IntPtr previous=GetThreadDesktop(GetCurrentThreadId());
            IntPtr queryDesktop=OpenDesktop("Default",0,false,0x83);
            if(queryDesktop==IntPtr.Zero || !SetThreadDesktop(queryDesktop)) {
                Interlocked.Increment(ref ReadFailures);
                File.WriteAllText(Path.Combine(output,"first-display-failure.txt"),"Read-only query desktop unavailable: "+Marshal.GetLastWin32Error());
                if(queryDesktop!=IntPtr.Zero)CloseDesktop(queryDesktop);return;
            }
            try {
            do {
                var watch = Stopwatch.StartNew();
                try { DisplaySetupProbe.Probe(false); }
                catch (Exception ex) {
                    if (Interlocked.Increment(ref ReadFailures)==1) {
                        string detail=ex.ToString();
                        File.WriteAllText(Path.Combine(output,"first-display-failure.txt"),detail.Substring(0,Math.Min(detail.Length,4096)));
                    }
                }
                finally { ObserveQuery(watch.ElapsedMilliseconds); Interlocked.Increment(ref DisplayReads); }
            } while (!stop.WaitOne(15000));
            } finally { SetThreadDesktop(previous);CloseDesktop(queryDesktop); }
        }
        internal static long QueryMaxMs;
        internal static void ObserveMedia(MediaSessionStatus[] sessions)
        {
            if(sessions==null){MediaSessions=MediaPlaying=MediaPaused=-1;return;}
            int playing=0,paused=0;
            foreach(var item in sessions){if(item.State==MediaSessionPlayback.Playing)playing++;if(item.State==MediaSessionPlayback.Paused)paused++;}
            MediaPlaying=playing;MediaPaused=paused;MediaSessions=sessions.Length;
        }
        internal static void ObserveQuery(long value) { long old; do { old = Interlocked.Read(ref QueryMaxMs); if (value <= old) return; } while (Interlocked.CompareExchange(ref QueryMaxMs,value,old) != old); }

        private static void RunWindows(double elapsed, bool repeated)
        {
            if(group=="attribution") {
                string close=Path.Combine(output,"close.flag"), open=Path.Combine(output,"open.flag");
                if(File.Exists(close)) { File.Delete(close); if(window!=null)CloseWindow(); return; }
                if(window!=null || !File.Exists(open))return;
                File.Delete(open);nextWindow=0;
            }
            if (window != null && elapsed >= nextWindow) CloseWindow();
            if (window != null || elapsed < nextWindow) return;
            // One warmup plus 20 measured cycles; combined opens once at either end.
            bool opening = group=="attribution" || (repeated ? opened < cycles : opened == 0 || (opened == 1 && elapsed >= seconds - 30));
            if (!opening) return;
            using (SettingsDpiContext.EnterUi()) {
                var options = new PlaybackOptions(); options.WhitelistPaths = fixturePaths;
                var watch=Stopwatch.StartNew(); double cpu=CpuMilliseconds();
                var state = new PlaybackPanelState { Audio = new AudioSnapshot { Known=true, Sources=new AudioSource[0], SampleUtc=DateTime.UtcNow }, AppliedOptions=options, Timing=new AutoOffTimingResult(0,AutoOffPauseReason.None) };
                window = new SettingsDialog(new UiText("zh-CN"),new SettingsValues {TimeoutMinutes=3,Language="zh-CN",Hotkey=HotkeyBinding.Default},
                    v=>null, ()=>{throw new InvalidOperationException("No setup in diagnostics");},()=>{},()=>{},()=>{},()=>{},"isolated-memory",
                    ()=>state,()=>{},p=>{ options.WhitelistPaths=p; return null; });
                window.ConfigureWindow(()=>{}); window.RefreshSetup(new SetupPanelState {Ready=true});
                ObserveControls(window);
                window.Show();
                if (opened == 1) { window.ClientSize = new Size(750,800); }
                window.Update();
                lifecycle.WriteLine(clock.Elapsed.TotalSeconds.ToString(CultureInfo.InvariantCulture)+",open,"+watch.ElapsedMilliseconds+","+(CpuMilliseconds()-cpu).ToString(CultureInfo.InvariantCulture)+","+paints+","+layouts);
                opened++; nextWindow = clock.Elapsed.TotalSeconds + 5;
            }
        }
        private static void CloseWindow()
        {
            var watch=Stopwatch.StartNew(); double cpu=CpuMilliseconds();
            using (SettingsDpiContext.EnterUi()) {
                SettingsDialog old = window; window = null;
                old.Close(); if (!old.IsDisposed) { releaseFailures++; old.Dispose(); }
                windows.Add(new WeakReference(old)); closed++; closeAt = clock.Elapsed.TotalSeconds;
                nextWindow=closeAt+(closed==1 && group=="settings" ? 300 : 10);
                lifecycle.WriteLine(clock.Elapsed.TotalSeconds.ToString(CultureInfo.InvariantCulture)+",close,"+watch.ElapsedMilliseconds+","+(CpuMilliseconds()-cpu).ToString(CultureInfo.InvariantCulture)+","+paints+","+layouts);
            }
        }
        private static double CpuMilliseconds() { using(var p=Process.GetCurrentProcess()) return p.TotalProcessorTime.TotalMilliseconds; }
        private static void ResizeWorkingSetBuffer(int bytes) {
            if(bytes<256*1024 || bytes>4*1024*1024)throw new InvalidOperationException("Bounded diagnostic buffer required");
            IntPtr next=Marshal.AllocHGlobal(bytes),old=workingSetBuffer;
            workingSetBuffer=next;workingSetBufferBytes=bytes;if(old!=IntPtr.Zero)Marshal.FreeHGlobal(old);
            File.WriteAllText(Path.Combine(output,"native-allocations.json"),
                "[{\"role\":\"diagnostic-working-set-buffer\",\"address\":\""+workingSetBuffer.ToInt64().ToString("X")+"\",\"bytes\":"+bytes+"}]");
        }
        private static int NextWorkingSetBufferSize(int current,int error) {
            if(error!=24 || current>=4*1024*1024)throw new System.ComponentModel.Win32Exception(error);
            return Math.Min(current*2,4*1024*1024);
        }
        private static void DumpImageInventory() {
            var images=new HashSet<Image>();var rows=new List<object>();
            if(window!=null) InventoryControls(window,images,rows);
            var json=new System.Web.Script.Serialization.JavaScriptSerializer();
            long cacheBytes=0;var cacheType=typeof(Process).Assembly.GetType("System.Diagnostics.NtProcessInfoHelper");
            var cacheField=cacheType==null?null:cacheType.GetField("CachedBuffer",BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public);
            var cache=cacheField==null?null:cacheField.GetValue(null) as long[];if(cache!=null)cacheBytes=24+cache.LongLength*8;
            File.WriteAllText(Path.Combine(output,"images-"+clock.ElapsedMilliseconds+".json"),json.Serialize(new { WindowOpen=window!=null,Rows=rows,FrameworkProcessBufferBytes=cacheBytes,MediaNameEnumerations=Interlocked.Read(ref MediaNameEnumerations) }));
        }
        private static void InventoryImage(Image image,string owner,HashSet<Image> images,List<object> rows) {
            if(image==null || !images.Add(image))return;
            int bits=Image.GetPixelFormatSize(image.PixelFormat);
            rows.Add(new {Owner=owner,Width=image.Width,Height=image.Height,PixelFormat=image.PixelFormat.ToString(),
                PixelPayloadEstimate=((long)image.Width*bits+31)/32*4*image.Height});
        }
        private static void InventoryControls(Control control,HashSet<Image> images,List<object> rows) {
            for(Type type=control.GetType();type!=null;type=type.BaseType) {
                foreach(var field in type.GetFields(BindingFlags.Instance|BindingFlags.DeclaredOnly|BindingFlags.Public|BindingFlags.NonPublic)) {
                    if(typeof(Image).IsAssignableFrom(field.FieldType)) InventoryImage(field.GetValue(control) as Image,type.FullName+"."+field.Name,images,rows);
                    if(field.FieldType==typeof(ApplicationIconCache)) {
                        object cache=field.GetValue(control); if(cache==null)continue;
                        var gate=typeof(ApplicationIconCache).GetField("gate",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(cache);
                        lock(gate) {
                            var entries=(System.Collections.IDictionary)typeof(ApplicationIconCache).GetField("entries",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(cache);
                            foreach(object entry in entries.Values) {
                                var imageField=entry.GetType().GetField("Image",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
                                if(imageField!=null)InventoryImage(imageField.GetValue(entry) as Image,"ApplicationIconCache.Image",images,rows);
                            }
                        }
                    }
                }
            }
            foreach(Control child in control.Controls)InventoryControls(child,images,rows);
        }
        private static void ObserveControls(Control c) {
            c.Paint += delegate { paints++; }; c.Layout += delegate { layouts++; };
            foreach(Control child in c.Controls) ObserveControls(child);
        }
        private sealed class CpuPoint { internal double Seconds,Cpu; }
        private static void Guard(long commit,long working,double elapsed,double cpu,Queue<CpuPoint> rolling,ref double highSince,ref string reason)
        {
            if (commit > 512L*1024*1024) reason = "private-commit-limit";
            if (working > 256L*1024*1024) { if (highSince < 0) highSince = elapsed; if (elapsed-highSince >= 60) reason = "working-set-limit"; } else highSince=-1;
            rolling.Enqueue(new CpuPoint {Seconds=elapsed,Cpu=cpu});
            while (rolling.Count>1 && elapsed-rolling.ToArray()[1].Seconds>=60) rolling.Dequeue();
            if (rolling.Count>9) throw new InvalidOperationException("Bounded CPU observation queue exceeded");
            CpuPoint first=rolling.Peek(); double duration=elapsed-first.Seconds;
            if (duration>=60 && 100*(cpu-first.Cpu)/(duration*1000*Environment.ProcessorCount)>1) reason="cpu-limit";
        }
        private static void Sample(double elapsed,Queue<CpuPoint> rolling,ref double highSince,ref string reason)
        {
            using (Process p=Process.GetCurrentProcess()) {
                p.Refresh(); Counters memory; IoCounters io;
                if (!GetProcessMemoryInfo(p.Handle,out memory,(uint)Marshal.SizeOf(typeof(Counters))) || !GetProcessIoCounters(p.Handle,out io)) throw new System.ComponentModel.Win32Exception();
                while(!QueryWorkingSet(p.Handle,workingSetBuffer,workingSetBufferBytes)) {
                    int error=Marshal.GetLastWin32Error();
                    ResizeWorkingSetBuffer(NextWorkingSetBufferSize(workingSetBufferBytes,error));
                }
                long entries=Marshal.ReadIntPtr(workingSetBuffer).ToInt64(), privatePages=0;
                if(entries<0 || entries>(workingSetBufferBytes-IntPtr.Size)/IntPtr.Size) throw new Exception("Working set buffer bounds");
                for(int i=0;i<entries;i++) if((Marshal.ReadIntPtr(workingSetBuffer,(i+1)*IntPtr.Size).ToInt64()&0x100)==0) privatePages++;
                long commit=(long)memory.PrivateUsage.ToUInt64(), working=(long)memory.Working.ToUInt64();
                Guard(commit,working,elapsed,p.TotalProcessorTime.TotalMilliseconds,rolling,ref highSince,ref reason);
                AudioSnapshot snapshot=audio==null?null:audio.Latest;
                int alive=0; foreach(var weak in windows) if(weak.IsAlive) alive++;
                string phase=window!=null?"settings-open":elapsed<300?"warmup":elapsed-closeAt<120?"cooldown":"background";
                uint handles;if(!GetProcessHandleCount(p.Handle,out handles))throw new System.ComponentModel.Win32Exception();
                // Attribution mode measures threads externally: Process.Threads on
                // Framework introduces a whole-machine process-info cache into the subject.
                int threads=group=="attribution"?-1:p.Threads.Count;
                samples.WriteLine(String.Join(",", new object[] {elapsed.ToString("F3",CultureInfo.InvariantCulture),phase,
                    privatePages*Environment.SystemPageSize,working,commit,p.TotalProcessorTime.TotalMilliseconds.ToString(CultureInfo.InvariantCulture),handles,threads,
                    GetGuiResources(p.Handle,0),GetGuiResources(p.Handle,1),io.ReadBytes,io.WriteBytes,GC.GetTotalMemory(false),GC.CollectionCount(0),GC.CollectionCount(1),GC.CollectionCount(2),
                    Interlocked.Read(ref AudioReads),Interlocked.Read(ref DisplayReads),Interlocked.Read(ref ReadFailures),snapshot==null?-1:snapshot.Known?1:0,snapshot==null||snapshot.Sources==null?0:snapshot.Sources.Length,
                    MediaSessions,MediaPlaying,MediaPaused,window!=null?1:0,opened,closed,alive,releaseFailures,window==null?0:GetDpiForWindow(window.Handle),Interlocked.Read(ref QueryMaxMs),Interlocked.Read(ref AudioUnknowns),Interlocked.Read(ref MediaReads),Interlocked.Read(ref MediaUnknowns),Interlocked.Read(ref MediaNameEnumerations)}));
            }
        }
        [StructLayout(LayoutKind.Sequential)] private struct Counters { internal uint Size,Faults; internal UIntPtr PeakWorking,Working,PeakPaged,Paged,PeakNonpaged,Nonpaged,Pagefile,PeakPagefile,PrivateUsage; }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { internal ulong Reads,Writes,Other,ReadBytes,WriteBytes,OtherBytes; }
        [DllImport("psapi.dll",SetLastError=true)] private static extern bool GetProcessMemoryInfo(IntPtr process,out Counters counters,uint size);
        [DllImport("psapi.dll",SetLastError=true)] private static extern bool QueryWorkingSet(IntPtr process,IntPtr buffer,int size);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetProcessIoCounters(IntPtr process,out IoCounters io);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetProcessHandleCount(IntPtr process,out uint count);
        [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process,int flag);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr OpenDesktop(string name,uint flags,bool inherit,uint access);
        [DllImport("user32.dll",SetLastError=true)] private static extern bool SetThreadDesktop(IntPtr desktop);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool GetUserObjectInformation(IntPtr handle,int index,System.Text.StringBuilder value,int bytes,out int required);
    }
}
