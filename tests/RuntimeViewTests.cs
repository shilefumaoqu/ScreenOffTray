using System;
using System.Reflection;
using System.Runtime.Serialization;
using System.Net.Http;
using ScreenOffTray.Playback;
using ScreenOffTray.SettingsTransport;
using ScreenOffTray.Ui;
using OledPrototype;
namespace ScreenOffTray.Tests
{
    internal static class RuntimeViewTests
    {
        private static int checks;
        private const BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic;
        private static void Set(object owner,string name,object value){owner.GetType().GetField(name,Fields).SetValue(owner,value);}
        private static void Check(bool value,string name){if(!value)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
        private sealed class Registrar:IHotkeyRegistrar {public bool TryRegister(int id,HotkeyBinding binding,out string error){throw new Exception("No registration allowed");}public bool Unregister(int id){throw new Exception("No registration allowed");}}
        private sealed class Evidence:IDisplayEvidenceStore {public DisplayEvidence Load(){return new DisplayEvidence{Observed="p",Completed="p",Checked=DateTime.UtcNow.ToString("o")};}public void Save(DisplayEvidence value){throw new Exception("No persistence allowed");}}
        private sealed class Clock:IAutoSetupClock {public long Milliseconds{get{return 0;}}}
        private sealed class Scheduler:IAutoSetupScheduler {public IDisposable Schedule(Action action,int ms){throw new Exception("No work scheduled");}}
        private sealed class Http:HttpMessageHandler {protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,System.Threading.CancellationToken token){throw new Exception("No HTTP allowed");}}
        private static int Main()
        {
            OledSession session=null;
            try {
                session=(OledSession)FormatterServices.GetUninitializedObject(typeof(OledSession));
                Set(session,"flow",new SessionFlow());Set(session,"hotkeys",new HotkeyRegistration(new Registrar(),HotkeyBinding.Default));
                Set(session,"stopBarrier",new RecoveryStopBarrier());
                typeof(OledSession).GetProperty("Supported",Fields).GetSetMethod(true).Invoke(session,new object[]{true});
                var root=(TrayApplicationContext)FormatterServices.GetUninitializedObject(typeof(TrayApplicationContext));
                using(var setup=new DisplaySetupState(a=>{throw new Exception("No native work permitted");},new Evidence(),()=>{throw new Exception();},()=>{throw new Exception();},new Clock(),new Scheduler()))
                using(var updates=new UpdateChecker("",new Version("0.1.9.11"),new Http(),TimeSpan.FromSeconds(2),4096)) {
                    Set(setup,"ready",true);Set(setup,"validUntil",35000L);
                    var options=new PlaybackOptions{WhitelistPaths=new[]{@"C:\Fixture\player.exe"}};
                    var audio=new AudioSnapshot{Known=true,Sources=new[]{new AudioSource{ExePath=options.WhitelistPaths[0],DisplayName="Player A"}}};
                    var timing=new AutoOffTimingResult(45000,new PlaybackProtectionState(AutoOffPauseReason.Playback,false,"Player A"));
                    Set(root,"_session",session);Set(root,"_setup",setup);Set(root,"_updates",updates);Set(root,"_migrationDone",true);Set(root,"_text",new UiText("zh-CN"));Set(root,"_timeoutMinutes",3);Set(root,"_playbackOptions",options);Set(root,"_snapshotVersion",51L);
                    Set(root,"_startupEnabled",true);
                    Set(root,"_setupPrompts",new SetupPromptPolicy(true,"",true));
                    Set(root,"_runtimeSnapshot",new RuntimeSnapshot(51,"test",true,false,false,audio,options,timing,45000,""));
                    audio.Sources[0].DisplayName="producer changed";
                    var export=typeof(TrayApplicationContext).GetMethod("CreateUiState",Fields);
                    var state=(ViewState)export.Invoke(root,null);
                    Check(state.Applied.Startup,"verified startup value is preserved in initial UI draft");
                    Check(state.Version==51 && state.Sources[0].Name=="Player A" && state.Sources[0].Whitelisted && state.Sources[0].Protected,"export uses one published audio version and backend-confirmed protection");
                    Check(state.ProtectionEn.Contains("Player A") && !state.ProtectionEn.Contains("producer changed"),"playback reason matches displayed source sample");
                    state.Whitelist[0]="mutated";state.Sources[0].Name="mutated";state.Applied.Minutes=100;
                    var again=(ViewState)export.Invoke(root,null);
                    Check(again.Whitelist[0]==options.WhitelistPaths[0] && again.Sources[0].Name=="Player A" && again.Applied.Minutes==3,"export mutations cannot change applied owner state");
                    Check(again.Ready && again.UpdateEn.Contains("not configured"),"display readiness and blank update source are explicit");
                    Set(root,"_savingSettings",true);Check(!((ViewState)export.Invoke(root,null)).CanClose,"pending settings transaction keeps close gate");Set(root,"_savingSettings",false);
                    Packet result=null;typeof(TrayApplicationContext).GetMethod("HandleUiCommand",Fields).Invoke(root,new object[]{new Packet{Kind="arbitrary-path-operation"},new Action<Packet>(p=>result=p)});
                    Check(result!=null && result.Code=="unsupported_command","unknown command cannot execute an arbitrary operation");
                    Set(root,"_migrationDone",false);
                    var loading=(ViewState)export.Invoke(root,null);
                    Check(loading.Applied==null && loading.CanClose && !loading.CanPrepare,"Settings opens while loading without an editable stale draft");
                    Set(root,"_migrationDone",true);
                    var flow=(SessionFlow)typeof(OledSession).GetField("flow",Fields).GetValue(session);
                    flow.TryBegin(true,false,false);flow.BeginSwitch();flow.MarkOff();flow.BeginRestore();
                    var restoring=(ViewState)export.Invoke(root,null);
                    Check(restoring.StatusEn=="Restoring" && !restoring.Ready && !restoring.CanPrepare,"Settings shows real recovery state and protects display writes");
                    var retry=new UnexpectedDisplayRetry();
                    Set(root,"_displayRetry",retry);Set(root,"_autoClock",System.Diagnostics.Stopwatch.StartNew());
                    var schedule=typeof(TrayApplicationContext).GetMethod("ScheduleDisplayRetry",Fields);
                    Set(root,"_paused",true);schedule.Invoke(root,new object[]{7u});
                    Check(!retry.Pending,"a manually turned-off display does not schedule automatic retry while paused");
                    Set(root,"_paused",false);schedule.Invoke(root,new object[]{7u});
                    Check(retry.Pending,"unpaused unexpected output can schedule retry");
                    retry.Cancel();
                    using(var first=new SettingsPipeHost(a=>a(),(p,c)=>c(new Packet()),delegate{},delegate{},delegate{}))
                    using(var other=new SettingsPipeHost(a=>a(),(p,c)=>c(new Packet()),delegate{},delegate{},delegate{})) {
                        Set(root,"_settingsHost",first);
                        schedule.Invoke(root,new object[]{7u});
                        Check(!retry.Pending,"an existing Settings editor pauses automatic retry before countdown");
                        typeof(TrayApplicationContext).GetMethod("ShowSettings",Fields).Invoke(root,null);
                        var replies=(System.Collections.Generic.Queue<Packet>)typeof(SettingsPipeHost).GetField("replies",Fields).GetValue(first);
                        Check(replies.Dequeue().Kind=="activate","settings activation goes through while display recovery owns the session");
                        flow.Complete(0,false);flow.TryBegin(true,false);
                        var retryAttempt=new PreparationAttempt(1,0,true);
                        Set(session,"automaticRequest",true);Set(session,"preparation",retryAttempt);
                        Set(session,"clock",System.Diagnostics.Stopwatch.StartNew());
                        Set(session,"swallowed",new System.Collections.Generic.HashSet<int>());
                        Set(session,"hotkeyInput",new HotkeyInputState());
                        Set(session,"exitNotifications",new ExitNotifications());
                        Set(root,"_retryOffSession",true);
                        typeof(TrayApplicationContext).GetMethod("ShowSettings",Fields).Invoke(root,null);
                        Check(replies.Dequeue().Kind=="activate" && !flow.Busy && retryAttempt.Cancelled && !retryAttempt.Commit(),
                            "opening Settings without physical input cancels a dispatched but uncommitted retry");
                        flow.TryBegin(true,false);flow.BeginSwitch();
                        var committedAttempt=new PreparationAttempt(2,0,true);committedAttempt.Commit();
                        Set(session,"preparation",committedAttempt);Set(root,"_retryOffSession",true);
                        typeof(TrayApplicationContext).GetMethod("ShowSettings",Fields).Invoke(root,null);
                        Check(replies.Dequeue().Kind=="activate" && flow.Phase==SessionPhase.SwitchingOff && !committedAttempt.Cancelled,
                            "Settings opens without interfering with an already committed display switch");
                        var exit=typeof(TrayApplicationContext).GetMethod("UiExited",Fields);exit.Invoke(root,new object[]{other});
                        var hostField=root.GetType().GetField("_settingsHost",Fields);
                        Check(Object.ReferenceEquals(hostField.GetValue(root),first),"late exit from another UI cannot release current editor gate");
                        exit.Invoke(root,new object[]{first});Check(hostField.GetValue(root)==null,"actual matching UI exit releases editor gate");
                    }
                    GC.SuppressFinalize(root);
                }
                Check(!TrayApplicationContext.NeedsShortcutReservation(HotkeyBinding.Default,HotkeyBinding.Default,false,true,false),"unrelated setting save does not retry occupied unchanged shortcut");
                Check(!TrayApplicationContext.NeedsShortcutReservation(HotkeyBinding.Default,HotkeyBinding.Default,false,false,false),"unrelated save does not depend on hook availability");
                Check(TrayApplicationContext.NeedsShortcutReservation(HotkeyBinding.Default,HotkeyBinding.Default,false,true,true),"explicit re-entry can recover an initially unregistered shortcut");
                Check(!TrayApplicationContext.NeedsShortcutReservation(HotkeyBinding.Default,HotkeyBinding.Default,true,true,true),"already active unchanged shortcut is never reregistered");
                Console.WriteLine("Checks="+checks+"; unconstructed coordinator/control, fake stores/registrar; no HWND, product Main, hooks, apply or config reads/writes");return 0;
            }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
            finally{if(session!=null)GC.SuppressFinalize(session);}
        }
    }
}
