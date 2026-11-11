using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using ScreenOffTray.SettingsUi;

internal static class WpfSettingsProbe
{
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr CreateDesktop(string name,IntPtr device,IntPtr mode,uint flags,uint access,IntPtr security);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [STAThread] private static int Main(string[] args)
    {
        if(args.Length!=4)return 2; // Only the private-desktop supervisor supplies this shape.
        try {return Run(args[0],args[1]=="en",args[1]=="visible");}
        catch(Exception ex){File.WriteAllText(Path.Combine(args[0],"failure.txt"),ex.ToString());return 1;}
    }
    private static int Run(string output,bool english,bool visible)
    {
        var timer=Stopwatch.StartNew();var preload=SettingsWindow.PreloadArtwork();var app=new Application{ShutdownMode=ShutdownMode.OnMainWindowClose};
        preload.GetAwaiter().GetResult();
        var window=new SettingsWindow(english);
        if(visible)window.Title="ScreenOffTray · WPF 隔离测试";
        window.SetList(new[]{"Chrome","网易云音乐","VLC","Windows Media Player","mpv","PotPlayer","Spotify","MusicBee","foobar2000","Firefox"});
        window.SaveRequested+=()=>window.SetError(english?"Isolated draft validated; no files saved.":"隔离草稿已检查；未保存任何正式设置。");
        window.WhitelistAddRequested+=()=>window.SetError("Fake callback only");
        window.WhitelistRemoveRequested+=()=>window.SetError("Fake callback only");
        window.RefreshRequested+=()=>window.SetStatus("✓ Display ready","Chrome","Playing","Isolated data");
        System.Threading.Timer ping=null;int pending=0;var delays=new System.Collections.Generic.Queue<double>();int moves=0,sizes=0;
        window.SourceInitialized+=(s,e)=>{if(visible)((HwndSource)PresentationSource.FromVisual(window)).AddHook(delegate(IntPtr h,int message,IntPtr w,IntPtr l,ref bool handled){if(message==0x0231)moves++;if(message==0x0005)sizes++;return IntPtr.Zero;});};
        if(visible)ping=new System.Threading.Timer(delegate {
            if(System.Threading.Interlocked.Exchange(ref pending,1)!=0)return;
            long started=Stopwatch.GetTimestamp();
            try{window.Dispatcher.BeginInvoke(new Action(delegate {
                delays.Enqueue((Stopwatch.GetTimestamp()-started)*1000.0/Stopwatch.Frequency);if(delays.Count>4096)delays.Dequeue();System.Threading.Interlocked.Exchange(ref pending,0);
            }),DispatcherPriority.Normal);}catch(InvalidOperationException){}
        },null,1000,16);
        var close=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(visible?600000:800)};
        bool first=true;
        window.ContentRendered+=(s,e)=>{
            if(!first)return;first=false;
            File.WriteAllText(Path.Combine(output,"dpi.txt"),GetDpiForWindow(new WindowInteropHelper(window).Handle).ToString());
            File.WriteAllText(Path.Combine(output,"ready-ms.txt"),timer.Elapsed.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            window.Dispatcher.BeginInvoke(new Action(()=>{
                var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
                var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(Path.Combine(output,"window.png")))png.Save(stream);
                close.Start();
            }),DispatcherPriority.Background);
        };
        close.Tick+=(s,e)=>{close.Stop();window.Close();};
        app.Run(window);
        if(ping!=null){ping.Dispose();File.WriteAllLines(Path.Combine(output,"message-delays-ms.txt"),System.Linq.Enumerable.Select(delays,x=>x.ToString(System.Globalization.CultureInfo.InvariantCulture)));File.WriteAllText(Path.Combine(output,"window-messages.txt"),"interactive move/resize entries="+moves+"; size messages="+sizes);}

        File.WriteAllText(Path.Combine(output,"boundary.txt"),(visible?"current input desktop; ":"private non-input desktop; ")+"fake callbacks; hooks=0; APPLY=0; formal settings=0; own process exits");
        return first?4:0;
    }
}
