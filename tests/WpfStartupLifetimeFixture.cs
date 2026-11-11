using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Text;
using System.Runtime.InteropServices;
using ScreenOffTray;
using ScreenOffTray.SettingsTransport;
namespace OledPrototype{internal static class Program{internal static void Log(string value){}}}
internal static class WpfStartupLifetimeFixture
{
    private static int Main(string[] args)
    {
        string output=args[0],mode=args[1],ui=Environment.GetEnvironmentVariable("SCREENOFF_LIFETIME_TEST_UI");
        try {
            if(mode=="connected") {
                using(var shown=new ManualResetEvent(false))using(var host=new SettingsPipeHost(a=>a(),(p,done)=>done(new Packet()),()=>shown.Set(),()=>{},s=>{})) {
                    host.Start(ui,"en");host.Publish(new ViewState{Version=1,Ready=true,CanClose=true,AudioKnown=true,Applied=new FormValues{Minutes=3,Hotkey="Ctrl+Alt+F10",Language="en",LimitMinutes=60},Whitelist=new string[0],Sources=new PlayingSource[0]});
                    var child=(Process)typeof(SettingsPipeHost).GetField("child",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(host);Record(output,child);
                    if(!shown.WaitOne(7000))throw new Exception("UI not shown");return 0;
                }
            }
            using(var parent=Process.GetCurrentProcess()) {
                int pid=mode=="invalid"?Int32.MaxValue:parent.Id;long ticks=mode=="invalid"?1:parent.StartTime.ToUniversalTime().Ticks;
                using(var child=Process.Start(new ProcessStartInfo(ui,"--connect ScreenOffTray.Absent."+Guid.NewGuid().ToString("N")+" "+pid+" "+ticks+" en"){UseShellExecute=false})) {
                    Record(output,child);
                    if(mode=="error-dialog") {WaitForOwnErrorDialog(output,child);}
                    else Thread.Sleep(500);
                }
            }
            return 0;
        }catch(Exception ex){File.WriteAllText(Path.Combine(output,"failure.txt"),ex.ToString());return 1;}
    }
    private static void WaitForOwnErrorDialog(string output,Process child)
    {
        var clock=Stopwatch.StartNew();IntPtr dialog=IntPtr.Zero;
        while(clock.ElapsedMilliseconds<12000 && !child.HasExited) {
            EnumWindows(delegate(IntPtr window,IntPtr unused) {
                uint pid;GetWindowThreadProcessId(window,out pid);if(pid!=(uint)child.Id)return true;
                var kind=new StringBuilder(256);var title=new StringBuilder(256);GetClassName(window,kind,kind.Capacity);GetWindowText(window,title,title.Capacity);
                if(kind.ToString()=="#32770" && title.ToString()=="ScreenOffTray" && IsWindowVisible(window)){dialog=window;return false;}return true;
            },IntPtr.Zero);
            if(dialog!=IntPtr.Zero)break;Thread.Sleep(20);
        }
        if(dialog==IntPtr.Zero || child.HasExited)throw new Exception("Exact live UI error dialog was not observed");
        File.WriteAllText(Path.Combine(output,"live-backend-error-observed.txt"),"Confirmed visible modal dialog #32770/ScreenOffTray; own UI pid="+child.Id+"; hwnd="+dialog+"; backend exits only after this receipt");
    }
    private delegate bool EnumCallback(IntPtr window,IntPtr value);
    [DllImport("user32.dll")]private static extern bool EnumWindows(EnumCallback callback,IntPtr value);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(IntPtr window,StringBuilder name,int size);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(IntPtr window,StringBuilder title,int size);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr window);
    private static void Record(string output,Process child){using(var writer=new StreamWriter(Path.Combine(output,"ui-identity.tmp"))){writer.WriteLine(child.Id);writer.WriteLine(child.StartTime.ToUniversalTime().Ticks);writer.WriteLine(child.StartInfo.FileName);}File.Move(Path.Combine(output,"ui-identity.tmp"),Path.Combine(output,"ui-identity.txt"));}
}
