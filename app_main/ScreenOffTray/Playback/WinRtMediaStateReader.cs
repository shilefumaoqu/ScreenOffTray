using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace ScreenOffTray.Playback
{
    internal interface IMediaStateReader : IDisposable { MediaStateSnapshot Read(); }

    internal static class MediaClock
    {
        internal static long Milliseconds() { return (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency)); }
    }

    // Owned by one AudioMonitor lifetime, including native-reader recreation.
    // No blocking wait, helper process, periodic timer or media-control command.
    internal sealed class WinRtMediaStateReader : IMediaStateReader
    {
        private readonly Func<Task<object>> request;
        private readonly Func<object, MediaSessionStatus[]> read;
        private readonly Action changed;
        private readonly Func<long> clock;
        private readonly RustMediaApi native;
        private Task<object> pending;
        private bool requested, failed;
        private int failures, retryRequested;
        private long retryAt;
        private static readonly int[] RetryDelays = { 5000, 30000, 120000 };
        internal string Diagnostic { get { return failures == 0 ? "" : "播放器暂停状态暂不可用，保护按音频判断 / Player pause state is unavailable; protection uses audio activity."; } }
        internal void RequestRetry() { Interlocked.Exchange(ref retryRequested, 1); }
        private int disposed;

        internal WinRtMediaStateReader(Action changed) : this(new RustMediaApi(), changed) { }
        private WinRtMediaStateReader(RustMediaApi api, Action changed)
            : this(api.RequestAsync, api.Read, changed, MediaClock.Milliseconds) { native = api; }

        internal WinRtMediaStateReader(Func<Task<object>> request, Func<object, MediaSessionStatus[]> read,
            Action changed, Func<long> clock)
        {
            this.request = request; this.read = read; this.changed = changed; this.clock = clock;
        }

        public MediaStateSnapshot Read()
        {
            if (Volatile.Read(ref disposed) != 0) return Unknown();
            if (Volatile.Read(ref retryRequested) != 0 && (pending == null || pending.IsCompleted)) {
                Interlocked.Exchange(ref retryRequested, 0);
                if (failures>0) {
                    if (native != null) native.Dispose();
                    failed = false; requested = false; pending = null; retryAt = clock();
                }
            }
            if (failed || (!requested && clock() < retryAt)) return Unknown();
            try
            {
                if (!requested)
                {
                    requested = true;
                    pending = request();
                    if (pending == null) { RequestFailed(); return Unknown(); }
                    pending.ContinueWith(Completed, new WeakReference(this), CancellationToken.None,
                        TaskContinuationOptions.None, TaskScheduler.Default);
                }
                if (!pending.IsCompleted) return Unknown();
                if (pending.IsFaulted || pending.IsCanceled)
                {
                    if (pending.IsFaulted) GC.KeepAlive(pending.Exception); // Observe even when the API is unavailable.
                    RequestFailed(); return Unknown();
                }
                object manager = pending.Result; // Only reached after completion; never waits.
                if (manager == null || (native != null && !native.CompleteRequest())) { RequestFailed(); return Unknown(); }
                // The earliest status may already be old when later sessions finish.
                long observedAt = clock();
                MediaSessionStatus[] sessions = read(manager);
                if (Volatile.Read(ref disposed) != 0 || sessions == null) return Unknown();
                failures=0;
                return new MediaStateSnapshot { Known = true, Sessions = sessions, ObservedMilliseconds = observedAt };
            }
            catch
            {
                if (pending == null) RequestFailed();
                return Unknown(); // Auxiliary failure never becomes an empty audio list.
            }
        }

        private void RequestFailed()
        {
            if (native != null) native.Dispose();
            pending = null; requested = false;
            if (failures < RetryDelays.Length) retryAt = clock() + RetryDelays[failures++];
            else { failures++; failed = true; }
        }

        private MediaStateSnapshot Unknown()
        { return new MediaStateSnapshot { Known = false, Sessions = new MediaSessionStatus[0], ObservedMilliseconds = clock() }; }

        private static void Completed(Task<object> task, object state)
        {
            if (task.IsFaulted) GC.KeepAlive(task.Exception);
            var owner = ((WeakReference)state).Target as WinRtMediaStateReader;
            if (owner == null || Volatile.Read(ref owner.disposed) != 0) return;
            try { if (owner.changed != null) owner.changed(); } catch { }
        }

        // No WinRT/COM callbacks or waits on the caller. The monitor closes on its existing worker.
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0 && native != null) native.Dispose();
        }


#if MEDIA_CANDIDATE_PROBE
        internal static long RustReads, RustPlayingReads, RustPausedReads;
