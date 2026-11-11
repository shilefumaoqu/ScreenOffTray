using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
public sealed class ExternalResourceCounter:IDisposable
{
    private IntPtr buffer;private int size=262144;
    public ExternalResourceCounter(){buffer=Marshal.AllocHGlobal(size);}
    public ResourceSample Read(Process process,long ticks,string path)
    {
        process.Refresh();if(!OwnedProcessIdentity.Matches(process,ticks,path))throw new InvalidOperationException("Process identity mismatch");
        Memory memory;Io io;uint handles;
        if(!GetProcessMemoryInfo(process.Handle,out memory,(uint)Marshal.SizeOf(typeof(Memory))) || !GetProcessIoCounters(process.Handle,out io) || !GetProcessHandleCount(process.Handle,out handles))throw new Win32Exception();
        while(!QueryWorkingSet(process.Handle,buffer,size)){
            int error=Marshal.GetLastWin32Error();if(error!=24 || size>=16777216)throw new Win32Exception(error);
            size*=2;Marshal.FreeHGlobal(buffer);buffer=IntPtr.Zero;buffer=Marshal.AllocHGlobal(size);
        }
        long entries=Marshal.ReadIntPtr(buffer).ToInt64(),pages=0;
        if(entries<0 || entries>(size-IntPtr.Size)/IntPtr.Size)throw new InvalidOperationException("Working-set bounds");
        for(int i=0;i<entries;i++)if((Marshal.ReadIntPtr(buffer,(i+1)*IntPtr.Size).ToInt64()&0x100)==0)pages++;
        return new ResourceSample{PrivateResident=pages*Environment.SystemPageSize,WorkingSet=(long)memory.Working.ToUInt64(),PrivateCommit=(long)memory.PrivateUsage.ToUInt64(),
            CpuMs=process.TotalProcessorTime.TotalMilliseconds,Handles=handles,Threads=process.Threads.Count,Gdi=GetGuiResources(process.Handle,0),User=GetGuiResources(process.Handle,1),ReadBytes=io.ReadBytes,WriteBytes=io.WriteBytes};
    }
    public void Dispose(){if(buffer!=IntPtr.Zero){Marshal.FreeHGlobal(buffer);buffer=IntPtr.Zero;}}
    public sealed class ResourceSample{public long PrivateResident,WorkingSet,PrivateCommit;public double CpuMs;public uint Handles;public int Threads,Gdi,User;public ulong ReadBytes,WriteBytes;}
    [StructLayout(LayoutKind.Sequential)]private struct Memory{internal uint Size,Faults;internal UIntPtr PeakWorking,Working,PeakPaged,Paged,PeakNonpaged,Nonpaged,Pagefile,PeakPagefile,PrivateUsage;}
    [StructLayout(LayoutKind.Sequential)]private struct Io{internal ulong Reads,Writes,Other,ReadBytes,WriteBytes,OtherBytes;}
    [DllImport("psapi.dll",SetLastError=true)]private static extern bool GetProcessMemoryInfo(IntPtr process,out Memory memory,uint size);
    [DllImport("psapi.dll",SetLastError=true)]private static extern bool QueryWorkingSet(IntPtr process,IntPtr buffer,int size);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetProcessIoCounters(IntPtr process,out Io io);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetProcessHandleCount(IntPtr process,out uint count);
    [DllImport("user32.dll")]private static extern int GetGuiResources(IntPtr process,int flag);
}

public sealed class ResourceBudget
{
 private sealed class Point{internal double At,Cpu;}
 private readonly System.Collections.Generic.Queue<Point> history=new System.Collections.Generic.Queue<Point>();
 private double high=-1;
 public string Check(long commit,long working,double seconds,double cpu,int processors){
  if(commit>512L*1024*1024)return "private-commit-limit";
  if(working>256L*1024*1024){if(high<0)high=seconds;if(seconds-high>=60)return "working-set-limit";}else high=-1;
  history.Enqueue(new Point{At=seconds,Cpu=cpu});while(history.Count>1 && seconds-history.ToArray()[1].At>=60)history.Dequeue();
  var first=history.Peek();if(seconds-first.At>=60 && 100*(cpu-first.Cpu)/(1000*(seconds-first.At)*processors)>1)return "cpu-limit";return "";
 }
 public static void SelfTest(){
  if(new ResourceBudget().Check(513L*1024*1024,0,0,0,1)!="private-commit-limit")throw new Exception("Commit protection failed");
  var ws=new ResourceBudget();ws.Check(0,257L*1024*1024,0,0,1);if(ws.Check(0,257L*1024*1024,60,0,1)!="working-set-limit")throw new Exception("Working-set protection failed");
  var cpu=new ResourceBudget();cpu.Check(0,0,0,0,1);if(cpu.Check(0,0,60,1200,1)!="cpu-limit")throw new Exception("CPU protection failed");
 }
}
