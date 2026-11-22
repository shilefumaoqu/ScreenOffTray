using System;
using System.Collections.Generic;
using System.Threading;

namespace ScreenOffTray
{
#if !BASELINE
    // Published by the dispatch owner. Readers receive detached legacy view
    // models, so a view cannot mutate either protection evidence or options.
    internal sealed class RuntimeSnapshot
    {
        internal readonly long Version;
        internal readonly string Status;
        internal readonly bool Ready, Checking, Preparing;
        private readonly Playback.AudioSnapshot audio;
        private readonly Playback.PlaybackOptions options;
        private readonly AutoOffTimingResult timing;
        private readonly uint physicalIdle;
        private readonly string warning;
        internal RuntimeSnapshot(long version,string status,bool ready,bool checking,bool preparing,
            Playback.AudioSnapshot audio,Playback.PlaybackOptions options,AutoOffTimingResult timing,uint idle,string warning)
        {
            Version=version;Status=status;Ready=ready;Checking=checking;Preparing=preparing;
            this.audio=CopyAudio(audio);this.options=options.Copy();this.timing=timing;physicalIdle=idle;this.warning=warning;
        }
        internal Playback.PlaybackPanelState PlaybackView()
        {return new Playback.PlaybackPanelState{Audio=CopyAudio(audio),AppliedOptions=options.Copy(),Timing=timing,PhysicalIdleMilliseconds=physicalIdle,SettingsWarning=warning};}
        private static Playback.AudioSnapshot CopyAudio(Playback.AudioSnapshot source)
        {
            if(source==null)return null;
            var sources=new List<Playback.AudioSource>();
            if(source.Sources!=null) foreach(var item in source.Sources) if(item!=null) sources.Add(new Playback.AudioSource{
                ExePath=item.ExePath,DisplayName=item.DisplayName,SessionCount=item.SessionCount,
                ProcessIds=item.ProcessIds==null?null:(int[])item.ProcessIds.Clone()});
            return new Playback.AudioSnapshot{Known=source.Known,Error=source.Error,Warning=source.Warning,UnidentifiedSourceCount=source.UnidentifiedSourceCount,
                SampleUtc=source.SampleUtc,SampleTicks=source.SampleTicks,HasMonotonicSample=source.HasMonotonicSample,Sources=sources.ToArray()};
        }
    }

#endif
    // One owner thread, one outstanding read. Dispose signals only; an unreturned
    // native call retains its own handles until it really returns.
    internal sealed class SingleReadOnlyWorker<T> : IDisposable
    {
        private readonly object sync = new object();
        private readonly AutoResetEvent signal = new AutoResetEvent(false);
        private Thread thread;
        private Action job;
        private bool occupied, stopping;
        internal bool Occupied { get { lock (sync) return occupied; } }
        internal bool TryRead(Func<T> read, Action<T, Exception> completed)
        {
            lock (sync) {
                if (stopping || occupied) return false;
                occupied = true;
                job = delegate {
                    T value = default(T); Exception error = null;
                    try { value = read(); } catch (Exception ex) { error = ex; }
                    lock (sync) occupied = false;
                    completed(value, error);
                };
                if (thread == null) { thread = new Thread(Run) { IsBackground = true, Name = "ScreenOffTray.DisplayQuery" }; thread.Start(); }
                signal.Set(); return true;
            }
        }
        private void Run()
        {
            try {
                for (;;) {
                    signal.WaitOne(); Action next;
                    lock (sync) { if (stopping) return; next = job; job = null; }
                    try { if (next != null) next(); }
                    catch (Exception ex) { OledPrototype.Program.Log("display_query_dispatch_failed: " + ex); }
                    lock (sync) if (stopping) return;
                }
            } finally { signal.Dispose(); }
        }
        public void Dispose()
        {
            lock (sync) {
                if (stopping) return; stopping = true;
                if (thread == null) signal.Dispose(); else signal.Set();
            }
        }
    }

    internal enum OffRequestResult { Accepted, Waiting, Cancelled, TimedOut, Rejected }

    // Caller-serialized. No timer, hook, persistence or display API.
    internal sealed class ManualOffRequest
    {
        private readonly Func<long> now;
        private readonly HashSet<int> gestureHeld = new HashSet<int>();
        private readonly HashSet<int> observedPhysicalHeld = new HashSet<int>();
        internal int[] HeldGesture(){var result=new int[observedPhysicalHeld.Count];observedPhysicalHeld.CopyTo(result);return result;}
        private long started;
        internal bool Pending { get; private set; }
        internal ManualOffRequest(Func<long> clock) { now = clock; }
        internal OffRequestResult Begin(bool ready, bool checking, Func<bool> start, IEnumerable<int> held)
        {
            if (Pending) return OffRequestResult.Waiting;
            if (ready) return start() ? OffRequestResult.Accepted : OffRequestResult.Rejected;
            if (!checking) return OffRequestResult.Rejected;
            gestureHeld.Clear(); if (held != null) foreach (int id in held) gestureHeld.Add(id);
            started = now(); Pending = true; return OffRequestResult.Waiting;
        }
        internal OffRequestResult? Poll(bool ready, bool checking, Func<bool> start)
        {
            if (!Pending) return null;
            if (now() - started >= 3000) { Pending = false; return OffRequestResult.TimedOut; }
            if (checking) return null;
            Pending = false;
            return ready && start() ? OffRequestResult.Accepted : OffRequestResult.Rejected;
        }
        internal bool Observe(OledPrototype.InputKind kind, int id, bool injected)
        {
            if(injected)return false;
            if(kind==OledPrototype.InputKind.KeyDown || kind==OledPrototype.InputKind.ButtonDown)observedPhysicalHeld.Add(id);
            else if(kind==OledPrototype.InputKind.KeyUp || kind==OledPrototype.InputKind.ButtonUp)observedPhysicalHeld.Remove(id);
            if(!Pending)return false;
            if (kind == OledPrototype.InputKind.KeyUp || kind == OledPrototype.InputKind.ButtonUp) { gestureHeld.Remove(id); return false; }
            if ((kind == OledPrototype.InputKind.KeyDown || kind == OledPrototype.InputKind.ButtonDown) && gestureHeld.Contains(id)) return false;
            Pending = false; return true;
        }
        internal void Cancel() { Pending = false; gestureHeld.Clear(); }
    }
}
