using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using ScreenOffTray.SettingsTransport;

namespace ScreenOffTray
{
    // Installer-only broker, run as the original user. Exit codes distinguish
    // actual initialization from Process.Start success. It never kills the child.
    internal static class VerifiedLaunch
    {
        internal const int Ready=0,NeedsConfiguration=10,Failed=11;
        internal static bool ValidReportArguments(string[] args)
        {
            Guid id;int pid;long ticks;
            return args!=null && (args.Length==4 || (args.Length==5 && args[4]=="--setup")) && args[0]=="--startup-report" &&
                Guid.TryParseExact(args[1],"N",out id) && Int32.TryParse(args[2],out pid) && pid>0 && Int64.TryParse(args[3],out ticks) && ticks>0;
        }
        internal static int Run(bool setup)
        {return Run(typeof(Program).Assembly.Location,setup,15000);}
        // Alternate executable and shorter deadline are test-only call sites.
        internal static int Run(string executable,bool setup,int deadlineMilliseconds)
        {
            if(deadlineMilliseconds<1 || deadlineMilliseconds>15000)throw new ArgumentOutOfRangeException("deadlineMilliseconds");
            string id=Guid.NewGuid().ToString("N");var clock=Stopwatch.StartNew();
            var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid,null),PipeAccessRights.FullControl,AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,PipeAccessRights.FullControl,AccessControlType.Allow));
            using(var pipe=new NamedPipeServerStream("ScreenOffTray.Startup."+id,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4096,4096,security))
            using(var parent=Process.GetCurrentProcess()) {
                string arguments="--startup-report "+id+" "+parent.Id+" "+parent.StartTime.ToUniversalTime().Ticks+(setup?" --setup":"");
                Task<int> work=Task.Run(delegate {
                    using(var child=Process.Start(new ProcessStartInfo(executable,arguments){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(executable)})) {
                        if(child==null)return Failed;
                        child.EnableRaisingEvents=true;
                        child.Exited+=delegate{try{pipe.Dispose();}catch{}};
                        if(child.HasExited)return Failed;
                        pipe.WaitForConnection();uint peer;
                        if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out peer) || peer!=(uint)child.Id || child.HasExited)
                            throw new IOException("Startup report identity mismatch");
                        Packet result=Frames.Read(pipe);
                        if(result.Kind!="startup" || child.HasExited)return Failed;
                        if(result.Code=="ready" && (!setup || result.Flag))return Ready;
                        if(result.Code=="needs_configuration" && result.Flag)return NeedsConfiguration;
                        return Failed;
                    }
                });
                try {
                    int remaining=(int)Math.Max(0,deadlineMilliseconds-clock.ElapsedMilliseconds);
                    return work.Wait(remaining)?work.GetAwaiter().GetResult():Failed;
                }catch(Exception ex){OledPrototype.Program.Log("startup_verification_failed: "+ex.GetType().Name);return Failed;}
                finally {
                    pipe.Dispose(); // Cancels only the reporting channel, never the product.
                    work.ContinueWith(task=>{if(task.IsFaulted){var observed=task.Exception;}},TaskScheduler.Default);
                }
            }
        }
        internal static bool BrokerStillAlive(string[] args)
        {
            if(!ValidReportArguments(args))return false;
            try{using(var broker=Process.GetProcessById(Int32.Parse(args[2])))return !broker.HasExited && broker.StartTime.ToUniversalTime().Ticks==Int64.Parse(args[3]) &&
                String.Equals(Path.GetFullPath(broker.MainModule.FileName),Path.GetFullPath(typeof(Program).Assembly.Location),StringComparison.OrdinalIgnoreCase);}
            catch{return false;}
        }
        internal static void Report(string[] args,bool ready,bool windowShown)
        {
            if(!ValidReportArguments(args))return;
            Task.Run(delegate {
                try {
                    int pid=Int32.Parse(args[2]);long ticks=Int64.Parse(args[3]);
                    using(var parent=Process.GetProcessById(pid))using(var pipe=new NamedPipeClientStream(".","ScreenOffTray.Startup."+args[1],PipeDirection.Out,PipeOptions.Asynchronous)) {
                        if(parent.HasExited || parent.StartTime.ToUniversalTime().Ticks!=ticks)throw new IOException("Startup broker identity changed");
                        pipe.Connect(2000);uint server;
                        if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out server) || server!=(uint)pid || parent.HasExited)
                            throw new IOException("Startup broker pipe identity mismatch");
                        Frames.Write(pipe,new Packet{Kind="startup",Code=ready?"ready":"needs_configuration",Flag=windowShown});
                    }
                }catch(Exception ex){OledPrototype.Program.Log("startup_report_unavailable: "+ex.GetType().Name);}
            });
        }
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetNamedPipeClientProcessId(IntPtr pipe,out uint pid);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetNamedPipeServerProcessId(IntPtr pipe,out uint pid);
    }
}
