using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OledPrototype
{
    internal sealed class RecoveryRetryIntent
    {
        private bool armed = true;
        internal bool Observe(InputKind kind, bool injected, bool enabled)
        {
            if (injected) return false;
            if (kind == InputKind.KeyUp || kind == InputKind.ButtonUp) { armed = true; return false; }
            if (!enabled || !armed || (kind != InputKind.KeyDown && kind != InputKind.ButtonDown)) return false;
            armed = false; return true;
        }
    }

    // One snapshot can be handed to an initial guard and delayed replacements.
    // Register creation before starting it; seal only after no new readers may start.
    internal sealed class RecoveryReaders
    {
        internal sealed class Lease
        {
            private readonly TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();
            private int supplied;
            internal Task<bool> Completion { get { return completion.Task; } }
            internal void ConfirmWith(Task<bool> confirmation)
            {
                if (Interlocked.Exchange(ref supplied, 1) != 0) return;
                if (confirmation == null) { completion.TrySetResult(false); return; }
                confirmation.ContinueWith(delegate(Task<bool> result) {
                    bool success = result.Status == TaskStatus.RanToCompletion && result.Result;
                    if (result.IsFaulted) { Exception observed = result.Exception; }
                    completion.TrySetResult(success);
                }, TaskScheduler.Default);
            }
        }
        private readonly object sync = new object();
        private readonly List<Task<bool>> readers = new List<Task<bool>>();
        private Task<bool> sealedCompletion;
        internal Lease Register()
        {
            lock (sync) {
                if (sealedCompletion != null) throw new InvalidOperationException("Recovery readers already sealed.");
                var lease = new Lease(); readers.Add(lease.Completion); return lease;
            }
        }
        internal Task<bool> Seal()
        {
            lock (sync) {
                if (sealedCompletion == null) sealedCompletion = ConfirmAll(readers.ToArray());
                return sealedCompletion;
            }
        }
        private static async Task<bool> ConfirmAll(Task<bool>[] readers)
        {
            bool[] results = await Task.WhenAll(readers).ConfigureAwait(false);
            foreach (bool result in results) if (!result) return false;
            return true;
        }
    }

    internal sealed class RecoveryStopBarrier
    {
        private int pending, unconfirmed;
        internal int Pending { get { return Volatile.Read(ref pending); } }
        internal bool Unconfirmed { get { return Volatile.Read(ref unconfirmed) != 0; } }
        internal void RetainUnconfirmed() { Interlocked.Exchange(ref unconfirmed, 1); }
        internal Task<bool> Observe(Func<Task<bool>> request, Action<bool> completed, Action<string> log)
        {
            Interlocked.Increment(ref pending);
            Task<bool> stopping;
            try { stopping = request(); }
            catch (Exception ex) { ExitNotifications.Write(log, "recovery_stop_request_failed: " + ex); stopping = Task.FromResult(false); }
            if (stopping == null) stopping = Task.FromResult(false);
            var confirmation = new TaskCompletionSource<bool>();
            stopping.ContinueWith(delegate(Task<bool> result) {
                bool success = result.Status == TaskStatus.RanToCompletion && result.Result;
                if (!success) {
                    Interlocked.Exchange(ref unconfirmed, 1);
                    if (result.IsFaulted) ExitNotifications.Write(log, "recovery_stop_failed: " + result.Exception);
                    ExitNotifications.Write(log, "recovery_stop_unconfirmed; snapshot_preserved; further_display_off_disabled");
                }
                Interlocked.Decrement(ref pending);
                confirmation.TrySetResult(success);
                try { if (completed != null) completed(success); }
                catch (Exception ex) { ExitNotifications.Write(log, "recovery_stop_status_failed: " + ex); }
            }, TaskScheduler.Default);
            return confirmation.Task;
        }
    }

    // The display/input state remains the sole authority for exit safety.
    internal sealed class ExitNotifications
    {
        private bool notifying, delivered;
        private int failures;
        private long retryAt;
        internal bool Failed { get { return failures >= 3; } }
        internal bool ShouldRetry(long now) { return !delivered && !Failed && now >= retryAt; }
        internal void Retry() { failures = 0; retryAt = 0; }
        internal void Dispatch(Action changed, Func<bool> safeToExit, Action exitReady, Action<string> log, long now = 0)
        {
            if (notifying || delivered) return;
            notifying = true;
            try {
                InvokeAll(changed, log, "session_changed_failed");
                if (exitReady == null || !ShouldRetry(now) || !safeToExit()) return;
                delivered = true;
                try { exitReady(); }
                catch (Exception ex) { delivered = false; failures++; retryAt = now + 1000; Write(log, "exit_ready_failed; attempt=" + failures + "; " + ex); }
            } finally { notifying = false; }
        }
        internal static void InvokeAll(Action callback, Action<string> log, string name)
        {
            if (callback == null) return;
            foreach (Action subscriber in callback.GetInvocationList()) {
                try { subscriber(); } catch (Exception ex) { Write(log, name + ": " + ex); }
            }
        }
        internal static void Write(Action<string> log, string value)
        { try { if (log != null) log(value); } catch { } }
    }

    internal sealed class ExitCleanupStep
    {
        internal readonly string Name;
        internal readonly Action Run;
        internal ExitCleanupStep(string name, Action run) { Name = name; Run = run; }
    }

    internal sealed class ExitCleanup
    {
        private readonly HashSet<string> attempted = new HashSet<string>(StringComparer.Ordinal);
        private bool running;
        internal bool Completed { get; private set; }
        internal bool Run(IEnumerable<ExitCleanupStep> steps, Action endMessageLoop, Action<string> log)
        {
            if (running || Completed) return Completed;
            running = true;
            try {
                foreach (ExitCleanupStep step in steps) {
                    if (!attempted.Add(step.Name)) continue;
                    try { step.Run(); }
                    catch (Exception ex) { ExitNotifications.Write(log, "exit_cleanup_failed; step=" + step.Name + "; " + ex); }
                }
                // A failed icon/dialog cleanup must not prevent ending a safe session.
                try { endMessageLoop(); Completed = true; }
                catch (Exception ex) { ExitNotifications.Write(log, "exit_message_loop_failed: " + ex); }
                return Completed;
            } finally { running = false; }
        }
    }

    internal sealed class ExitWaitNotice
    {
        private long? waitingSince;
        private bool shown;
        internal bool Observe(bool exiting, bool draining, int heldCount, long now)
        {
            if (!exiting) { waitingSince = null; shown = false; return false; }
            if (!draining || heldCount == 0) { waitingSince = null; return false; }
            if (!waitingSince.HasValue) waitingSince = now;
            if (shown || now < waitingSince.Value || now - waitingSince.Value < 5000) return false;
            shown = true;
            return true;
        }
        internal static string Describe(IEnumerable<int> held, bool hotkeyHeld, int hotkey)
        {
            var zh = new List<string>(); var en = new List<string>();
            foreach (int id in held) Add(id, zh, en);
            if (hotkeyHeld && !zh.Contains(((Keys)hotkey).ToString())) Add(hotkey, zh, en);
            string chinese = zh.Count == 0 ? "对应键鼠" : String.Join("、", zh.ToArray());
            string english = en.Count == 0 ? "the matching key or mouse button" : String.Join(", ", en.ToArray());
            return "正在等待松开：" + chinese + "。如果已经松开，请重新按下再松开对应的实体键或鼠标按钮，随后会自动退出。" +
                " / Waiting for release: " + english + ". If already released, press and release that physical key or mouse button again; exit will then continue.";
        }
        private static void Add(int id, List<string> zh, List<string> en)
        {
            if (zh.Count >= 8) return;
            string a, b;
            switch (id) {
                case 257: a = "鼠标左键"; b = "left mouse button"; break;
                case 258: a = "鼠标右键"; b = "right mouse button"; break;
                case 259: a = "鼠标中键"; b = "middle mouse button"; break;
                case 260: a = "鼠标侧键1"; b = "mouse side button 1"; break;
                case 261: a = "鼠标侧键2"; b = "mouse side button 2"; break;
                default: a = b = ((Keys)id).ToString(); break;
            }
            zh.Add(a); en.Add(b);
        }
    }
}
