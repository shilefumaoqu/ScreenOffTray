using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using ScreenOffTray.Playback;

namespace ScreenOffTray.Tests
{
    // Fake COM contracts only: no MMDeviceEnumerator, sound, user input or display.
    internal static class AudioSessionLifetimeTests
    {
        private static int checks;
        private static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;Console.WriteLine("PASS "+message);}
        [MTAThread] private static int Main()
        {
            try {
                var first=new Control("initial",0);var manager=new Manager(first);int notifications=0;
                using(var endpoint=new Endpoint(manager,()=>notifications++)) {
                    Check(endpoint.Read().Sources.Length==0,"inactive session does not protect playback");
                    for(int i=0;i<12;i++)endpoint.Read();
                    Check(manager.Enumerations==1 && manager.Counts==1,"existing list initialized once with notification-enabling GetCount");
                    Check(first.StateReads>=13,"session state still polled on every read");
                    first.State=1;first.NotifyState();
                    Check(notifications==1 && endpoint.Read().Sources[0].SessionCount==1,"inactive-to-playing uses existing state notification without re-enumeration");
                    var next=new Control("new",1);manager.Announce(next);
                    Check(endpoint.Read().Sources[0].SessionCount==2 && manager.Enumerations==1,"new session notification extends the authoritative list");
                    manager.Announce(next);endpoint.Read();
                    Check(next.Registers==1,"duplicate notification does not duplicate retained control or event subscription");
                    first.State=2;
                    Check(endpoint.Read().Sources[0].SessionCount==1 && first.Unregisters==1,"expired control removed and notification unregistered");
                    next.FailState=true;
                    bool failed=false;try{endpoint.Read();}catch(COMException){failed=true;}
                    Check(failed,"state query failure propagates instead of becoming known-empty playback");
                    next.FailState=false;
                }
                Check(manager.Unregisters==1,"manager notification unregistered on close");
                using(var endpoint=new Endpoint(manager=new Manager(new Control("race-initial",1)),()=>{})) {
                    manager.OnCount=()=>manager.Announce(new Control("raced-notification",1));
                    Check(endpoint.Read().Sources[0].SessionCount==2,"notification racing initial enumeration is retained");
                }
                using(var endpoint=new Endpoint(manager=new Manager(new Control("retry",1)),()=>{})) {
                    manager.FailCount=true;bool failed=false;try{endpoint.Read();}catch(COMException){failed=true;}
                    Check(failed,"failed initial enumeration is not published as success");
                    manager.FailCount=false;
                    Check(endpoint.Read().Sources.Length==1 && manager.Enumerations==2,"failed initialization does not latch the initialized flag");
                }
                using(var endpoint=new Endpoint(manager=new Manager(new Control("overflow",1)),()=>{})) {
                    endpoint.Read();var duplicate=manager.Items[0];
                    for(int i=0;i<257;i++)manager.Announce(duplicate);
                    bool failed=false;try{endpoint.Read();}catch(InvalidOperationException){failed=true;}
                    Check(failed,"notification overflow fails closed for monitor recreation");
                }
                Check(manager.Unregisters==1,"overflow cleanup unregisters manager");
                Console.WriteLine("RESULT "+checks+" fake-contract checks; no real audio, input, display or settings");return 0;
            }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        }
        private sealed class Endpoint:IDisposable
        {
            private readonly object value;
            private readonly MethodInfo collect;
            internal Endpoint(Manager manager,Action changed){
                Type type=typeof(CoreAudioReader).GetNestedType("Endpoint",BindingFlags.NonPublic);
                value=Activator.CreateInstance(type,BindingFlags.NonPublic|BindingFlags.Instance,null,new object[]{new Device(manager),changed},null);
                collect=type.GetMethod("Collect",BindingFlags.NonPublic|BindingFlags.Instance);
            }
            internal AudioSnapshot Read(){
                var source=new CoreAudioReader.SourceCollection(delegate(uint pid,out long created){created=1;return @"C:\fake-audio\player.exe";});
                try{collect.Invoke(value,new object[]{source});}catch(TargetInvocationException ex){throw ex.InnerException;}
                return source.Build(new Dictionary<string,string>());
            }
            public void Dispose(){((IDisposable)value).Dispose();}
        }
        private sealed class Device:AudioNative.IMMDevice
        {
            private readonly Manager manager;internal Device(Manager manager){this.manager=manager;}
            public int Activate(ref Guid iid,uint context,IntPtr parameters,out object result){result=manager;return 0;}
            public int OpenPropertyStore(uint access,out IntPtr value){value=IntPtr.Zero;throw new NotSupportedException();}
            public int GetId(out string id){id="fake-endpoint";return 0;}
            public int GetState(out uint state){state=1;return 0;}
        }
        private sealed class Manager:AudioNative.IAudioSessionManager2
        {
            internal readonly List<Control> Items=new List<Control>();
            internal AudioNative.IAudioSessionNotification Sink;
            internal int Enumerations,Counts,Unregisters;internal bool FailCount;internal Action OnCount;
            internal Manager(Control control){Items.Add(control);}
            internal void Announce(Control control){if(!Items.Contains(control))Items.Add(control);if(Sink!=null)Sink.OnSessionCreated(control);}
            public int GetSessionEnumerator(out AudioNative.IAudioSessionEnumerator result){Enumerations++;result=new Enumeration(this,Items.ToArray());return 0;}
            public int RegisterSessionNotification(AudioNative.IAudioSessionNotification sink){Sink=sink;return 0;}
            public int UnregisterSessionNotification(AudioNative.IAudioSessionNotification sink){Unregisters++;Sink=null;return 0;}
            public int GetAudioSessionControl(ref Guid id,uint flags,out AudioNative.IAudioSessionControl control){control=null;throw new NotSupportedException();}
            public int GetSimpleAudioVolume(ref Guid id,uint flags,out IntPtr value){value=IntPtr.Zero;throw new NotSupportedException();}
            public int RegisterDuckNotification(string id,IntPtr notification){throw new NotSupportedException();}
            public int UnregisterDuckNotification(IntPtr notification){throw new NotSupportedException();}
        }
        private sealed class Enumeration:AudioNative.IAudioSessionEnumerator
        {
            private readonly Manager manager;private readonly Control[] values;
            internal Enumeration(Manager manager,Control[] values){this.manager=manager;this.values=values;}
            public int GetCount(out int count){manager.Counts++;count=values.Length;if(manager.FailCount)return unchecked((int)0x80004005);if(manager.OnCount!=null){var action=manager.OnCount;manager.OnCount=null;action();}return 0;}
            public int GetSession(int index,out AudioNative.IAudioSessionControl control){control=values[index];return 0;}
        }
        private sealed class Control:AudioNative.IAudioSessionControl,AudioNative.IAudioSessionControl2
        {
            private readonly string id;private AudioNative.IAudioSessionEvents sink;
            internal int State,StateReads,Registers,Unregisters;internal bool FailState;
            internal Control(string id,int state){this.id=id;State=state;}
            internal void NotifyState(){if(sink!=null)sink.OnStateChanged(State);}
            public int GetState(out int state){StateReads++;state=State;return FailState?unchecked((int)0x80004005):0;}
            public int GetSessionIdentifier(out string value){value=id;return 0;}
            public int GetSessionInstanceIdentifier(out string value){value=id;return 0;}
            public int GetProcessId(out uint pid){pid=101;return 0;}
            public int IsSystemSoundsSession(){return 1;}
            public int SetDuckingPreference(bool value){return 0;}
            public int RegisterAudioSessionNotification(AudioNative.IAudioSessionEvents value){Registers++;sink=value;return 0;}
            public int UnregisterAudioSessionNotification(AudioNative.IAudioSessionEvents value){Unregisters++;sink=null;return 0;}
            public int GetDisplayName(out string value){value=id;return 0;}
            public int SetDisplayName(string value,ref Guid context){return 0;}
            public int GetIconPath(out string value){value="";return 0;}
            public int SetIconPath(string value,ref Guid context){return 0;}
            public int GetGroupingParam(out Guid value){value=Guid.Empty;return 0;}
            public int SetGroupingParam(ref Guid value,ref Guid context){return 0;}
        }
    }
}
