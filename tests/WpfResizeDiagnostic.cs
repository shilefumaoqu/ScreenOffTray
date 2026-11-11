using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenOffTray.SettingsUi;

internal static class WpfResizeDiagnostic
{
    private static string output;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window,int message,IntPtr w,IntPtr l);
    [STAThread] private static int Main(string[] args)
    {
        if(args.Length!=4)return 2;output=args[0];
        try {
            var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
            app.Startup+=async delegate {
                try {
                    File.WriteAllText(Path.Combine(output,"tier.txt"),(RenderCapability.Tier>>16).ToString());
                    foreach(string variant in new[]{"minimal","sample","sample-software"}) {
                        Window window=variant=="minimal"?new Window{Width=1040,Height=760,Content=new TextBlock{Text="Isolated WPF resize"}}:new SettingsWindow(false);
                        window.ShowActivated=false;window.Show();if(variant=="sample-software")((HwndSource)PresentationSource.FromVisual(window)).CompositionTarget.RenderMode=RenderMode.SoftwareOnly;
                        await window.Dispatcher.InvokeAsync(delegate{},DispatcherPriority.ApplicationIdle);
                        IntPtr hwnd=new WindowInteropHelper(window).Handle;double factor=GetDpiForWindow(hwnd)/96.0;
                        SendMessage(hwnd,0x0231,IntPtr.Zero,IntPtr.Zero);
                        using(var process=Process.GetCurrentProcess())for(int i=0;i<60;i++) {
                            process.Refresh();var cpu=process.TotalProcessorTime;var clock=Stopwatch.StartNew();SetWindowPos(hwnd,IntPtr.Zero,0,0,(int)((640+(i%20)*18)*factor),(int)((610+(i%10)*7)*factor),0x0016);var width=clock.Elapsed.TotalMilliseconds;var size=width;
                            await window.Dispatcher.InvokeAsync(delegate{},DispatcherPriority.Background);process.Refresh();
                            File.AppendAllText(Path.Combine(output,"stages.csv"),variant+","+i+","+width+","+size+","+clock.Elapsed.TotalMilliseconds+","+(process.TotalProcessorTime-cpu).TotalMilliseconds+Environment.NewLine);
                        }
                        SendMessage(hwnd,0x0232,IntPtr.Zero,IntPtr.Zero);window.Close();
                    }
                    File.WriteAllText(Path.Combine(output,"ready-ms.txt"),"0");app.Shutdown();
                }catch(Exception ex){File.WriteAllText(Path.Combine(output,"failure.txt"),ex.ToString());app.Shutdown(1);}
            };return app.Run();
        }catch(Exception ex){File.WriteAllText(Path.Combine(output,"failure.txt"),ex.ToString());return 1;}
    }
}
