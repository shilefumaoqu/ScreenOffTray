using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ScreenOffTray.Playback
{
    internal interface IAudioReader : IDisposable { AudioSnapshot Read(); }

    // One background MTA owns all Core Audio objects. A blocked native read is
    // never replaced by another thread. Closing the form never joins this thread.
    internal sealed class AudioMonitor : IAudioMonitor
    {
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly AutoResetEvent refresh = new AutoResetEvent(false);
        private readonly object signalGate = new object();
        private readonly Thread worker;
        private readonly Func<IAudioReader> readerFactory;
        private readonly IMediaStateReader mediaState;
        private readonly int pollMilliseconds;
        private readonly int timeoutMilliseconds;
        private volatile AudioSnapshot latest = AudioSnapshot.Unknown("正在读取当前播放程序…");
        private int disposed, reading, completed;
        private long readStarted;
        private bool handlesClosed;

        internal AudioMonitor() : this(null, 5000, 3000, true) { }

        internal AudioMonitor(Func<AudioSnapshot> read, Action cleanup, int pollMilliseconds, int timeoutMilliseconds)
            : this(delegate { return new DelegateAudioReader(read, cleanup); }, pollMilliseconds, timeoutMilliseconds) { }

        internal AudioMonitor(Func<IAudioReader> factory, int pollMilliseconds, int timeoutMilliseconds)
            : this(factory, pollMilliseconds, timeoutMilliseconds, false) { }

        private AudioMonitor(Func<IAudioReader> factory, int pollMilliseconds, int timeoutMilliseconds, bool native)
        {
            if (factory == null && !native) throw new ArgumentNullException("factory");
            if (pollMilliseconds < 1 || timeoutMilliseconds < 1) throw new ArgumentOutOfRangeException("pollMilliseconds");
            if (native)
            {
                mediaState = new WinRtMediaStateReader(RequestRefresh);
                readerFactory = delegate { return new PauseAwareAudioReader(new CoreAudioReader(RequestRefresh), mediaState,
                    delegate { return new WindowsMediaSourceResolver(); }, MediaClock.Milliseconds); };
            }
            else readerFactory = factory;
            this.pollMilliseconds = pollMilliseconds; this.timeoutMilliseconds = timeoutMilliseconds;
            worker = new Thread(ReadLoop) { IsBackground = true, Name = "PlaybackPrototype.AudioReader" };
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
        }

        public bool IsReading { get { return Interlocked.CompareExchange(ref reading, 0, 0) != 0; } }
        internal bool HasSnapshot { get { return Interlocked.CompareExchange(ref completed, 0, 0) != 0; } }
        public AudioSnapshot Latest
        {
            get
            {
                if (Interlocked.CompareExchange(ref disposed, 0, 0) != 0)
                    return AudioSnapshot.Unknown("音频读取已停止");
                if (IsReading && ElapsedMilliseconds(Interlocked.Read(ref readStarted)) > timeoutMilliseconds)
                    return AudioSnapshot.Unknown("音频读取超时，暂缓自动关屏判断");
                AudioSnapshot snapshot = latest;
                if ((DateTime.UtcNow - snapshot.SampleUtc).TotalSeconds > 15)
                    return AudioSnapshot.Unknown("音频状态已过期，暂缓自动关屏判断");
                return snapshot;
            }
        }

        public void RequestRefresh()
        {
            lock (signalGate) { if (!handlesClosed && disposed == 0) refresh.Set(); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (signalGate) { if (!handlesClosed) stop.Set(); }
        }

        private static double ElapsedMilliseconds(long started)
        { return (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency; }

        private void ReadLoop()
        {
            IAudioReader reader = null;
            try
            {
                while (!stop.WaitOne(0))
                {
                    Interlocked.Exchange(ref readStarted, Stopwatch.GetTimestamp());
                    Interlocked.Exchange(ref reading, 1);
                    AudioSnapshot next;
                    try
                    {
                        if (reader == null) reader = readerFactory();
                        if (reader == null) throw new InvalidOperationException("Missing audio reader");
                        next = reader.Read();
                    }
                    catch (Exception ex)
                    {
                        next = AudioSnapshot.Unknown("无法读取音频（0x" + ex.HResult.ToString("X8") + "）");
                        DropReader(ref reader);
                    }
                    if (next == null) next = AudioSnapshot.Unknown("音频读取没有返回状态");
                    if (ElapsedMilliseconds(Interlocked.Read(ref readStarted)) > timeoutMilliseconds)
                        next = AudioSnapshot.Unknown("音频读取超时，暂缓自动关屏判断");
                    if (Interlocked.CompareExchange(ref disposed, 0, 0) == 0) latest = next;
                    Interlocked.Exchange(ref completed, 1);
                    Interlocked.Exchange(ref reading, 0);
                    int signal = WaitHandle.WaitAny(new WaitHandle[] { stop, refresh }, pollMilliseconds);
                    if (signal == 0) break;
                    if (signal == 1)
                    {
                        if (stop.WaitOne(150)) break; // coalesce native state-change bursts
                        refresh.Reset();
                    }
                }
            }
            catch (Exception ex)
            {
                latest = AudioSnapshot.Unknown("音频检测不可用（0x" + ex.HResult.ToString("X8") + "）");
                Interlocked.Exchange(ref completed, 1);
            }
            finally
            {
                Interlocked.Exchange(ref reading, 0);
                DropReader(ref reader);
                if (mediaState != null) { try { mediaState.Dispose(); } catch { } }
                lock (signalGate)
                {
                    handlesClosed = true;
                    refresh.Dispose(); stop.Dispose();
                }
            }
        }

        private static void DropReader(ref IAudioReader reader)
        {
            IAudioReader old = reader; reader = null;
            if (old != null) { try { old.Dispose(); } catch { } }
        }

        private sealed class DelegateAudioReader : IAudioReader
        {
            private readonly Func<AudioSnapshot> read;
            private readonly Action cleanup;
            private int closed;
            internal DelegateAudioReader(Func<AudioSnapshot> read, Action cleanup)
            { if (read == null) throw new ArgumentNullException("read"); this.read = read; this.cleanup = cleanup; }
            public AudioSnapshot Read() { return read(); }
            public void Dispose() { if (Interlocked.Exchange(ref closed, 1) == 0 && cleanup != null) cleanup(); }
        }
    }

    internal sealed class CoreAudioReader : IAudioReader
    {
        private readonly AudioNative.IMMDeviceEnumerator enumerator;
        private readonly Dictionary<string, Endpoint> endpoints = new Dictionary<string, Endpoint>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> sourceNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Action changed;

        internal CoreAudioReader() : this(null) { }
        internal CoreAudioReader(Action changed)
        { this.changed = changed; enumerator = (AudioNative.IMMDeviceEnumerator)new AudioNative.MMDeviceEnumerator(); }

        public AudioSnapshot Read()
        {
            AudioNative.IMMDeviceCollection devices = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var sources = new SourceCollection(AudioNative.ReadProcessPath);
            try
            {
                AudioNative.Check(enumerator.EnumAudioEndpoints(0, 1, out devices)); // render, active only
                uint count; AudioNative.Check(devices.GetCount(out count));
                if (count == 0) return AudioSnapshot.Unknown("当前没有可读取的音频输出设备");
                if (count > 128) throw new InvalidOperationException("Too many render endpoints");
                for (uint i = 0; i < count; i++)
                {
                    AudioNative.IMMDevice device = null;
                    try
                    {
                        AudioNative.Check(devices.Item(i, out device));
                        string id; AudioNative.Check(device.GetId(out id));
                        seen.Add(id);
                        Endpoint endpoint;
                        if (!endpoints.TryGetValue(id, out endpoint))
                        {
                            endpoint = new Endpoint(device, changed);
                            endpoints.Add(id, endpoint);
                        }
                        endpoint.Collect(sources);
                    }
                    finally { AudioNative.Release(device); }
                }
                return sources.Build(sourceNames);
            }
            finally
            {
                AudioNative.Release(devices);
                // Do not retain devices that disappeared or inactive sessions on
                // removed endpoints. This cleanup remains on the audio thread.
                var gone = new List<string>();
                foreach (string id in endpoints.Keys) if (!seen.Contains(id)) gone.Add(id);
                foreach (string id in gone) { endpoints[id].Dispose(); endpoints.Remove(id); }
            }
        }

        public void Dispose()
        {
            foreach (Endpoint endpoint in endpoints.Values) endpoint.Dispose();
            endpoints.Clear(); AudioNative.Release(enumerator);
        }

        internal delegate string ProcessPathReader(uint pid, out long created);

        // Per-query collection: inaccessible identities never become whitelist candidates.
        internal sealed class SourceCollection
        {
            private readonly ProcessPathReader readPath;
            private readonly Dictionary<string, SourceBuilder> sources = new Dictionary<string, SourceBuilder>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<uint> unidentified = new HashSet<uint>();

            internal SourceCollection(ProcessPathReader readPath) { this.readPath = readPath; }

            internal void Add(uint pid, ref uint previousPid, ref long previousCreated)
            {
                long created;
                string path;
                try { path = readPath(pid, out created); }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    // Only this process-identity access failure is partial. COM,
                    // device and other Win32 failures must still fail the query.
                    if (ex.NativeErrorCode != 5) throw;
                    unidentified.Add(pid);
                    return;
                }
                if (previousCreated != 0 && (previousPid != pid || previousCreated != created))
                    throw new InvalidOperationException("Audio owner identity changed");
                previousCreated = created; previousPid = pid;
                SourceBuilder source;
                if (!sources.TryGetValue(path, out source)) { source = new SourceBuilder { Path = path }; sources.Add(path, source); }
                source.Pids.Add(checked((int)pid)); source.Count++;
            }

            internal AudioSnapshot Build(Dictionary<string, string> sourceNames)
            {
                var result = new List<AudioSource>();
                foreach (SourceBuilder source in sources.Values)
                {
                    string name;
                    if (!sourceNames.TryGetValue(source.Path, out name))
                    {
                        name = AudioSourceNames.Read(source.Path);
                        if (sourceNames.Count >= 128) sourceNames.Clear();
                        sourceNames[source.Path] = name;
                    }
                    result.Add(source.Build(name));
                }
                result.Sort(delegate(AudioSource a, AudioSource b) { return StringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName); });
                return new AudioSnapshot { Known = true, Sources = result.ToArray(), SampleUtc = DateTime.UtcNow,
                    UnidentifiedSourceCount = unidentified.Count,
                    Warning = unidentified.Count == 0 ? null : "有 " + unidentified.Count + " 个正在活动的音频进程因访问被拒绝无法确认身份，来源列表不完整；这些来源不参与白名单匹配。" };
            }
        }

        private sealed class SourceBuilder
        {
            internal string Path;
            internal readonly HashSet<int> Pids = new HashSet<int>();
            internal int Count;
            internal AudioSource Build(string name)
            {
                var ids = new int[Pids.Count]; Pids.CopyTo(ids); Array.Sort(ids);
                return new AudioSource { ExePath = Path, DisplayName = name, SessionCount = Count, ProcessIds = ids };
            }
        }

        private sealed class Session
        {
            internal AudioNative.IAudioSessionControl Control;
            internal AudioStateSink Events;
            internal long ProcessCreated;
            internal uint ProcessId;
            internal void Close()
            {
                if (Events != null) Events.Stop();
                if (Control != null && Events != null) { try { Control.UnregisterAudioSessionNotification(Events); } catch { } }
                AudioNative.Release(Control); Control = null; Events = null;
            }
        }

        private sealed class Endpoint : IDisposable
        {
            private AudioNative.IAudioSessionManager2 manager;
            private SessionSink sink;
            private readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>(StringComparer.Ordinal);
            private readonly Action changed;

            internal Endpoint(AudioNative.IMMDevice device, Action changed)
            {
                this.changed = changed;
                object activated = null;
                try
                {
                    Guid iid = typeof(AudioNative.IAudioSessionManager2).GUID;
                    AudioNative.Check(device.Activate(ref iid, 23, IntPtr.Zero, out activated));
                    manager = (AudioNative.IAudioSessionManager2)activated;
                    sink = new SessionSink(changed);
                    AudioNative.Check(manager.RegisterSessionNotification(sink));
                }
                catch
                {
                    if (sink != null) sink.Stop();
                    AudioNative.Release(activated); manager = null;
                    throw;
                }
            }

            internal void Collect(SourceCollection sources)
            {
                AudioNative.IAudioSessionEnumerator current = null;
                try
                {
                    // Register first, then GetCount: notifications supplement
                    // enumeration so newly created sessions are not lost.
                    AudioNative.Check(manager.GetSessionEnumerator(out current));
                    int count; AudioNative.Check(current.GetCount(out count));
                    if (count < 0 || count > 1024) throw new InvalidOperationException("Too many audio sessions");
                    PurgeExpired();
                    for (int i = 0; i < count; i++)
                    {
                        AudioNative.IAudioSessionControl control;
                        AudioNative.Check(current.GetSession(i, out control));
                        Add(control);
                    }
                }
                finally { AudioNative.Release(current); }
                IntPtr[] pending = sink.Drain();
                try
                {
                    for (int i = 0; i < pending.Length; i++)
                    {
                        AudioNative.IAudioSessionControl control = null;
                        try { control = (AudioNative.IAudioSessionControl)Marshal.GetObjectForIUnknown(pending[i]); }
                        finally { Marshal.Release(pending[i]); pending[i] = IntPtr.Zero; }
                        Add(control);
                    }
                }
                finally { foreach (IntPtr token in pending) if (token != IntPtr.Zero) Marshal.Release(token); }
                if (sink.TakeFailure()) throw new InvalidOperationException("Audio notification overflow");
                foreach (Session session in sessions.Values)
                {
                    int state; AudioNative.Check(session.Control.GetState(out state));
                    if (state != 1) continue; // inactive/expired are never candidates
                    var extended = (AudioNative.IAudioSessionControl2)session.Control;
                    int system = extended.IsSystemSoundsSession();
                    AudioNative.Check(system);
                    if (system == 0) continue;
                    uint pid; int ownerResult = extended.GetProcessId(out pid); AudioNative.Check(ownerResult);
                    if (ownerResult == 0x0889000D) throw new InvalidOperationException("Shared audio session owner is not unique");
                    sources.Add(pid, ref session.ProcessId, ref session.ProcessCreated);
                }
            }

            private void Add(AudioNative.IAudioSessionControl control)
            {
                bool retained = false;
                AudioStateSink events = null;
                bool registered = false;
                try
                {
                    var extended = (AudioNative.IAudioSessionControl2)control;
                    string id; AudioNative.Check(extended.GetSessionInstanceIdentifier(out id));
                    if (String.IsNullOrEmpty(id)) throw new InvalidOperationException("Missing session identity");
                    if (!sessions.ContainsKey(id))
                    {
                        if (sessions.Count >= 1024) throw new InvalidOperationException("Audio session limit");
                        events = new AudioStateSink(changed);
                        AudioNative.Check(control.RegisterAudioSessionNotification(events)); registered = true;
                        sessions.Add(id, new Session { Control = control, Events = events }); retained = true;
                    }
                }
                finally
                {
                    if (!retained)
                    {
                        if (events != null) events.Stop();
                        if (registered) { try { control.UnregisterAudioSessionNotification(events); } catch { } }
                        AudioNative.Release(control);
                    }
                }
            }

            private void PurgeExpired()
            {
                var expired = new List<string>();
                foreach (var entry in sessions)
                {
                    int state; int hr = entry.Value.Control.GetState(out state);
                    if (hr < 0 || state == 2) expired.Add(entry.Key);
                }
                foreach (string id in expired) { sessions[id].Close(); sessions.Remove(id); }
            }

            public void Dispose()
            {
                if (sink != null) sink.Stop();
                if (manager != null && sink != null) { try { manager.UnregisterSessionNotification(sink); } catch { } }
                foreach (Session session in sessions.Values) session.Close();
                sessions.Clear(); AudioNative.Release(manager); manager = null; sink = null;
            }
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        private sealed class SessionSink : AudioNative.IAudioSessionNotification
        {
            private readonly object gate = new object();
            private readonly Queue<IntPtr> tokens = new Queue<IntPtr>();
            private bool stopped, failed;
            private readonly Action changed;
            internal SessionSink(Action changed) { this.changed = changed; }
            public int OnSessionCreated(AudioNative.IAudioSessionControl control)
            {
                IntPtr token = IntPtr.Zero;
                bool notify = false;
                try
                {
                    token = Marshal.GetIUnknownForObject(control); // explicit ownership across callback lifetime
                    lock (gate)
                    {
                        if (!stopped && tokens.Count < 256) { tokens.Enqueue(token); token = IntPtr.Zero; notify = true; }
                        else if (!stopped) { failed = true; notify = true; }
                    }
                }
                catch { lock (gate) { if (!stopped) failed = true; } }
                finally { if (token != IntPtr.Zero) Marshal.Release(token); }
                try { if (notify && changed != null) changed(); } catch { }
                return 0;
            }
            internal IntPtr[] Drain()
            { lock (gate) { IntPtr[] result = tokens.ToArray(); tokens.Clear(); return result; } }
            internal bool TakeFailure()
            { lock (gate) { bool value = failed; failed = false; return value; } }
            internal void Stop()
            {
                IntPtr[] pending;
                lock (gate) { stopped = true; pending = tokens.ToArray(); tokens.Clear(); }
                foreach (IntPtr token in pending) Marshal.Release(token);
            }
        }
    }

    internal static class AudioNative
    {
        internal static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
        internal static void Release(object value)
        { if (value != null && Marshal.IsComObject(value)) { try { Marshal.ReleaseComObject(value); } catch { } } }

        internal static string ReadProcessPath(uint pid, out long created)
        {
            created = 0;
            if (pid == 0 || pid > Int32.MaxValue) throw new InvalidOperationException("Unidentified audio owner");
            IntPtr process = OpenProcess(0x1000, false, pid);
            if (process == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var path = new StringBuilder(32768); int length = path.Capacity;
                if (!QueryFullProcessImageName(process, 0, path, ref length))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                long exited, kernel, user;
                if (!GetProcessTimes(process, out created, out exited, out kernel, out user))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                string normalized = ExeIdentity.Normalize(path.ToString());
                if (normalized == null) throw new InvalidOperationException("Invalid audio owner path");
                return normalized;
            }
            finally { CloseHandle(process); }
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        internal class MMDeviceEnumerator { }
        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
        }
        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }
        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object value);
            [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out uint state);
        }
        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(ref Guid guid, uint flags, out IAudioSessionControl control);
            [PreserveSig] int GetSimpleAudioVolume(ref Guid guid, uint flags, out IntPtr volume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
            [PreserveSig] int RegisterSessionNotification(IAudioSessionNotification notification);
            [PreserveSig] int UnregisterSessionNotification(IAudioSessionNotification notification);
            [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr notification);
            [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
        }
        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
        }
        [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioSessionControl
        {
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
            [PreserveSig] int GetGroupingParam(out Guid value);
            [PreserveSig] int SetGroupingParam(ref Guid value, ref Guid context);
            [PreserveSig] int RegisterAudioSessionNotification(IAudioSessionEvents notification);
            [PreserveSig] int UnregisterAudioSessionNotification(IAudioSessionEvents notification);
        }
        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioSessionControl2
        {
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
            [PreserveSig] int GetGroupingParam(out Guid value);
            [PreserveSig] int SetGroupingParam(ref Guid value, ref Guid context);
            [PreserveSig] int RegisterAudioSessionNotification(IAudioSessionEvents notification);
            [PreserveSig] int UnregisterAudioSessionNotification(IAudioSessionEvents notification);
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetProcessId(out uint pid);
            [PreserveSig] int IsSystemSoundsSession();
            [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
        }
        [ComVisible(true), Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioSessionNotification
        {
            [PreserveSig] int OnSessionCreated(IAudioSessionControl control);
        }
        [ComVisible(true), Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioSessionEvents
        {
            [PreserveSig] int OnDisplayNameChanged([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr context);
            [PreserveSig] int OnIconPathChanged([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr context);
            [PreserveSig] int OnSimpleVolumeChanged(float volume, [MarshalAs(UnmanagedType.Bool)] bool muted, IntPtr context);
            [PreserveSig] int OnChannelVolumeChanged(uint count, IntPtr volumes, uint changedChannel, IntPtr context);
            [PreserveSig] int OnGroupingParamChanged(IntPtr grouping, IntPtr context);
            [PreserveSig] int OnStateChanged(int state);
            [PreserveSig] int OnSessionDisconnected(int reason);
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class AudioStateSink : AudioNative.IAudioSessionEvents
    {
        private readonly Action changed;
        private int stopped;
        internal AudioStateSink(Action changed) { this.changed = changed; }
        internal void Stop() { Interlocked.Exchange(ref stopped, 1); }
        private int Wake()
        { if (Interlocked.CompareExchange(ref stopped, 0, 0) == 0) { try { if (changed != null) changed(); } catch { } } return 0; }
        public int OnDisplayNameChanged(string name, IntPtr context) { return 0; }
        public int OnIconPathChanged(string path, IntPtr context) { return 0; }
        public int OnSimpleVolumeChanged(float volume, bool muted, IntPtr context) { return 0; }
        public int OnChannelVolumeChanged(uint count, IntPtr volumes, uint channel, IntPtr context) { return 0; }
        public int OnGroupingParamChanged(IntPtr grouping, IntPtr context) { return 0; }
        public int OnStateChanged(int state) { return Wake(); }
        public int OnSessionDisconnected(int reason) { return Wake(); }
    }
}
