using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
internal static class MemoryAttributionFixture {
    internal static byte[] LiveBytes=new byte[3*1024*1024];
    internal static char[] LiveChars=new char[512*1024];
    internal static WeakReference WeakOnly;
    internal static object Key=new object();
    internal static ConditionalWeakTable<object,byte[]> Dependent=new ConditionalWeakTable<object,byte[]>();
    [MethodImpl(MethodImplOptions.NoInlining)] private static void Weak(){WeakOnly=new WeakReference(new byte[1024*1024]);}
    private static void Main(string[] args) {
        string output=args[0];Directory.CreateDirectory(output);Weak();
        Dependent.Add(Key,new byte[512*1024]);for(int i=0;i<LiveBytes.Length;i+=4096)LiveBytes[i]=1;
        using(var process=System.Diagnostics.Process.GetCurrentProcess())File.WriteAllText(Path.Combine(output,"fixture-threads.txt"),process.Threads.Count.ToString());
        IntPtr native=Marshal.AllocHGlobal(2*1024*1024);
        for(int i=0;i<2*1024*1024;i+=4096)Marshal.WriteByte(native,i,1);
        File.WriteAllText(Path.Combine(output,"native-allocations.json"),"[{\"role\":\"calibration-native-buffer\",\"address\":\""+native.ToInt64().ToString("X")+"\",\"bytes\":2097152}]");
        try {for(int n=0;n<600 && !File.Exists(Path.Combine(output,"stop.flag"));n++){File.WriteAllText(Path.Combine(output,"heartbeat.txt"),n.ToString());Thread.Sleep(100);}}
        finally {Marshal.FreeHGlobal(native);GC.KeepAlive(LiveBytes);GC.KeepAlive(LiveChars);GC.KeepAlive(Dependent);GC.KeepAlive(Key);}
    }
}
