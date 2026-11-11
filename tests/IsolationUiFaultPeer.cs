using System;
using System.Threading;
using ScreenOffTray.SettingsTransport;
using ScreenOffTray.SettingsUi;
internal static class IsolationUiFaultPeer
{
    private static int Main(string[] args)
    {
        if(args.Length!=5 || args[0]!="--connect")return 2;
        if(Environment.GetEnvironmentVariable("SCREENOFF_TEST_UI_FAULT")=="before")return 13;
        using(var client=new SettingsClient(args[1],Int32.Parse(args[2]),Int64.Parse(args[3])))using(var state=new ManualResetEvent(false)) {
            client.Received+=p=>{if(p.Kind=="state")state.Set();};client.StartReceiving();if(!state.WaitOne(3000))return 14;
            using(var close=new ManualResetEvent(false)){
                client.Received+=p=>{if(p.Kind=="close")close.Set();};client.Notify(new Packet{Kind="shown"});
                if(Environment.GetEnvironmentVariable("SCREENOFF_TEST_UI_FAULT")=="dwell"){if(!close.WaitOne(7000))return 15;}else Thread.Sleep(200);
            }
            // A successful exit code alone is insufficient: there was no dwell
            // or requested close. The resource supervisor must reject this cycle.
            return 0;
        }
    }
}
