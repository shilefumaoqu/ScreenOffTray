using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
#if BEFORE_STATIC_CAT
using Cat = ScreenOffTray.Ui.AnimatedCatControl;
#else
using Cat = ScreenOffTray.Ui.StaticCatControl;
#endif

// Compiled alone with the cat control. Never links the product entry or backend.
internal static class CatMemoryProbe
{
    [DllImport("psapi.dll",SetLastError=true)] private static extern bool QueryWorkingSet(IntPtr process,IntPtr data,int size);
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process,int kind);
    private static readonly List<string> rows = new List<string>();

    private static long PrivateResident(Process process)
    {
        IntPtr buffer = Marshal.AllocHGlobal(1024*1024);
        try {
            if (!QueryWorkingSet(process.Handle,buffer,1024*1024)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            long count = Marshal.ReadIntPtr(buffer).ToInt64(), privatePages = 0;
            if (count < 0 || count > (1024*1024-IntPtr.Size)/IntPtr.Size) throw new InvalidDataException("Working set bounds");
            for (int i=0;i<count;i++) if ((Marshal.ReadIntPtr(buffer,(i+1)*IntPtr.Size).ToInt64()&0x100)==0) privatePages++;
            return privatePages*Environment.SystemPageSize;
        } finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void Sample(string phase)
    {
        using (Process process = Process.GetCurrentProcess()) {
            process.Refresh();
            rows.Add(String.Join(",",phase,process.PrivateMemorySize64,PrivateResident(process),
                process.WorkingSet64,GC.GetTotalMemory(false),process.HandleCount,
                process.Threads.Count,GetGuiResources(process.Handle,0),GetGuiResources(process.Handle,1),
                process.TotalProcessorTime.TotalMilliseconds));
        }
    }

    [STAThread] private static int Main(string[] args)
    {
        try {
            // Match the same Framework/graphics warmup for both fresh processes.
            using (Control warm = new Control()) { string ignored=warm.Font.Name; }
            using (Bitmap warm = new Bitmap(16,16)) using (Graphics graphics = Graphics.FromImage(warm)) graphics.Clear(Color.Transparent);
            rows.Add("phase,privateBytes,privateResidentBytes,workingSetBytes,managedAllocatedBytes,handles,threads,gdi,user,cpuMilliseconds");
            Sample("base");
            using (var cat = new Cat()) {
                cat.Size = new Size(324,174);
                using (Bitmap output = new Bitmap(324,174)) using (Graphics graphics = Graphics.FromImage(output)) {
                    typeof(Cat).GetMethod("OnPaint",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(cat,
                        new object[]{new PaintEventArgs(graphics,new Rectangle(0,0,324,174))});
                }
                Sample("loaded");
                for (int i=0;i<10;i++) { Thread.Sleep(1000); Sample("held-"+i); }
            }
            Sample("disposed");
            for (int i=0;i<3;i++) { Thread.Sleep(1000); Sample("after-"+i); }
            File.WriteAllLines(Path.GetFullPath(args[0]),rows.ToArray());
            Console.WriteLine("PASS " + typeof(Cat).Name + "; no forced GC, native window, audio, settings or display actions");
            return 0;
        } catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
}
