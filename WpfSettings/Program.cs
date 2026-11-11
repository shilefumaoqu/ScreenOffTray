using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ScreenOffTray.SettingsTransport;

namespace ScreenOffTray.SettingsUi
{
    internal static class Program
    {
        private static Application application;
        private static SettingsClient client;
        private static SettingsWindow window;
        private static ViewState state;
        private static bool saving,closing,changingLanguage,closingFromBackend,disconnected,listChanging;
        private static string language;
        private static bool rendered,presented;
        [STAThread] private static int Main(string[] args)
        {
            if(args.Length!=5 || args[0]!="--connect")return 2;
            int parent;long ticks;if(!Int32.TryParse(args[2],out parent) || !Int64.TryParse(args[3],out ticks))return 2;
            BackendLifetime lifetime;
            try{lifetime=new BackendLifetime(parent,ticks);}catch{return 1;}
            language=args[4]=="en"?"en":"zh-CN";
            try {
                var artwork=SettingsWindow.PreloadArtwork();
                if(Environment.GetEnvironmentVariable("SCREENOFF_WPF_SOFTWARE_ONLY")=="1")
                    System.Windows.Media.RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
                application=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
                client=new SettingsClient(args[1],parent,ticks);
                client.Received+=Receive;
                client.Disconnected+=delegate {application.Dispatcher.BeginInvoke(new Action(delegate {disconnected=true;if(window!=null){window.SetDisconnected();window.SetError("连接已断开，请关闭后重新打开设置。 / Connection lost. Close and reopen Settings.");}}));};
                client.StartReceiving();artwork.GetAwaiter().GetResult();Build(null);application.Run();return 0;
            }catch(Exception ex){if(lifetime.IsAlive)MessageBox.Show(ex.Message,"ScreenOffTray",MessageBoxButton.OK,MessageBoxImage.Warning);return 1;}
            finally {if(client!=null)client.Dispose();lifetime.Dispose();}
        }
        private static void Receive(Packet packet)
        {
            application.Dispatcher.BeginInvoke(new Action(delegate {
                if(window==null)return;
                if(packet.Kind=="error"){window.SetError(packet.Error);return;}
                if(packet.Kind=="reconnected"){client.Notify(new Packet{Kind="capture",Flag=window.IsCaptureFocused});return;}
                if(packet.Kind=="state"){
                    bool first=state==null;
                    if(packet.State==null || (state!=null && packet.State.Version<=state.Version))return;
                    state=packet.State;
                    if(first && state.Applied!=null)ApplyValues(state.Applied);
                    Present();AnnouncePresented();if(closing && state.CanClose){window.Close();}
                } else if(packet.Kind=="key")window.ObserveKey(packet.Key,packet.Modifiers,packet.Up);
                else if(packet.Kind=="activate"){if(window.WindowState==WindowState.Minimized)window.WindowState=WindowState.Normal;window.Activate();}
                else if(packet.Kind=="close"){closingFromBackend=true;window.Close();}
            }));
        }
        private static void Build(FormValues draft)
        {
            var previous=window;
            var next=new SettingsWindow(language=="en");rendered=false;window=next;application.MainWindow=next;
            next.SaveRequested+=Save;
            next.AddPathRequested+=Add;
            next.WhitelistRemoveRequested+=Remove;
            next.RecheckRequested+=delegate {Send("recheck");};
            next.RefreshRequested+=delegate {Send("refresh");};
            next.MigrationRequested+=ChooseMigration;
            next.DisplaySettingsRequested+=delegate {Send("displaySettings");};
            next.PrepareRequested+=delegate {Send("prepare");};
            next.CancelPrepareRequested+=delegate {Send("cancelPrepare");};
            next.UpdateRequested+=delegate {Send("checkUpdate");};
            next.CaptureChanged+=focus=>client.Notify(new Packet{Kind="capture",Flag=focus});
            next.LanguageRequested+=delegate {
                if(changingLanguage || window!=next)return;
                var values=Draft();language=values.Language;changingLanguage=true;
                try {Build(values);}finally{changingLanguage=false;}
            };
            next.Closing+=Closing;
            next.Closed+=delegate {if(window==next && !changingLanguage){client.Notify(new Packet{Kind="capture",Flag=false});window=null;application.Shutdown();}};
            next.ContentRendered+=delegate {if(window==next){rendered=true;AnnouncePresented();}};
            if(draft!=null)ApplyValues(draft);else if(state!=null && state.Applied!=null)ApplyValues(state.Applied);
            if(state!=null)Present();else{next.SetBusy(true);next.SetStatus("正在读取设置… / Loading settings…","","","");}next.Show();if(previous!=null)previous.Close();
        }
        private static void AnnouncePresented()
        {if(!presented && rendered && state!=null && window!=null){presented=true;client.Notify(new Packet{Kind="shown"});}}
        private static FormValues Draft()
        {return new FormValues{Minutes=window.Minutes,Hotkey=window.Shortcut,HotkeyEdited=window.ShortcutEdited,Language=window.UiLanguage,Startup=window.Startup,AutomaticUpdates=window.Updates,LimitEnabled=window.Cap,LimitMinutes=window.ProtectionMinutes,InterfaceTheme=window.InterfaceTheme};}
        private static void ApplyValues(FormValues values)
        {window.SetValues(values.Minutes,values.Hotkey,values.Language,values.Startup,values.AutomaticUpdates,values.LimitEnabled,values.LimitMinutes,values.InterfaceTheme);window.ShortcutEdited=values.HotkeyEdited;}
        private static void Present()
        {
            bool en=language=="en";var first=state.Sources==null?null:state.Sources.FirstOrDefault();
            window.SetStatus(en?state.StatusEn:state.StatusZh,first==null?(en?"No apps playing":"暂无正在播放的程序"):first.Name,
                first==null?"":(en?"Playing":"正在播放"),en?state.ProtectionEn:state.ProtectionZh);
            window.SetList(state.Whitelist??new string[0]);window.SetSources(state.Sources,state.AudioKnown);window.SetUpdate(en?state.UpdateEn:state.UpdateZh);
            window.SetMigrationChoices(state.MigrationChoices);window.SetListBusy(listChanging || state.SavingWhitelist);
            window.ShowSetup(state.Ready,state.Checking,state.Preparing,state.CanPrepare,en?state.DetailEn:state.DetailZh,state.Diagnostics);
            if(disconnected)window.SetDisconnected();else window.SetBusy(saving || state.Saving);
        }
        private static async void ChooseMigration(int minutes)
        {
            if(saving || disconnected)return;saving=true;window.SetBusy(true);
            try{var result=await client.Command(new Packet{Kind="migrationChoice",Values=new FormValues{Minutes=minutes}});
                if(window!=null){if(!String.IsNullOrEmpty(result.Error))window.SetError(result.Error);else if(state!=null)ApplyValues(state.Applied);}
            }catch(Exception ex){if(window!=null)window.SetError(ex.Message);}
            finally{saving=false;if(window!=null)Present();}
        }
        private static async void Save()
        {
            if(saving || disconnected || state==null)return;FormValues draft=Draft();saving=true;window.SetBusy(true);
            try {var result=await client.Command(new Packet{Kind="save",Values=draft});if(window==null)return;if(!String.IsNullOrEmpty(result.Error)){window.SetError(result.Error);return;}saving=false;window.Close();}
            catch(Exception ex){if(window!=null)window.SetError(ex.Message);}
            finally{saving=false;if(window!=null){if(disconnected)window.SetDisconnected();else window.SetBusy(false);}}
        }
        private static void Add(string path)
        {if(state==null || state.Sources==null || state.Sources.Length==0)return;ChangeList("whitelistAdd",path);}
        private static void Remove()
        {if(state==null || window.SelectedPath==null)return;ChangeList("whitelistRemove",window.SelectedPath);}
        private static async void ChangeList(string kind,string path)
        {if(listChanging || disconnected)return;listChanging=true;window.SetListBusy(true);
            try{var result=await client.Command(new Packet{Kind=kind,Paths=new[]{path}});if(window!=null && !String.IsNullOrEmpty(result.Error))window.SetError(result.Error);}
            catch(Exception ex){if(window!=null)window.SetError(ex.Message);}
            finally{listChanging=false;if(window!=null)window.SetListBusy(false);}
        }
        private static async void Send(string kind)
        {try{var result=await client.Command(new Packet{Kind=kind});if(window!=null && !String.IsNullOrEmpty(result.Error))window.SetError(result.Error);}catch(Exception ex){if(window!=null)window.SetError(ex.Message);}}
        private static void Closing(object sender,CancelEventArgs args)
        {
            if(sender!=window || changingLanguage || closingFromBackend || disconnected)return;
            if(saving){args.Cancel=true;return;}
            if(state!=null && !state.CanClose){args.Cancel=true;if(!closing){closing=true;Send("cancelPrepare");}window.SetError("正在安全结束准备，请稍候。 / Finishing preparation safely.");}
        }
    }
}
