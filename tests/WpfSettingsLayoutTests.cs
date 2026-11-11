using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
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
                window.SetValues(3,"Ctrl+Alt+F10",english?"en":"zh-CN",true,true,true,87,"geometry");
                window.SetList(paths);
                window.SetSources(new[]{new PlayingSource{Path=paths[0],Name="Fixture player"}},true);
                window.SetStatus(english?"✓ Display ready":"✓ 显示已就绪","","","");
                window.Show();
                await window.Dispatcher.InvokeAsync(delegate{},DispatcherPriority.ApplicationIdle);

                string tag=english?"en":"zh";
                Check(Math.Abs(window.Width/window.Height-1.4)<0.001,tag+" preserves the 7:5 window ratio");
                Check(window.ActualWidth>0 && window.ActualHeight>0,tag+" has a visible native work area");
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

                foreach(string theme in new[]{"geometry","warm","black","geometry"}) {
                    window.SetValues(3,"Ctrl+Alt+F10",english?"en":"zh-CN",true,true,true,87,theme);
                    Check(window.InterfaceTheme==theme,tag+" applies "+theme+" appearance");
                }
                Check(Field<Canvas>(window,"GeometryLayer").Visibility==Visibility.Visible,tag+" geometry decorations are visible by default");
                ShowPage(window,"playback");Capture(window,"settings-playback-"+tag+".png");
                ShowPage(window,"about");Capture(window,"settings-about-"+tag+".png");
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
