using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
public static class OwnedProcessIdentity
{
    // Kernel process image identity is available before the user-mode loader
    // finishes. Process.MainModule may have no module at that point.
    public static bool Matches(Process process,long ticks,string path)
    {
        IntPtr handle=process.Handle;long creation,exit,kernel,user;var image=new StringBuilder(32768);int length=image.Capacity;
        return GetProcessId(handle)==(uint)process.Id && GetProcessTimes(handle,out creation,out exit,out kernel,out user) &&
            DateTime.FromFileTimeUtc(creation).Ticks==ticks && QueryFullProcessImageName(handle,0,image,ref length) &&
            String.Equals(image.ToString(),path,StringComparison.OrdinalIgnoreCase);
    }
    [DllImport("kernel32.dll")]private static extern uint GetProcessId(IntPtr handle);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetProcessTimes(IntPtr handle,out long creation,out long exit,out long kernel,out long user);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool QueryFullProcessImageName(IntPtr handle,uint flags,StringBuilder path,ref int length);
}
