using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using ScreenOffTray;
namespace ScreenOffTray {internal static class Program {}}
namespace OledPrototype {internal static class Program {internal static void Log(string text){}}}
internal static class StartupTests
{
    private static int checks;
    private static void Check(bool value,string name){if(!value)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
    private static int Main(string[] args)
    {
        if(VerifiedLaunch.ValidReportArguments(args))return Peer(args);
        try {
            string output=AppDomain.CurrentDomain.BaseDirectory;
            Environment.SetEnvironmentVariable("SCREENOFF_STARTUP_TEST_DIR",output);
            string[] valid;using(var process=Process.GetCurrentProcess())valid=new[]{"--startup-report",Guid.NewGuid().ToString("N"),process.Id.ToString(),process.StartTime.ToUniversalTime().Ticks.ToString()};
            Check(VerifiedLaunch.ValidReportArguments(valid) && VerifiedLaunch.BrokerStillAlive(valid),"valid internal launch binds PID, start time and exact executable");
            var invalid=(string[])valid.Clone();invalid[1]="not-a-guid";Check(!VerifiedLaunch.ValidReportArguments(invalid),"invalid launch nonce rejected");
            invalid=(string[])valid.Clone();invalid[2]="0";Check(!VerifiedLaunch.ValidReportArguments(invalid),"invalid process identity rejected");
            foreach(var test in new[]{new Case("ready",false,0),new Case("pending",false,10),new Case("pending-no-window",false,11),new Case("ready-no-window",true,11),new Case("exit",false,11)}) {
                Environment.SetEnvironmentVariable("SCREENOFF_STARTUP_TEST_MODE",test.Mode);
                Check(VerifiedLaunch.Run(typeof(StartupTests).Assembly.Location,test.Setup,3000)==test.Expected,"launch result: "+test.Mode+" setup="+test.Setup);
            }
            Environment.SetEnvironmentVariable("SCREENOFF_STARTUP_TEST_MODE","timeout");
            var clock=Stopwatch.StartNew();int result=VerifiedLaunch.Run(typeof(StartupTests).Assembly.Location,false,150);
            Check(result==11 && clock.ElapsedMilliseconds<1000,"deadline is independent of unfinished startup/report work");
            string identity=null;Check(SpinWait.SpinUntil(()=>{var files=Directory.GetFiles(output,"timeout-*.txt");if(files.Length==0)return false;identity=files[0];return true;},1500),"owned no-report peer wrote identity");
            string[] lines=File.ReadAllLines(identity);
            using(var child=Process.GetProcessById(Int32.Parse(lines[0]))) {
                Check(child.StartTime.ToUniversalTime().Ticks==Int64.Parse(lines[1]) && !child.HasExited,"verification timeout does not kill the product peer");
                File.WriteAllText(identity+".stop","stop");Check(child.WaitForExit(2500),"only the identity-matched fake peer stops normally");
            }
            Console.WriteLine("Checks="+checks+"; fake startup reports only; no product Main, hooks, devices, configuration or install");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    private sealed class Case {internal string Mode;internal bool Setup;internal int Expected;internal Case(string mode,bool setup,int expected){Mode=mode;Setup=setup;Expected=expected;}}
    private static int Peer(string[] args)
    {
        string mode=Environment.GetEnvironmentVariable("SCREENOFF_STARTUP_TEST_MODE");
        if(mode=="exit")return 0;
        if(mode=="timeout") {
            string path=Path.Combine(Environment.GetEnvironmentVariable("SCREENOFF_STARTUP_TEST_DIR"),"timeout-"+args[1]+".txt");
            using(var self=Process.GetCurrentProcess())File.WriteAllLines(path,new[]{self.Id.ToString(),self.StartTime.ToUniversalTime().Ticks.ToString()});
            var clock=Stopwatch.StartNew();while(!File.Exists(path+".stop") && clock.ElapsedMilliseconds<6000)Thread.Sleep(20);return 0;
        }
        VerifiedLaunch.Report(args,mode.StartsWith("ready"),!mode.EndsWith("no-window"));Thread.Sleep(600);return 0;
    }
}