#endif
        private sealed class RustMediaApi : IDisposable
        {
            private const string Dll = "screenoff_media.dll";
            private const DllImportSearchPath Search = DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32;
            private IntPtr reader;
            private ManualResetEvent completed;
            private RegisteredWaitHandle wait;
            private bool ready;

            internal Task<object> RequestAsync()
            {
                completed = new ManualResetEvent(false);
                var result = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = ThreadPool.RegisterWaitForSingleObject(completed, Signal, result, Timeout.Infinite, true);
                Marshal.ThrowExceptionForHR(media_open(completed.SafeWaitHandle, out reader), new IntPtr(-1));
                return result.Task;
            }

            private static void Signal(object state, bool timedOut)
            {
                ((TaskCompletionSource<object>)state).TrySetResult(true);
            }

            internal bool CompleteRequest()
            {
                if (ready) return true;
                ready = media_finish(reader) == 0;
                ReleaseWait();
                return ready;
            }

            internal MediaSessionStatus[] Read(object completion)
            {
                int count = media_sample(reader);
                if (count < 0 || count > 128) throw new InvalidDataException("Media session bounds");
                var result = new MediaSessionStatus[count];
#if MEDIA_CANDIDATE_PROBE
                bool playing = false, paused = false;
#endif
                for (int i = 0; i < count; i++)
                {
                    Row row;
                    if (media_row(reader, (UIntPtr)(uint)i, out row) != 0 || row.Id == IntPtr.Zero ||
                        row.Length < 1 || row.Length > 1024 || row.State < 1 || row.State > 3)
                        throw new InvalidDataException("Invalid native media row");
                    string id = Marshal.PtrToStringUni(row.Id, row.Length);
                    if (String.IsNullOrWhiteSpace(id)) throw new InvalidDataException("Media identity unavailable");
                    var status = (MediaSessionPlayback)row.State;
#if MEDIA_CANDIDATE_PROBE
                    playing |= status == MediaSessionPlayback.Playing;
                    paused |= status == MediaSessionPlayback.Paused;
#endif
                    result[i] = new MediaSessionStatus { SourceAppId = id, State = status };
                }
#if MEDIA_CANDIDATE_PROBE
                Interlocked.Increment(ref RustReads);
                if (playing) Interlocked.Increment(ref RustPlayingReads);
                if (paused) Interlocked.Increment(ref RustPausedReads);
#endif
                return result;
            }

            private void ReleaseWait()
            {
                if (wait != null) { wait.Unregister(null); wait = null; }
                if (completed != null) { completed.Dispose(); completed = null; }
            }

            public void Dispose()
            {
                ReleaseWait();
                if (reader != IntPtr.Zero) { media_close(reader); reader = IntPtr.Zero; }
                ready = false;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct Row { internal IntPtr Id; internal int Length; internal int State; }

            [DllImport(Dll, ExactSpelling = true), DefaultDllImportSearchPaths(Search)]
            private static extern int media_open(SafeWaitHandle completed, out IntPtr reader);
            [DllImport(Dll, ExactSpelling = true), DefaultDllImportSearchPaths(Search)]
            private static extern int media_finish(IntPtr reader);
            [DllImport(Dll, ExactSpelling = true), DefaultDllImportSearchPaths(Search)]
            private static extern int media_sample(IntPtr reader);
            [DllImport(Dll, ExactSpelling = true), DefaultDllImportSearchPaths(Search)]
            private static extern int media_row(IntPtr reader, UIntPtr index, out Row value);
            [DllImport(Dll, ExactSpelling = true), DefaultDllImportSearchPaths(Search)]
            private static extern void media_close(IntPtr reader);
        }
    }

    internal sealed class PauseAwareAudioReader : IAudioReader
    {
        private readonly IAudioReader audio;
        private readonly IMediaStateReader media;
        private readonly Func<IMediaSourceResolver> resolver;
        private readonly Func<long> clock;
        private int disposed;

        internal PauseAwareAudioReader(IAudioReader audio, IMediaStateReader media, Func<IMediaSourceResolver> resolver, Func<long> clock)
        { this.audio = audio; this.media = media; this.resolver = resolver; this.clock = clock; }

        public AudioSnapshot Read()
        {
            AudioSnapshot original = audio.Read();
            if (original == null || !original.Known) return original;
            try
            {
                MediaStateSnapshot state = media.Read();
                AudioSnapshot filtered = MediaPauseFilter.Apply(original, state, resolver(), clock());
                // Process identity verification can consume the remaining evidence lifetime.
                return MediaPauseFilter.IsFresh(state, clock()) ? filtered : original;
            }
            catch { return original; }
        }

        // The monitor owns media across recreation; the wrapper owns only this native reader.
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) audio.Dispose(); }
    }
}
