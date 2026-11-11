using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using ScreenOffTray;
using ScreenOffTray.SettingsTransport;

namespace OledPrototype {internal static class Program {internal static void Log(string text){throw new Exception("Test logger must be injected");}}}
internal static class SettingsPipeTests
{
    private static int checks;
    private static void Check(bool value,string name){if(!value)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
    private static int Main(string[] args)
    {
        if(args.Length==5 && args[0]=="--connect")return args[4]=="zh-CN"?RepairPeer(args):Peer(args);
        try {
            var posted=new BlockingCollection<Action>();var requestIds=new ConcurrentQueue<long>();int calls=0,shown=0,exited=0;
            var dispatcher=new Thread(delegate(){foreach(var work in posted.GetConsumingEnumerable())work();}){IsBackground=true};dispatcher.Start();
            using(var done=new ManualResetEvent(false))using(var host=new SettingsPipeHost(a=>posted.Add(a),delegate(Packet command,Action<Packet> complete){
                Interlocked.Increment(ref calls);requestIds.Enqueue(command.RequestId);ThreadPool.QueueUserWorkItem(delegate {
                    Thread.Sleep(40);hostStatePublish(command);complete(new Packet());complete(new Packet{Error="must be ignored"});
                });
            },()=>Interlocked.Increment(ref shown),delegate {Interlocked.Increment(ref exited);done.Set();},delegate{})) {
                active=host;host.Start(typeof(SettingsPipeTests).Assembly.Location,"en");
                Check(done.WaitOne(6000),"owned peer exits and lifetime callback is observed");
                Check(calls==1,"duplicate request executes once even while pending; observed IDs="+String.Join(",",requestIds.ToArray()));
                Check(shown==1 && exited==1,"shown and actual process exit remain distinct one-time notifications");
                Check(!host.IsAlive,"child ownership ends only after process exit");
                var count=(int)typeof(SettingsPipeHost).GetField("inFlight",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(host);
                Check(count==0,"double completion cannot decrement operation accounting twice");
            }
            posted.CompleteAdding();dispatcher.Join(1000);posted.Dispose();
            string receipt=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"peer-result.txt");Check(File.Exists(receipt) && File.ReadAllText(receipt)=="state-before-result;duplicate-safe","framed peer received current state before write acknowledgment");
            using(var failed=new SettingsPipeHost(a=>a(),(p,c)=>c(new Packet()),delegate{},delegate{},delegate{})) {
                bool rejected=false;try{failed.Start(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"does-not-exist.exe"),"en");}catch(System.ComponentModel.Win32Exception){rejected=true;}
                Check(rejected && !failed.IsAlive,"failed start retains no fake live child");
            }
            int repairedCalls=0;using(var repairedEnd=new ManualResetEvent(false)) {
                SettingsPipeHost repaired=null;
                using(repaired=new SettingsPipeHost(a=>ThreadPool.QueueUserWorkItem(delegate{a();}),(packet,complete)=>{
                    Interlocked.Increment(ref repairedCalls);
                    object gate=typeof(SettingsPipeHost).GetField("sync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(repaired);
                    lock(gate)typeof(SettingsPipeHost).GetMethod("BreakConnection",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic,null,Type.EmptyTypes,null).Invoke(repaired,null);
                    ThreadPool.QueueUserWorkItem(delegate {Thread.Sleep(100);repaired.Publish(new ViewState{Version=19});complete(new Packet());});
                },delegate{},()=>repairedEnd.Set(),delegate{})) {
                    repaired.Start(typeof(SettingsPipeTests).Assembly.Location,"zh-CN");
                    Check(repairedEnd.WaitOne(7000),"bounded reconnect peer exits normally");
                    Check(repairedCalls==1,"connection loss never resubmits an accepted write");
                    Check(File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"repair-result.txt")),"original request outcome is recovered after authenticated reconnect");
                }
            }
            int lateNotifications=0;
            var detached=new SettingsPipeHost(a=>{Interlocked.Increment(ref lateNotifications);throw new ObjectDisposedException("test dispatcher");},(p,c)=>c(new Packet()),delegate{},delegate{},delegate{});
            detached.Start(typeof(SettingsPipeTests).Assembly.Location,"en");detached.Dispose();
            Check(SpinWait.SpinUntil(()=>!detached.IsAlive,6000),"disposed host still observes its actual child exit");
            Check(lateNotifications==0,"disposed host detaches late owner notification without crashing parent");
            Console.WriteLine("Checks="+checks+"; dedicated fake peers only; no product Main, hooks, display or formal settings");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    private static SettingsPipeHost active;
    private static void hostStatePublish(Packet packet){active.Publish(new ViewState{Version=19,StatusEn="fake only"});}
    private static int RepairPeer(string[] args)
    {
        try {
            using(var client=new ScreenOffTray.SettingsUi.SettingsClient(args[1],Int32.Parse(args[2]),Int64.Parse(args[3]))) {
                int reconnects=0;client.Received+=packet=>{if(packet.Kind=="reconnected")Interlocked.Increment(ref reconnects);};client.StartReceiving();
                var result=client.Command(new Packet{Kind="save"});
                if(!result.Wait(5000) || result.Result.Error!=null || reconnects!=1)return 6;
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"repair-result.txt"),"one write, one reconnect, query original ID only");return 0;
            }
        }catch(Exception ex){File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"repair-failure.txt"),ex.ToString());return 1;}
    }
    private static int Peer(string[] args)
    {
        try {
            using(var pipe=new NamedPipeClientStream(".",args[1],PipeDirection.InOut)) {
                pipe.Connect(3000);Frames.Write(pipe,new Packet{Kind="hello"});Frames.Write(pipe,new Packet{Kind="shown"});
                Frames.Write(pipe,new Packet{Kind="save",RequestId=1});Frames.Write(pipe,new Packet{Kind="save",RequestId=1});
                bool state=false;for(int i=0;i<4;i++) {
                    Packet packet=Frames.Read(pipe);if(packet.Kind=="state")state=packet.State.Version==19;
                    if(packet.Kind=="result"){
                        if(!state || packet.Error!=null)return 4;
                        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"peer-result.txt"),"state-before-result;duplicate-safe");return 0;
                    }
                }
            }return 5;
        }catch(Exception ex){File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"peer-failure.txt"),ex.ToString());return 1;}
    }
}
