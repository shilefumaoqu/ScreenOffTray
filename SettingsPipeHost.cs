using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using ScreenOffTray.SettingsTransport;

namespace ScreenOffTray
{
    // Local, per-launch channel. All callbacks only dispatch to the controller;
    // no stream wait or serialization is performed on its input thread.
    internal sealed class SettingsPipeHost : IDisposable
    {
        private readonly object sync=new object();
        private readonly Queue<Packet> replies=new Queue<Packet>();
        private readonly AutoResetEvent signal=new AutoResetEvent(false);
        private readonly Action<Action> post;
        private readonly Action<Packet,Action<Packet>> command;
        private readonly Action shown,exited;
        private readonly Action<string> log;
        private readonly string pipeName="ScreenOffTray.Settings."+Guid.NewGuid().ToString("N");
        private readonly Dictionary<long,Packet> outcomes=new Dictionary<long,Packet>();
        private readonly Queue<long> completed=new Queue<long>();
        private NamedPipeServerStream pipe;
        private Process child;
        private Packet latest;
        private ViewState lastState;
        private bool disposed,connected,writerStarted,childExited,writerExited;
        private volatile bool captureFocused;
        private int inFlight;
        private long lastRequest;
        internal bool CaptureFocused {get{return captureFocused;} private set{captureFocused=value;}}
        internal bool IsAlive {get {lock(sync){try{return child!=null && !childExited && !child.HasExited;}catch{return child!=null && !childExited;}}}}
        internal SettingsPipeHost(Action<Action> post,Action<Packet,Action<Packet>> command,Action shown,Action exited,Action<string> log=null)
        {this.post=post;this.command=command;this.shown=shown;this.exited=exited;this.log=log??OledPrototype.Program.Log;}
        internal void Start(string executable,string language)
        {
            pipe=CreatePipe();
            try {
                using(var parent=Process.GetCurrentProcess()) {
                    var info=new ProcessStartInfo(executable,"--connect "+pipeName+" "+parent.Id+" "+parent.StartTime.ToUniversalTime().Ticks+" "+(language=="en"?"en":"zh-CN")){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(executable)};
                    child=Process.Start(info);if(child==null)throw new InvalidOperationException("Settings process did not start");
                }
                new Thread(Read){IsBackground=true,Name="ScreenOffTray.UiPipeRead"}.Start();
                new Thread(Write){IsBackground=true,Name="ScreenOffTray.UiPipeWrite"}.Start();writerStarted=true;
                new Thread(ObserveChildExit){IsBackground=true,Name="ScreenOffTray.UiLifetime"}.Start();
            }catch{Dispose();throw;}
        }
        private NamedPipeServerStream CreatePipe()
        {
            var security=new PipeSecurity();var owner=WindowsIdentity.GetCurrent().User;
            security.SetAccessRuleProtection(true,false);
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid,null),PipeAccessRights.FullControl,AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(owner,PipeAccessRights.FullControl,AccessControlType.Allow));
            return new NamedPipeServerStream(pipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4096,4096,security);
        }
        private void ObserveChildExit()
        {
            bool confirmed=false;
            try{child.WaitForExit();confirmed=true;}catch(Exception ex){TryLog("settings_wait_failed: "+ex.GetType().Name);try{confirmed=child.HasExited;}catch{}}
            if(!confirmed)return;
            bool notify;lock(sync){childExited=true;CaptureFocused=false;notify=!disposed;}
            try{child.Dispose();}catch(Exception ex){TryLog("settings_process_release_failed: "+ex.GetType().Name);}
            // Dispose detaches notifications. A callback already selected before
            // disposal still cannot escape a concurrently closed dispatcher.
            if(notify)try{post(exited);}catch(Exception ex){TryLog("settings_exit_dispatch_failed: "+ex.GetType().Name);}
        }
        internal void Publish(ViewState state)
        {lock(sync){if(disposed)return;lastState=state;latest=new Packet{Kind="state",State=state};signal.Set();}}
        internal void Send(Packet packet)
        {lock(sync){if(disposed)return;if(replies.Count>=64){BreakConnection();return;}replies.Enqueue(packet);signal.Set();}}
        internal void Activate(){Send(new Packet{Kind="activate"});}
        internal void Close(){Send(new Packet{Kind="close"});}
        internal void ForwardKey(int key,uint modifiers,bool up)
        {
            if(!CaptureFocused || !OwnsForeground())return;
            Send(new Packet{Kind="key",Key=key,Modifiers=modifiers,Up=up});
        }
        private bool OwnsForeground()
        {uint pid;GetWindowThreadProcessId(GetForegroundWindow(),out pid);lock(sync)return child!=null && !childExited && pid==(uint)child.Id;}
        private void Read()
        {
            // At most one reconnect, on this same read thread. A write operation
            // survives transport loss; the peer may query its existing ID only.
            for(int connection=0;connection<2;connection++) {
                NamedPipeServerStream current;lock(sync){if(disposed)return;current=pipe;}
                try {
                    current.WaitForConnection();uint pid;
                    if(!GetNamedPipeClientProcessId(current.SafePipeHandle.DangerousGetHandle(),out pid) || child==null || pid!=(uint)child.Id || child.HasExited)
                        throw new IOException("Settings client identity mismatch");
                    Packet hello=Frames.Read(current);if(hello.Kind!="hello")throw new IOException("Expected settings handshake");
                    lock(sync){if(disposed)return;connected=true;if(lastState!=null)latest=new Packet{Kind="state",State=lastState};signal.Set();}
                    for(;;) {
                        Packet packet=Frames.Read(current);
                        if(packet.Kind=="shown"){post(shown);continue;}
                        if(packet.Kind=="capture"){CaptureFocused=packet.Flag;continue;}
                        if(packet.RequestId<=0)throw new InvalidDataException("Settings command requires an ID");
                        bool dispatch=false;Packet previous;
                        lock(sync) {
                            if(outcomes.TryGetValue(packet.RequestId,out previous)){ }
                            else if(packet.Kind=="queryResult")previous=new Packet{Kind="result",RequestId=packet.RequestId,Error="No retained result. No write was resubmitted; read current state."};
                            else if(packet.RequestId<=lastRequest)previous=new Packet{Kind="result",RequestId=packet.RequestId,Error="Result expired; read current state before retrying."};
                            else {
                                lastRequest=packet.RequestId;
                                if(inFlight>=32)previous=new Packet{Kind="result",RequestId=packet.RequestId,Error="Settings command queue is busy."};
                                else {inFlight++;outcomes.Add(packet.RequestId,null);previous=null;dispatch=true;}
                            }
                        }
                        if(!dispatch){Send(previous??new Packet{Kind="pending",RequestId=packet.RequestId});continue;}
                        int completedOnce=0;
                        Action<Packet> finish=delegate(Packet result) {
                            if(Interlocked.Exchange(ref completedOnce,1)!=0)return;
                            result=result??new Packet{Error="No operation result"};result.RequestId=packet.RequestId;result.Kind="result";
                            lock(sync){inFlight--;outcomes[packet.RequestId]=result;completed.Enqueue(packet.RequestId);while(completed.Count>128)outcomes.Remove(completed.Dequeue());}
                            Send(result);
                        };
                        try{post(delegate {try{command(packet,finish);}catch(Exception ex){finish(new Packet{Error=ex.Message});}});}
                        catch(Exception ex){finish(new Packet{Error=ex.Message});}
                    }
                }catch(Exception ex){TryLog("settings_pipe_closed: "+ex.GetType().Name);lock(sync){BreakConnection(current);}}
                try{lock(sync){if(disposed || childExited || connection!=0)return;pipe=CreatePipe();}}catch(Exception ex){TryLog("settings_pipe_reopen_failed: "+ex.GetType().Name);return;}
            }
        }
        private void Write()
        {
            try {
                for(;;) {
                    signal.WaitOne();
                    for(;;) {
                        Packet packet;NamedPipeServerStream current;
                        lock(sync){if(disposed)return;if(!connected)break;current=pipe;if(latest!=null){packet=latest;latest=null;}else packet=replies.Count!=0?replies.Dequeue():null;if(packet==null)break;}
                        try{Frames.Write(current,packet);}
                        catch(InvalidDataException){Frames.Write(current,new Packet{Kind="error",Code="message_too_large",Error="Settings message exceeds the 4 MiB limit."});}
                        catch(Exception ex){TryLog("settings_pipe_write_failed: "+ex.GetType().Name);lock(sync){BreakConnection(current);}break;}
                    }
                }
            }catch(Exception ex){TryLog("settings_pipe_writer_stopped: "+ex.GetType().Name);}
            finally{lock(sync){writerExited=true;if(disposed)signal.Dispose();}}
        }
        private void TryLog(string value){try{log(value);}catch{}}
        private void BreakConnection(){BreakConnection(pipe);}
        private void BreakConnection(NamedPipeServerStream expected){if(!Object.ReferenceEquals(pipe,expected))return;connected=false;CaptureFocused=false;replies.Clear();try{if(expected!=null)expected.Dispose();}catch{}}
        public void Dispose()
        {
            lock(sync){if(disposed)return;disposed=true;BreakConnection();if(writerStarted && !writerExited)signal.Set();else signal.Dispose();replies.Clear();latest=null;lastState=null;}
            // The wait threads own no UI or recovery state; closing the pipe ends
            // stream waits. Process lifetime is observed, never killed here.
        }
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetNamedPipeClientProcessId(IntPtr pipe,out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    }
}
