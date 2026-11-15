using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ScreenOffTray.SettingsTransport;

namespace ScreenOffTray.SettingsUi
{
    internal sealed class SettingsClient : IDisposable
    {
        private readonly object sync=new object();
        private NamedPipeClientStream pipe;
        private readonly Process parent;
        private readonly string name;
        private readonly int parentId;
        private readonly long parentTicks;
        private readonly Queue<Packet> outgoing=new Queue<Packet>();
        private readonly Dictionary<long,TaskCompletionSource<Packet>> pending=new Dictionary<long,TaskCompletionSource<Packet>>();
        private readonly AutoResetEvent signal=new AutoResetEvent(false);
        private bool disposed,writerExited,terminal,connected;
        private int receiving;
        private long requestId;
        internal event Action<Packet> Received;
        internal event Action Disconnected;
        internal SettingsClient(string name,int pid,long ticks)
        {
            this.name=name;parentId=pid;parentTicks=ticks;
            try{parent=Process.GetProcessById(pid);var held=parent.Handle;pipe=Open(5000);connected=true;}
            catch{if(parent!=null)parent.Dispose();signal.Dispose();throw;}
            new Thread(Write){IsBackground=true,Name="ScreenOffTray.SettingsWrite"}.Start();
        }
        private NamedPipeClientStream Open(int timeout)
        {
            var value=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
            try {
                if(parent.StartTime.ToUniversalTime().Ticks!=parentTicks || parent.HasExited)throw new IOException("Backend identity changed");
                value.Connect(timeout);uint server;
                if(!GetNamedPipeServerProcessId(value.SafePipeHandle.DangerousGetHandle(),out server) || server!=(uint)parentId || parent.HasExited)throw new IOException("Backend pipe identity mismatch");
                Frames.Write(value,new Packet{Kind="hello"});return value;
            }catch{value.Dispose();throw;}
        }
        internal void StartReceiving(){if(Interlocked.Exchange(ref receiving,1)==0)new Thread(Read){IsBackground=true,Name="ScreenOffTray.SettingsRead"}.Start();}
        internal Task<Packet> Command(Packet packet)
        {
            var done=new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock(sync){
                if(disposed || terminal || !connected || pending.Count>=32){done.SetException(new IOException("Settings connection unavailable or busy"));return done.Task;}
                packet.RequestId=++requestId;pending.Add(packet.RequestId,done);
                try{Queue(packet);}catch(Exception ex){pending.Remove(packet.RequestId);done.TrySetException(ex);}
            }return done.Task;
        }
        internal void Notify(Packet packet){lock(sync){if(disposed || terminal || !connected)return;try{Queue(packet);}catch{Drop(pipe);}}}
        private void Queue(Packet packet){if(outgoing.Count>=64)throw new IOException("Settings outbound queue full");outgoing.Enqueue(packet);signal.Set();}
        private void Read()
        {
            for(int connection=0;connection<2;connection++) {
                NamedPipeClientStream current;lock(sync){if(disposed)return;current=pipe;}
                try {
                    for(;;){var packet=Frames.Read(current);
                        if(packet.Kind=="result") {
                            TaskCompletionSource<Packet> result;lock(sync){if(!pending.TryGetValue(packet.RequestId,out result))continue;pending.Remove(packet.RequestId);}result.TrySetResult(packet);
                        }else{var handler=Received;if(handler!=null)handler(packet);}
                    }
                }catch{Drop(current);}
                lock(sync){if(disposed)return;}
                if(connection!=0){Fail();return;}
                try {
                    var replacement=Open(2000);
                    lock(sync){
                        if(disposed){replacement.Dispose();return;}
                        pipe=replacement;connected=true;
                        // Never queue the original write again, including a command
                        // whose frame might have been only partly sent.
                        foreach(long id in pending.Keys.ToArray())Queue(new Packet{Kind="queryResult",RequestId=id});
                        signal.Set();
                    }
                    var handler=Received;if(handler!=null)handler(new Packet{Kind="reconnected"});
                }catch{Fail();return;}
            }
        }
        private void Write()
        {
            try {
                for(;;){signal.WaitOne();for(;;){
                    Packet packet;NamedPipeClientStream current;
                    lock(sync){if(disposed || terminal)return;if(!connected || outgoing.Count==0)break;current=pipe;packet=outgoing.Dequeue();}
                    try{Frames.Write(current,packet);}catch{Drop(current);break;}
                }}
            }catch{Fail();}
            finally{lock(sync){writerExited=true;if(disposed)signal.Dispose();}}
        }
        private void Drop(NamedPipeClientStream expected)
        {lock(sync){if(disposed || !Object.ReferenceEquals(pipe,expected))return;connected=false;outgoing.Clear();try{expected.Dispose();}catch{}}}
        private void Fail()
        {
            Action handler;
            lock(sync){if(disposed || terminal)return;terminal=true;connected=false;foreach(var task in pending.Values)task.TrySetException(new IOException("Backend connection lost; pending writes were not resent."));pending.Clear();outgoing.Clear();try{pipe.Dispose();}catch{}signal.Set();handler=Disconnected;}
            try{if(handler!=null)handler();}catch{}
        }
        public void Dispose()
        {
            lock(sync){if(disposed)return;disposed=true;foreach(var task in pending.Values)task.TrySetCanceled();pending.Clear();outgoing.Clear();pipe.Dispose();parent.Dispose();if(writerExited)signal.Dispose();else signal.Set();}
        }
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetNamedPipeServerProcessId(IntPtr pipe,out uint pid);
    }
    // Application lifetime belongs to the entry point, including initialization
    // and error dialogs before a transport connection exists.
    internal sealed class BackendLifetime : IDisposable
    {
        private readonly object sync=new object();
        private readonly Process parent;
        private bool disposed;
        internal BackendLifetime(int pid,long ticks)
        {
            try {
                parent=Process.GetProcessById(pid);
                var held=parent.Handle;
                if(parent.HasExited || parent.StartTime.ToUniversalTime().Ticks!=ticks)throw new IOException("Backend identity changed");
                new Thread(Watch){IsBackground=true,Name="ScreenOffTray.BackendLifetime"}.Start();
            }catch{if(parent!=null)parent.Dispose();throw;}
        }
        internal bool IsAlive {get {lock(sync){if(disposed)return false;try{return !parent.HasExited;}catch{return false;}}}}
        private void Watch()
        {
            bool ended=false;try{parent.WaitForExit();ended=true;}catch{lock(sync){if(disposed)return;try{ended=parent.HasExited;}catch{}}}
            lock(sync){if(disposed || !ended)return;}
            Environment.Exit(0); // Only this UI process; no authority over its backend.
        }
        public void Dispose(){lock(sync){if(disposed)return;disposed=true;parent.Dispose();}}
    }

}
