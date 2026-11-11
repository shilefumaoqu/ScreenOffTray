using System;
using System.IO;
using System.Text;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ScreenOffTray.Ui;

namespace ScreenOffTray.Tests {
 internal static class FirstRunSetupUiTests {
  static string output; static int checks, failures;
  [STAThread] static int Main(string[] args) { output=Path.GetFullPath(args[0]); Directory.CreateDirectory(output); try { Log(args.Length<2?"Launching private desktop":"Private desktop child started"); if(args.Length<2) return Launch(); Run(); } catch(Exception e) { Log("FAIL exception type="+e.GetType().FullName+" message="+e.Message); failures++; } Log("RESULT checks="+checks+" failures="+failures); return failures==0?0:1; }
  static void Log(string s) { File.AppendAllText(Path.Combine(output,"results.txt"),s+Environment.NewLine,Encoding.UTF8); }
  static void Check(bool v,string s) { checks++; if(!v) failures++; Log((v?"PASS ":"FAIL ")+s); }
  static T Get<T>(Control c,string n) where T:Control { return (T)c.Controls.Find(n,true)[0]; }
  static void Click(Button b) { b.PerformClick(); Application.DoEvents(); }
  static void Link(LinkLabel l) { typeof(LinkLabel).GetMethod("OnLinkClicked",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(l,new object[]{new LinkLabelLinkClickedEventArgs(l.Links[0])}); Application.DoEvents(); }
  static void Capture(Form f,string n) { using(var b=new Bitmap(f.ClientSize.Width,f.ClientSize.Height)) using(var g=Graphics.FromImage(b)) { IntPtr dc=g.GetHdc(); bool ok; try { ok=PrintWindow(f.Handle,dc,1); } finally { g.ReleaseHdc(dc); } Check(ok,n+" PrintWindow"); b.Save(Path.Combine(output,n+".png"),ImageFormat.Png); } }
  static void Run() {
   foreach(string lang in new[]{"zh-CN","en"}) {
    int prep=0,cancel=0,recheck=0,help=0,display=0;
    using(SettingsDpiContext.EnterUi()) using(var icon=new Icon(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ScreenOffTray.ico"))) using(var f=new FirstRunSetupDialog(new UiText(lang),()=>prep++,()=>cancel++,()=>recheck++,()=>display++,()=>help++,icon)) {
     f.Show(); Application.DoEvents(); uint dpi=GetDpiForWindow(f.Handle),sys=GetDpiForSystem();
     Log(lang+" actual_hwnd_dpi="+dpi+" system_dpi="+sys+" font="+f.Font.SizeInPoints+" client="+f.ClientSize); Check(dpi==168,lang+" actual HWND DPI is 168");
     var missing=new FirstRunSetupState { VirtualModeMissing=true,CanPrepare=true, Detail=lang=="en"?"The virtual display does not offer 2880 × 1800. Setup cannot finish yet.\r\nMatch its resolution, then check again. Display off remains disabled.":"虚拟屏尚未提供 2880 × 1800 模式，暂时无法完成准备。\r\n请先匹配虚拟屏分辨率，再重新检查。关屏保持停用。",Diagnostics="Diagnostic details" };
     f.RefreshSetup(missing); Application.DoEvents();
     Check(!Get<TextBox>(f,"diagnostics").Visible,lang+" diagnostics initially collapsed");
     Check(Get<Label>(f,"status").Text==(lang=="en"?"Match the virtual display resolution":"需要匹配虚拟屏分辨率"),lang+" missing title");
     Check(!Get<Panel>(f,"body").HorizontalScroll.Visible && !Get<Panel>(f,"body").VerticalScroll.Visible,lang+" missing no normal scroll"); Capture(f,lang+"-missing");
     Check(Get<Button>(f,"primary").Text==(lang=="en"?"Adapt display automatically":"自动适配显示"),lang+" missing primary explicitly adapts");
     Check(Get<Label>(f,"description").Text.Contains(lang=="en"?"Administrator":"管理员") && Get<Label>(f,"description").Text.Contains(lang=="en"?"flicker":"闪动"),lang+" missing explains elevation and flicker");
     Click(Get<Button>(f,"primary")); Link(Get<LinkLabel>(f,"displayLink")); Check(recheck==0 && help==1 && prep==1 && display==0,lang+" missing actions prepare only on explicit click");
     Link(Get<LinkLabel>(f,"disclosure")); Check(Get<TextBox>(f,"diagnostics").Visible,lang+" expanded"); Capture(f,lang+"-expanded"); Link(Get<LinkLabel>(f,"disclosure"));
     f.RefreshSetup(new FirstRunSetupState { Ready=true,VirtualModeMissing=true }); Application.DoEvents(); Check(!Get<Button>(f,"secondary").Visible,lang+" ready precedence"); Capture(f,lang+"-ready");
     f.RefreshSetup(new FirstRunSetupState { ClonedDisplay=true,CanPrepare=true, Detail="recovery is unverified" }); Application.DoEvents(); Check(Get<Label>(f,"description").Text.Contains("recovery is unverified"),lang+" recovery warning preserved"); Capture(f,lang+"-clone");
     Click(Get<Button>(f,"primary")); Check(prep==2,lang+" clone prepare only on click"); f.RefreshSetup(new FirstRunSetupState { Preparing=true, Detail="Preparing" }); f.Close(); Application.DoEvents(); Check(cancel==1 && !f.IsDisposed && f.Visible,lang+" close requests cancellation and waits");
     f.RefreshSetup(new FirstRunSetupState { Preparing=false,Detail="recovery is unverified" }); Application.DoEvents(); Check(f.IsDisposed || !f.Visible,lang+" closes only after preparation stops");
    }
    using(SettingsDpiContext.EnterUi()) using(var f=new FirstRunSetupDialog(new UiText(lang),()=>prep++,()=>cancel++,()=>recheck++,()=>display++,()=>help++,null)) {
     f.Show(); Application.DoEvents(); f.RefreshSetup(new FirstRunSetupState { MetricsUnavailable=true,CanPrepare=true,Detail=lang=="en"?"Unable to read metrics.":"暂时无法读取显示信息。" }); Application.DoEvents(); Capture(f,lang+"-metrics");
     Check(Get<Label>(f,"status").Text==(lang=="en"?"Virtual display is temporarily unavailable":"暂时无法读取虚拟屏"),lang+" metrics title"); Click(Get<Button>(f,"primary")); Check(recheck==1 && prep==2,lang+" metrics rechecks only");
     f.RefreshSetup(new FirstRunSetupState { ClonedDisplay=true,CanPrepare=false }); Click(Get<Button>(f,"primary")); Check(recheck==2 && prep==2,lang+" clone unavailable rechecks only");
     f.RefreshSetup(new FirstRunSetupState { VirtualModeMissing=true,CanPrepare=false, Detail="Configuration is protected" }); Click(Get<Button>(f,"primary")); Check(recheck==3 && prep==2,lang+" blocked adaptation rechecks without device work");
     f.RefreshSetup(new FirstRunSetupState { VirtualModeMissing=true, Detail="detail",Diagnostics="detail more" }); f.FitToWorkArea(new Rectangle(f.Left,f.Top,f.Width,230)); Application.DoEvents(); Capture(f,lang+"-low"); Check(!Get<Panel>(f,"body").HorizontalScroll.Visible,lang+" low height no horizontal scroll"); Click(Get<Button>(f,"secondary")); Check(f.IsDisposed || !f.Visible,lang+" not now closes");
    }
   }
  }
  static int Launch() { string name="FirstRun-"+Guid.NewGuid().ToString("N"); IntPtr desktop=CreateDesktop(name,IntPtr.Zero,IntPtr.Zero,0,0x10000000,IntPtr.Zero); if(desktop==IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); var si=new StartupInfo { Size=Marshal.SizeOf(typeof(StartupInfo)),Desktop=name }; ProcessInfo pi; string exe=Assembly.GetExecutingAssembly().Location; try { if(!CreateProcess(exe,new StringBuilder("\""+exe+"\" \""+output+"\" --child"),IntPtr.Zero,IntPtr.Zero,false,0x08000000,IntPtr.Zero,null,ref si,out pi)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); try { if(WaitForSingleObject(pi.Process,20000)==258) { Log("TIMEOUT exact child pid="+pi.ProcessId); TerminateProcess(pi.Process,3); WaitForSingleObject(pi.Process,5000); return 3; } uint code; GetExitCodeProcess(pi.Process,out code); return (int)code; } finally { CloseHandle(pi.Thread); CloseHandle(pi.Process); } } finally { CloseDesktop(desktop); } }
  [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct StartupInfo { internal int Size; internal string Reserved,Desktop,Title; internal int X,Y,Width,Height,XChars,YChars,Fill,Flags; internal short Show,ReservedSize; internal IntPtr ReservedData,Input,Output,Error; }
  [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { internal IntPtr Process,Thread; internal uint ProcessId,ThreadId; }
  [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateDesktop(string n,IntPtr d,IntPtr m,uint f,uint a,IntPtr s);
  [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr d);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcess(string a,StringBuilder c,IntPtr p,IntPtr t,bool i,uint f,IntPtr e,string w,ref StartupInfo s,out ProcessInfo pi);
  [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h,uint t);
  [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h,out uint c);
  [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr h,uint c);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] static extern uint GetDpiForSystem();
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h,IntPtr dc,uint flags);
 }
}
