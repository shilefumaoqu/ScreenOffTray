using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScreenOffTray;
using ScreenOffTray.SettingsTransport;

namespace OledPrototype {internal static class Program {internal static void Log(string text){throw new Exception("Fixture must inject its logger");}}}
internal static class WpfPipeFixture
{
    private static string output;
    private static int failed;
    [STAThread] private static int Main(string[] args)
    {
        if(args.Length==5 && args[0]=="--connect")return Peer(args);
        if(args.Length!=4)return 2;output=Path.GetFullPath(args[0]);
        try {
            var commands=new BlockingCollection<Action>();var owner=new Thread(delegate(){foreach(var command in commands.GetConsumingEnumerable())command();}){IsBackground=true};owner.Start();
            string external=Environment.GetEnvironmentVariable("SCREENOFF_UI_BENCH_BINARY");
            int saved=0,shown=0;Environment.SetEnvironmentVariable("SCREENOFF_WPF_FIXTURE_OUTPUT",output);var clock=Stopwatch.StartNew();
            using(var ended=new ManualResetEvent(false)) {
                SettingsPipeHost host=null;
                string playingPath=Environment.GetFolderPath(Environment.SpecialFolder.System)+@"\notepad.exe";
                var state=new ViewState{Version=1,Ready=true,CanClose=true,AudioKnown=true,StatusZh="✓ 显示已就绪",StatusEn="✓ Display ready",
                    Whitelist=Enumerable.Range(0,10).Select(x=>@"C:\Fixture\app"+x+".exe").ToArray(),
                    Sources=new[]{new PlayingSource{Path=playingPath,Name="Fixture icon reader",Whitelisted=false,Protected=false}},
                    Applied=new FormValues{Minutes=3,Hotkey="Ctrl+Alt+F10",Language=args[1]=="en"?"en":"zh-CN",AutomaticUpdates=true,LimitEnabled=true,LimitMinutes=60,InterfaceTheme="warm"}};
                using(host=new SettingsPipeHost(a=>commands.Add(a),delegate(Packet packet,Action<Packet> result){
                    if(packet.Kind=="save") {
                        if(packet.Values.Minutes!=4 || packet.Values.LimitMinutes!=87 || packet.Values.InterfaceTheme!="warm")failed=1;
                        saved++;state.Applied=packet.Values;state.Version++;host.Publish(state);result(new Packet());
                    }else result(new Packet{Error="Unexpected fixture command"});
                },delegate{Interlocked.Increment(ref shown);File.WriteAllText(Path.Combine(output,"ready-ms.txt"),clock.Elapsed.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));if(!String.IsNullOrEmpty(external))System.Threading.Tasks.Task.Delay(250).ContinueWith(_=>host.Close());},()=>ended.Set(),delegate{})) {
                    host.Start(String.IsNullOrEmpty(external)?typeof(WpfPipeFixture).Assembly.Location:external,args[1]);host.Publish(state);
                    if(!ended.WaitOne(12000))throw new Exception("UI fixture did not exit normally");
                    if(saved!=(String.IsNullOrEmpty(external)?1:0) || shown!=1 || failed!=0)throw new Exception("Unexpected saves/shown/result: "+saved+"/"+shown+"/"+failed);
                }
            }
            commands.CompleteAdding();owner.Join(1000);commands.Dispose();
            File.WriteAllText(Path.Combine(output,"fixture-result.txt"),"PASS real UI Program, fake owner and ten-entry state; normal save/exit; private non-input desktop; no product Main, hooks, display or formal writes");return 0;
        }catch(Exception ex){File.WriteAllText(Path.Combine(output,"failure.txt"),ex.ToString());return 1;}
    }
    private static int Peer(string[] args)
    {
        // Never create a window until this inherited private desktop is verified.
        string desktop=DesktopName();if(!desktop.StartsWith("ScreenOffMemory-",StringComparison.Ordinal))return 9;
        ThreadPool.QueueUserWorkItem(delegate {
            var until=Stopwatch.StartNew();while(Application.Current==null && until.ElapsedMilliseconds<5000)Thread.Sleep(10);
            var app=Application.Current;if(app==null)return;
            app.Dispatcher.BeginInvoke(new Action(delegate {
                var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};int attempts=0;
                timer.Tick+=delegate {
                    var type=typeof(ScreenOffTray.SettingsUi.Program);var flags=BindingFlags.NonPublic|BindingFlags.Static;
                    var state=type.GetField("state",flags).GetValue(null) as ViewState;
                    var window=app.MainWindow as ScreenOffTray.SettingsUi.SettingsWindow;
                    if(state==null || window==null){if(attempts++>200){timer.Stop();app.Shutdown(8);}return;}
                    try {
                        var icons=window.GetType().GetField("icons",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(window) as ScreenOffTray.SettingsUi.ApplicationIcons;
                        // Real local icon extraction runs through the production cache;
                        // the executable identity and all player/settings data are fake.
                        var image=icons.Read(Environment.GetFolderPath(Environment.SpecialFolder.System)+@"\notepad.exe");
                        if(image==null){if(attempts++>250)throw new Exception("Real icon load timeout");return;}
                        if(!image.IsFrozen)throw new Exception("UI icon must be frozen");
                        File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("SCREENOFF_WPF_FIXTURE_OUTPUT"),"icon-result.txt"),"PASS actual local notepad executable icon loaded and frozen");timer.Stop();
                        var content=(FrameworkElement)window.Content;
                        window.SetValues(4,"Ctrl+Alt+F10",state.Applied.Language,false,true,true,87,"geometry");Capture(content,"settings-geometry.png");
                        ShowPage(window,"playback");Capture(content,"settings-playback.png");
                        ShowPage(window,"about");Capture(content,"settings-about.png");
                        ShowPage(window,"general");
                        window.SetValues(4,"Ctrl+Alt+F10",state.Applied.Language,false,true,true,87,"black");
                        if(window.InterfaceTheme!="black")throw new Exception("Black appearance was not applied");
                        Capture(content,"settings-black.png");
                        window.SetValues(4,"Ctrl+Alt+F10",state.Applied.Language,false,true,true,87,"warm");
                        if(window.InterfaceTheme!="warm")throw new Exception("Saved appearance did not survive draft update");
                        Capture(content,"settings-warm.png");
                        type.GetMethod("Save",flags).Invoke(null,null);
                    }catch(Exception ex){timer.Stop();File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("SCREENOFF_WPF_FIXTURE_OUTPUT"),"peer-failure.txt"),ex.ToString());app.Shutdown(7);}
                };timer.Start();
            }),DispatcherPriority.Background);
        });
        var main=typeof(ScreenOffTray.SettingsUi.Program).GetMethod("Main",BindingFlags.NonPublic|BindingFlags.Static);
        return (int)main.Invoke(null,new object[]{args});
    }
    private static string DesktopName()
    {var name=new StringBuilder(256);int needed;if(!GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()),2,name,name.Capacity*2,out needed))throw new System.ComponentModel.Win32Exception();return name.ToString();}
    private static void ShowPage(ScreenOffTray.SettingsUi.SettingsWindow window,string page){window.GetType().GetMethod("ShowPage",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(window,new object[]{page});}
    private static void Capture(FrameworkElement content,string name)
    {
        var frame=new DispatcherFrame();
        content.Dispatcher.BeginInvoke(new Action(delegate {
            var target=PresentationSource.FromVisual(content).CompositionTarget;double scale=target.TransformToDevice.M11;
            var rendered=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth*scale),(int)Math.Ceiling(content.ActualHeight*scale),96*scale,96*scale,System.Windows.Media.PixelFormats.Pbgra32);rendered.Render(content);
            var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rendered));using(var file=File.Create(Path.Combine(Environment.GetEnvironmentVariable("SCREENOFF_WPF_FIXTURE_OUTPUT"),name)))png.Save(file);
            frame.Continue=false;
        }),DispatcherPriority.ApplicationIdle);
        Dispatcher.PushFrame(frame);
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint id);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool GetUserObjectInformation(IntPtr handle,int index,StringBuilder value,int length,out int needed);
}
