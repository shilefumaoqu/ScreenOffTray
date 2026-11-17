using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenOffTray.SettingsTransport;
using ScreenOffTray.SettingsUi;

internal static class WpfSettingsLayoutTests
{
    private static string output;
    private static int checks,exitCode;
    private const BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic;
    private static T Field<T>(object owner,string name)
    {
        return (T)owner.GetType().GetField(name,Fields).GetValue(owner);
    }
    private static void Check(bool value,string name)
    {
        if(!value)throw new Exception(name);
        checks++;File.AppendAllText(Path.Combine(output,"results.txt"),"PASS "+name+Environment.NewLine);
    }
    [STAThread] private static int Main(string[] args)
    {
        if(args.Length!=4)return 2;
        output=args[0];Directory.CreateDirectory(output);
        try {
            var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
            app.Startup+=Run;app.Run();return exitCode;
        }catch(Exception ex){File.WriteAllText(Path.Combine(output,"failure.txt"),ex.ToString());return 1;}
    }
    private static async void Run(object sender,StartupEventArgs args)
    {
        SettingsWindow window=null;
        try {
            foreach(bool english in new[]{false,true}) {
                window=new SettingsWindow(english,new Size(1366,768));
                string[] paths=Enumerable.Range(0,10).Select(i=>@"C:\IsolatedFixture\app"+i+".exe").ToArray();
                window.SetValues(3,"Ctrl+Alt+F10",english?"en":"zh-CN",true,true,true,87,"system");
                window.SetList(paths);
                window.SetSources(new[]{new PlayingSource{Path=paths[0],Name="Fixture player"}},true);
                window.SetStatus(english?"✓ Display ready":"✓ 显示已就绪","","","");
                window.Show();
                await window.Dispatcher.InvokeAsync(delegate{},DispatcherPriority.ApplicationIdle);

                string tag=english?"en":"zh";
                Check(Math.Abs(window.Width/window.Height-1.4)<0.001,tag+" preserves the 7:5 window ratio");
                Check(window.ActualWidth>0 && window.ActualHeight>0,tag+" has a visible native work area");
                Check(Field<Border>(window,"Shell").Margin==new Thickness(0),tag+" uses the glass shell as the window boundary without a display backdrop");
                Check(!window.AllowsTransparency,tag+" uses a compositor-compatible HWND rather than a layered transparent window");
                Check(Field<ComboBox>(window,"WhitelistCombo").Items.Count==10,tag+" renders all ten allowlist entries");
                Check(Field<Border>(window,"FooterBar").ActualHeight>0,tag+" keeps a fixed footer");
                Check(Field<Button>(window,"SaveButton").IsVisible && Field<Button>(window,"CancelButton").IsVisible,tag+" keeps save and cancel visible");
                Check(Field<StackPanel>(window,"PlayingRows").Children.Count>0,tag+" renders the isolated current playback row");
                var summary=Field<TextBlock>(window,"PlaybackSummary");
                Check(summary.Text.Contains(english?"add an app":"加入白名单后"),tag+" does not claim an unlisted playing app is protected");
                window.SetSources(new[]{new PlayingSource{Path=paths[0],Name="Fixture player",Whitelisted=true,Protected=true}},true);
                Check(summary.Text.Contains(english?"paused":"已暂停"),tag+" reports a backend-confirmed protected app");
                window.SetSources(new[]{new PlayingSource{Path=paths[0],Name="Fixture player",Whitelisted=true,Protected=false}},true);
                window.SetPlaybackState(false,true);
                Check(summary.Text.Contains(english?"manually paused":"手动暂停"),tag+" distinguishes manual pause from an unlisted app");
                window.SetPlaybackState(true,false);
                Check(summary.Text.Contains(english?"limit reached":"上限已到"),tag+" reports that the playback limit ended protection");

                string lightShell=null,darkShell=null,systemShell=null;
                foreach(string theme in new[]{"system","light","dark","system"}) {
                    window.SetValues(3,"Ctrl+Alt+F10",english?"en":"zh-CN",true,true,true,87,theme);
                    Check(window.InterfaceTheme==theme,tag+" applies "+theme+" appearance");
                    var shellBrush=(LinearGradientBrush)Field<Border>(window,"Shell").Background;
                    string shellColor=shellBrush.GradientStops[0].Color.ToString();
                    var fill=((SolidColorBrush)window.FindResource("FillControl"));
                    if(theme=="light") {
                        Check(fill.Color.A==0x80,tag+" uses Pane's translucent light control fill");
                        Check(shellBrush.GradientStops.All(s=>s.Color.A==(window.UsesDesktopBackdrop?0:255)),tag+" exposes native light blur or an explicitly solid fallback without a near-solid imitation");
                    } else if(theme=="dark") {
                        Check(fill.Color.A==0x12,tag+" uses Pane's translucent dark control fill");
                        Check(shellBrush.GradientStops.All(s=>s.Color.A==(window.UsesDesktopBackdrop?0:255)),tag+" exposes native dark blur or an explicitly solid fallback without a near-solid imitation");
                    }
                    if(theme!="system") {
                        var edge=((LinearGradientBrush)Field<Border>(window,"Shell").BorderBrush).GradientStops;
                        byte[] expected=theme=="light"?new byte[]{0x86,0x28,0x0D,0x3C}:new byte[]{0x27,0x18,0x12,0x1A};
                        Check(edge.Count==4 && edge[0].Color.A==expected[0] && edge[1].Color.A==expected[1] && edge[2].Color.A==expected[2] && edge[3].Color.A==expected[3],tag+" applies Pane's "+theme+" edge highlight");
                    }
                    if(theme=="light")lightShell=shellColor;
                    else if(theme=="dark")darkShell=shellColor;
                    else systemShell=shellColor;
                    ShowPage(window,"general");
                    Capture(window,"settings-"+theme+"-"+tag+".png");
                }
                Check(lightShell!=darkShell,tag+" uses distinct light and dark top-level colors");
                Check(systemShell==lightShell || systemShell==darkShell,tag+" resolves system appearance to light or dark");
                var hwnd=new WindowInteropHelper(window).Handle;
                IntPtr marker=CreateRectRgn(0,0,9,9);
                if(SetWindowRgn(hwnd,marker,false)==0){DeleteObject(marker);throw new Exception("Test region attachment failed");}
                object[] dpiMessage={hwnd,0x2E0,IntPtr.Zero,IntPtr.Zero,false};
                window.GetType().GetMethod("OnBackdropMessage",Fields).Invoke(window,dpiMessage);
                Check(RegionWidth(hwnd)==9,tag+" defers DPI clipping until WPF has processed the native message");
                await window.Dispatcher.InvokeAsync(delegate{},DispatcherPriority.ApplicationIdle);
                Check(RegionWidth(hwnd)>9,tag+" rebuilds the rounded native region after the DPI handler completes");
                Check(!NativeBackdrop.Apply(hwnd,false,true),tag+" disables native material for the high-contrast path without changing system settings");
                int nativeMode;int result=DwmGetWindowAttribute(hwnd,38,out nativeMode,4);
                Check(result<0 || nativeMode==1,tag+" requests no system backdrop on the accessible fallback path");
                window.SetValues(3,"Ctrl+Alt+F10",english?"en":"zh-CN",true,true,true,87,"light");
                result=DwmGetWindowAttribute(hwnd,38,out nativeMode,4);
                Check(!window.UsesDesktopBackdrop || (result>=0 && nativeMode==3),tag+" attaches Desktop Acrylic when supported; actual blur requires desktop photographs");
                Check(((SolidColorBrush)Field<Border>(window,"FooterBar").Background).Color.A==(window.UsesDesktopBackdrop?0x28:0xFF),tag+" keeps footer paint on the same native material, with an opaque unsupported fallback");
                Check(window.FindName("WarmChoice")==null && window.FindName("BlackChoice")==null,tag+" removes the reference test background from product theme choices");
                Check(Field<RadioButton>(window,"SystemChoice").Content.ToString()==(english?"Use system theme":"跟随系统") && Field<RadioButton>(window,"LightChoice").Content.ToString()==(english?"Light":"浅色") && Field<RadioButton>(window,"DarkChoice").Content.ToString()==(english?"Dark":"深色"),tag+" labels the same system, light and dark theme choices as Pane");
                window.SetValues(3,"Ctrl+Alt+F10",english?"en":"zh-CN",true,true,true,87,"geometry");Check(window.InterfaceTheme=="light",tag+" migrates legacy geometry theme to light");
                window.SetValues(3,"Ctrl+Alt+F10",english?"en":"zh-CN",true,true,true,87,"warm");Check(window.InterfaceTheme=="light",tag+" migrates removed warm test background to light");
                window.SetValues(3,"Ctrl+Alt+F10",english?"en":"zh-CN",true,true,true,87,"black");Check(window.InterfaceTheme=="dark",tag+" migrates legacy black theme to dark");
                var updateButton=Field<Button>(window,"UpdateButton");
                window.SetValues(3,"Ctrl+Alt+F10",english?"en":"zh-CN",true,true,true,87,"light");
                ShowPage(window,"about");
                window.SetUpdate("found",true);Check(updateButton.Content.ToString()==(english?"Download update":"下载更新"),tag+" offers in-app download for a verifiable installer");
                window.SetUpdate("downloading",true,"downloading",42);Check(!updateButton.IsEnabled && Field<ProgressBar>(window,"UpdateProgress").Value==42 && Field<Button>(window,"CancelUpdateButton").IsVisible,tag+" download shows progress and an accessible cancel action");
                window.SetUpdate("verified",true,"ready",100);Check(updateButton.IsEnabled && updateButton.Content.ToString()==(english?"Save and restart to update":"保存并重启更新") && !Field<ProgressBar>(window,"UpdateProgress").IsVisible,tag+" verified package requires explicit save and restart");
                Capture(window,"settings-update-ready-"+tag+".png");
                window.SetUpdate("failed",true,"failed");Check(updateButton.IsEnabled && Field<Button>(window,"ManualUpdateButton").IsVisible,tag+" failed download supports retry and manual recovery");
                window.SetUpdate("missing digest",true,"",0,false);Check(updateButton.Content.ToString()==(english?"Download manually":"手动下载"),tag+" unverifiable release never offers in-app installation");
                window.SetUpdate("applying",true,"applying");Check(!updateButton.IsEnabled && !Field<Button>(window,"CancelUpdateButton").IsVisible,tag+" committed installation cannot be resubmitted");
                window.SetUpdate("",false);Check(updateButton.Content.ToString()==(english?"Check for updates":"检查更新"),tag+" restores update check when no version is available");
                Check(window.FindName("GeometryLayer")==null,tag+" removes the reference image's colored geometry wallpaper");
                ShowPage(window,"playback");Capture(window,"settings-playback-"+tag+".png");
                ShowPage(window,"about");Capture(window,"settings-about-"+tag+".png");
                window.SetValues(3,"Ctrl+Alt+F10",english?"en":"zh-CN",true,true,true,87,"light");
                window.SetUpdate(english?"Version 0.2.5 is available.":"发现新版本 v0.2.5",true);
                Capture(window,"settings-about-update-light-"+tag+".png");
                window.SetUpdate("",false);
                ShowPage(window,"general");Capture(window,"settings-general-"+tag+".png");
                window.Close();window=null;
            }
            window=new SettingsWindow(false,new Size(640,360));
            Check(window.Width<=608 && window.Height<=328,"small work area caps the window instead of enforcing a larger minimum");
            window.Show();
            await window.Dispatcher.InvokeAsync(delegate{},DispatcherPriority.ApplicationIdle);
            var reset=Field<Button>(window,"ResetHotkeyButton");
            var resetBounds=reset.TransformToAncestor(window).TransformBounds(new Rect(0,0,reset.ActualWidth,reset.ActualHeight));
            Check(reset.IsVisible && resetBounds.Left>=0 && resetBounds.Right<=window.ActualWidth+1,"compact sidebar keeps fixed-width controls reachable");
            Check(window.FindName("GeneralNavText").Equals(null)==false && ((TextBlock)window.FindName("GeneralNavText")).Visibility==Visibility.Collapsed,"compact sidebar keeps navigation tooltips with collapsed labels");
            window.Close();window=null;
            File.WriteAllText(Path.Combine(output,"ready-ms.txt"),"0");
            File.AppendAllText(Path.Combine(output,"results.txt"),"Checks="+checks+"; private non-input desktop; fake data and callbacks; no hooks, APPLY, formal settings or display access."+Environment.NewLine);
        }catch(Exception ex) {
            exitCode=1;File.WriteAllText(Path.Combine(output,"failure.txt"),ex.ToString());
        }finally {
            if(window!=null)window.Close();
            Application.Current.Shutdown(exitCode);
        }
    }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window,int attribute,out int value,int size);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect {internal int Left,Top,Right,Bottom;}
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left,int top,int right,int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr region);
    [DllImport("gdi32.dll")] private static extern int GetRgnBox(IntPtr region,out NativeRect bounds);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr window,IntPtr region,bool redraw);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window,IntPtr region);
    private static int RegionWidth(IntPtr window)
    {
        IntPtr region=CreateRectRgn(0,0,0,0);
        try {NativeRect rect;if(GetWindowRgn(window,region)==0 || GetRgnBox(region,out rect)==0)throw new Exception("Test window has no native region");return rect.Right-rect.Left;}
        finally {DeleteObject(region);}
    }
    private static void ShowPage(SettingsWindow window,string page)
    {
        window.GetType().GetMethod("ShowPage",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(window,new object[]{page});
    }
    private static void Capture(Window window,string name)
    {
        window.UpdateLayout();
        var content=(FrameworkElement)window.Content;
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32);
        bitmap.Render(content);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
        using(var stream=File.Create(Path.Combine(output,name)))png.Save(stream);
    }
}
