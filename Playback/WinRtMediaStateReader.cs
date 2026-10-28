using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

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
        private Task<object> pending;
        private bool requested, failed;
        private int disposed;

        internal WinRtMediaStateReader(Action changed) : this(new ReflectionMediaApi(), changed) { }
        private WinRtMediaStateReader(ReflectionMediaApi api, Action changed)
            : this(api.RequestAsync, api.Read, changed, MediaClock.Milliseconds) { }

        internal WinRtMediaStateReader(Func<Task<object>> request, Func<object, MediaSessionStatus[]> read,
            Action changed, Func<long> clock)
        {
            this.request = request; this.read = read; this.changed = changed; this.clock = clock;
        }

        public MediaStateSnapshot Read()
        {
            if (Volatile.Read(ref disposed) != 0 || failed) return Unknown();
            try
            {
                if (!requested)
                {
                    requested = true;
                    pending = request();
                    if (pending == null) { failed = true; return Unknown(); }
                    pending.ContinueWith(Completed, new WeakReference(this), CancellationToken.None,
                        TaskContinuationOptions.None, TaskScheduler.Default);
                }
                if (!pending.IsCompleted) return Unknown();
                if (pending.IsFaulted || pending.IsCanceled)
                {
                    if (pending.IsFaulted) GC.KeepAlive(pending.Exception); // Observe even when the API is unavailable.
                    failed = true; return Unknown();
                }
                object manager = pending.Result; // Only reached after completion; never waits.
                if (manager == null) return Unknown();
                // The earliest status may already be old when later sessions finish.
                long observedAt = clock();
                MediaSessionStatus[] sessions = read(manager);
                if (Volatile.Read(ref disposed) != 0 || sessions == null) return Unknown();
                return new MediaStateSnapshot { Known = true, Sessions = sessions, ObservedMilliseconds = observedAt };
            }
            catch
            {
                if (requested && pending == null) failed = true;
                return Unknown(); // Auxiliary failure never becomes an empty audio list.
            }
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
        public void Dispose() { Interlocked.Exchange(ref disposed, 1); }

        private sealed class ReflectionMediaApi
        {
            private Type managerType, sessionType, infoType;
            private MethodInfo adapter;

            internal Task<object> RequestAsync()
            {
                managerType = Type.GetType("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType=WindowsRuntime", true);
                sessionType = Type.GetType("Windows.Media.Control.GlobalSystemMediaTransportControlsSession, Windows.Media.Control, ContentType=WindowsRuntime", true);
                infoType = Type.GetType("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackInfo, Windows.Media.Control, ContentType=WindowsRuntime", true);
                // System-owned Framework library; no SDK/runtime files are shipped with the prototype.
                Assembly extensions = Assembly.LoadFrom(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "System.Runtime.WindowsRuntime.dll"));
                foreach (MethodInfo method in extensions.GetType("System.WindowsRuntimeSystemExtensions", true).GetMethods())
                    if (method.Name == "AsTask" && method.IsGenericMethodDefinition && method.GetParameters().Length == 1 &&
                        method.GetParameters()[0].ParameterType.Name == "IAsyncOperation`1") adapter = method;
                if (adapter == null) throw new NotSupportedException("WinRT task projection unavailable");
                object operation = managerType.GetMethod("RequestAsync").Invoke(null, null);
                Task native = (Task)adapter.MakeGenericMethod(managerType).Invoke(null, new[] { operation });
                // Project an unknown runtime generic type without adding WinMD build dependencies.
                return native.ContinueWith(delegate(Task completed)
                {
                    if (completed.IsCanceled) throw new OperationCanceledException();
                    if (completed.IsFaulted) throw completed.Exception;
                    return completed.GetType().GetProperty("Result").GetValue(completed, null);
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }

            internal MediaSessionStatus[] Read(object manager)
            {
                var sessions = (IEnumerable)managerType.GetMethod("GetSessions").Invoke(manager, null);
                var result = new List<MediaSessionStatus>();
                foreach (object session in sessions)
                {
                    if (session == null || result.Count >= 128) throw new InvalidDataException("Media session bounds");
                    string id = (string)sessionType.GetProperty("SourceAppUserModelId").GetValue(session, null);
                    if (String.IsNullOrWhiteSpace(id) || id.Length > 1024) throw new InvalidDataException("Media identity unavailable");
                    object info = sessionType.GetMethod("GetPlaybackInfo").Invoke(session, null);
                    string status = Convert.ToString(infoType.GetProperty("PlaybackStatus").GetValue(info, null));
                    result.Add(new MediaSessionStatus { SourceAppId = id,
                        State = status == "Paused" ? MediaSessionPlayback.Paused : status == "Playing" ? MediaSessionPlayback.Playing : MediaSessionPlayback.Other });
                }
                return result.ToArray();
            }
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
