using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ScreenOffTray.Ui;
using ScreenOffTray.Playback;

namespace ScreenOffTray.Tests
{
    internal static class OptimizationUiLifetimeTests
    {
        static int checks;
        static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;Console.WriteLine("PASS "+message);}
        static void PumpUntil(Func<bool> done){var time=Stopwatch.StartNew();while(!done() && time.ElapsedMilliseconds<5000){Application.DoEvents();Thread.Sleep(5);}Application.DoEvents();}
        static T Find<T>(Control root,string name) where T:Control {if(root.Name==name)return (T)root;foreach(Control child in root.Controls){var found=Find<T>(child,name);if(found!=null)return found;}return null;}
        [STAThread] static int Main(string[] args)
        {
            if(args.Length!=4)return 2;
            string result=Path.Combine(args[0],"results.txt");
            try {
                Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
                using(SettingsDpiContext.EnterUi()) {
                    var form=new SettingsDialog(new UiText("zh-CN"),new SettingsValues{TimeoutMinutes=3,Hotkey=HotkeyBinding.Default,Language="zh-CN"},v=>null,()=>{},()=>{},()=>{});
                    var save=new TaskCompletionSource<string>();form.SaveAsync=v=>save.Task;
                    form.RefreshSetup(new SetupPanelState{Ready=true,Visible=false,Detail="fake ready",Diagnostics="fake"});
                    form.Show();Application.DoEvents();Find<Button>(form,"save").PerformClick();
                    form.Close();Check(!form.IsDisposed,"user close waits in-flight form persistence");
                    save.SetResult(null);PumpUntil(()=>form.IsDisposed);
                    Check(form.IsDisposed,"successful async save closes window instead of retaining editor gate");
                    var host=new Form();var panel=new PlaybackSettingsPanel(new UiText("zh-CN"),true,60,()=>{},p=>null);host.Controls.Add(panel);
                    panel.RefreshState(new PlaybackPanelState {Audio=new AudioSnapshot{Known=true,Sources=new AudioSource[0],SampleUtc=DateTime.UtcNow},AppliedOptions=new PlaybackOptions{WhitelistPaths=new[]{@"C:\Windows\System32\notepad.exe"}},SettingsWarning=""});
                    var list=new TaskCompletionSource<string>();panel.SaveWhitelistAsync=p=>list.Task;
                    host.Show();Application.DoEvents();
                    typeof(PlaybackSettingsPanel).GetMethod("Persist",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(panel,new object[]{new string[0]});
                    host.Close();host.Dispose();Check(panel.IsDisposed,"whitelist view may close while backend transaction continues");
                    list.SetResult(null);PumpUntil(()=>list.Task.IsCompleted);Application.DoEvents();
                    Check(panel.IsDisposed,"late whitelist completion leaves disposed view untouched");
                    File.WriteAllText(result,"Checks="+checks+"; private desktop; fake persistence; hooks=0; APPLY=0; formal writes=0");
                    return 0;
                }
            }catch(Exception ex){File.WriteAllText(result,ex.ToString());return 1;}
        }
    }
}
