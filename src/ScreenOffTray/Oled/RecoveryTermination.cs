using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace OledPrototype
{
    internal interface IRecoveryParent
    {
        int Id { get; }
        long StartTicks { get; }
        bool HasExited { get; }
        void Kill();
        bool WaitForExit(int milliseconds);
    }

    internal interface IRecoveryStop
    {
        bool Requested { get; }
        long Milliseconds { get; }
        bool Wait(int milliseconds);
    }

    // Contains no Process/display/event APIs. A pinned parent is never replaced by PID lookup.
    internal static class RecoveryTermination
    {
        private enum Observation { Unknown, Alive, Exited, Stopped }

        private static Observation Observe(IRecoveryParent parent, int pid, long ticks, IRecoveryStop stop)
        {
            try {
                if (stop.Requested) return Observation.Stopped;
                int actualPid = parent.Id;
                if (stop.Requested) return Observation.Stopped;
                long actualTicks = parent.StartTicks;
                if (stop.Requested) return Observation.Stopped;
                if (actualPid != pid || actualTicks != ticks) return Observation.Unknown;
                bool exited = parent.HasExited;
                if (stop.Requested) return Observation.Stopped;
                return exited ? Observation.Exited : Observation.Alive;
            } catch { return stop.Requested ? Observation.Stopped : Observation.Unknown; }
        }

        internal static int Run(IRecoveryParent parent, int pid, long ticks, IRecoveryStop stop,
            Func<int> recover, Action<string> log)
        {
            Observation state = Observe(parent, pid, ticks, stop);
            for (int attempt = 0; attempt < 3 && state == Observation.Alive; attempt++) {
                if (stop.Requested) return 0;
                bool failed = false;
                try { parent.Kill(); }
                catch (Exception ex) { failed = true; Log(log, "parent_stop_error: " + ex.Message); }
                // Kill can fail because the exact parent exited concurrently.
                state = Observe(parent, pid, ticks, stop);
                if (state != Observation.Alive) break;
                if (!failed) {
                    long started = stop.Milliseconds;
                    while (state == Observation.Alive && stop.Milliseconds - started < 3000) {
                        if (stop.Requested) return 0;
                        int remaining = (int)Math.Min(100, 3000 - (stop.Milliseconds - started));
                        if (remaining <= 0) break;
                        try { parent.WaitForExit(remaining); }
                        catch (Exception ex) { Log(log, "parent_wait_error: " + ex.Message); failed = true; }
                        state = Observe(parent, pid, ticks, stop);
                        if (failed) break;
                    }
                }
                // Recheck even if WaitForExit timed out or threw; its return value is not proof.
                state = Observe(parent, pid, ticks, stop);
            }
            if (state == Observation.Stopped || stop.Requested) return 0;
            if (state != Observation.Exited) {
                Log(log, "parent_exit_pending; recovery_deferred");
                do {
                    if (stop.Requested || stop.Wait(1000) || stop.Requested) return 0;
                    state = Observe(parent, pid, ticks, stop);
                } while (state != Observation.Exited && state != Observation.Stopped);
            }
            if (state == Observation.Stopped || stop.Requested) return 0;
            return recover();
        }

        internal static void Log(Action<string> log, string message)
        { try { if (log != null) log(message); } catch { } }
    }

    internal interface IGuardianChild : IDisposable
    {
        bool HasExited { get; }
        bool WaitForExit(int milliseconds);
    }

    // A failed constructor still owns a possibly live child; return its exact stop task.
    internal sealed class GuardianStartException : Exception
    {
        internal Task<bool> StopConfirmation { get; private set; }
        private GuardianStartException(Exception cause, Task<bool> stopConfirmation)
            : base(cause.Message, cause) { StopConfirmation = stopConfirmation; }

        internal static GuardianStartException Capture(Exception cause, GuardianLifetime lifetime)
        { return new GuardianStartException(cause, lifetime.StopAsync()); }
    }

    // Exactly one worker owns child exit confirmation and final disposal.
    // Pending/false is NOT permission to archive/delete a recovery snapshot.
    internal sealed class GuardianLifetime
    {
        private readonly object sync = new object();
        private readonly Action signalStop, cleanup;
        private readonly Action<string> log;
        private readonly Action<Action> startStop;
        private IGuardianChild child;
        private Task<bool> stopping;
        private bool heartbeat = true;
        private int cleaned;

        internal GuardianLifetime(Action signalStop, Action cleanup, Action<string> log)
            : this(signalStop, cleanup, log, delegate(Action work) { Task.Run(work); }) { }

        // Test launchers either start work once or throw before starting it, like Task.Run.
        internal GuardianLifetime(Action signalStop, Action cleanup, Action<string> log, Action<Action> startStop)
        { this.signalStop = signalStop; this.cleanup = cleanup; this.log = log; this.startStop = startStop; }

        internal void Attach(IGuardianChild value) { lock (sync) { child = value; } }
        internal void Use(Action action, bool pulseOnly)
        { lock (sync) { if (stopping == null && (!pulseOnly || heartbeat)) action(); } }
        internal void DisableHeartbeat() { lock (sync) { heartbeat = false; } }
        internal bool IsAlive
        {
            get { lock (sync) {
                if (stopping != null || child == null) return false;
                try { return !child.HasExited; } catch { return false; }
            } }
        }

        internal Task<bool> StopAsync()
        {
            lock (sync) {
                if (stopping != null) return stopping;
                heartbeat = false;
                TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();
                stopping = completion.Task;
                // The worker, rather than the UI, signals and waits. Use is excluded immediately.
                Stopwatch budget = Stopwatch.StartNew();
                try { startStop(delegate { completion.TrySetResult(StopWorker(budget)); }); }
                catch (Exception ex) {
                    RecoveryTermination.Log(log, "guardian_stop_start_error; snapshot_must_be_retained: " + ex.Message);
                    // No worker was launched. Send Stop without waiting, release local handles once,
                    // and expose a stable false instead of losing the failure or inventing confirmation.
                    try { signalStop(); }
                    catch (Exception signalError) { RecoveryTermination.Log(log, "guardian_stop_signal_error: " + signalError.Message); }
                    CleanupOnce();
                    completion.TrySetResult(false);
                }
                return stopping;
            }
        }

        internal void Stop()
        {
            try { StopAsync(); }
            catch (Exception ex) { RecoveryTermination.Log(log, "guardian_stop_error: " + ex.Message); }
        }

        private bool StopWorker(Stopwatch budget)
        {
            try {
                try { signalStop(); }
                catch (Exception ex) { RecoveryTermination.Log(log, "guardian_stop_signal_error: " + ex.Message); }
                if (child == null) return true; // Construction failed before any child started.
                bool reported = false;
                for (;;) {
                    try { if (child.HasExited) return true; }
                    catch (Exception ex) {
                        if (!reported) { reported = true; RecoveryTermination.Log(log, "guardian_exit_pending: " + ex.Message); }
                    }
                    int remaining = 3000 - (int)Math.Min(3000, budget.ElapsedMilliseconds);
                    if (remaining <= 0) {
                        RecoveryTermination.Log(log, "guardian_stop_exit_unconfirmed; snapshot_must_be_retained");
                        return false;
                    }
                    int slice = Math.Min(100, remaining);
                    Stopwatch interval = Stopwatch.StartNew();
                    try { child.WaitForExit(slice); }
                    catch (Exception ex) {
                        if (!reported) { reported = true; RecoveryTermination.Log(log, "guardian_exit_pending: " + ex.Message); }
                    }
                    try { if (child.HasExited) return true; } catch { }
                    // Errors/early wait returns must not spin. The same 3s budget includes this pause.
                    int pause = (int)Math.Min(3000 - Math.Min(3000, budget.ElapsedMilliseconds),
                        slice - Math.Min(slice, interval.ElapsedMilliseconds));
                    if (pause > 0) Thread.Sleep(pause);
                }
            } catch (Exception ex) {
                RecoveryTermination.Log(log, "guardian_stop_error; snapshot_must_be_retained: " + ex.Message);
                return false;
            } finally {
                CleanupOnce();
            }
        }

        private void CleanupOnce()
        {
            if (Interlocked.Exchange(ref cleaned, 1) != 0) return;
            // Closing this process's handles does not reset the stop event already opened by child.
            if (child != null) {
                try { child.Dispose(); }
                catch (Exception ex) { RecoveryTermination.Log(log, "guardian_process_cleanup_error: " + ex.Message); }
            }
            try { cleanup(); }
            catch (Exception ex) { RecoveryTermination.Log(log, "guardian_handle_cleanup_error: " + ex.Message); }
        }
    }
}
