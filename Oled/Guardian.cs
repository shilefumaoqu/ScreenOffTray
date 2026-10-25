using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace OledPrototype
{
    // A separate process restores only after the exact parent has exited.
    internal sealed class Guardian
    {
        private EventWaitHandle stop, pulse, arm, ready;
        private readonly GuardianLifetime lifetime;

        internal Guardian(string recoveryFile, int trialSeconds)
        {
            lifetime = new GuardianLifetime(delegate { if (stop != null) stop.Set(); }, Cleanup, Program.Log);
            try {
                string name = @"Local\OLEDPrototype." + Guid.NewGuid().ToString("N");
                stop = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".stop");
                pulse = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".pulse");
                arm = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".arm");
                ready = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".ready");
                using (Process me = Process.GetCurrentProcess()) {
                    Process child = Process.Start(new ProcessStartInfo {
                        FileName = me.MainModule.FileName,
                        Arguments = "--watchdog " + me.Id + " " + me.StartTime.ToUniversalTime().Ticks + " " +
                            Quote(recoveryFile) + " " + Quote(name) + " " + trialSeconds,
                        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
                    });
                    if (child == null) throw new InvalidOperationException("恢复进程未启动，取消关屏。");
                    lifetime.Attach(new GuardianChild(child));
                }
                if (!ready.WaitOne(5000)) throw new InvalidOperationException("恢复进程未就绪，取消关屏。");
            } catch (Exception ex) { throw GuardianStartException.Capture(ex, lifetime); }
        }

        internal void Pulse() { lifetime.Use(delegate { pulse.Set(); }, true); }
        internal bool IsAlive { get { return lifetime.IsAlive; } }
        internal void Arm() { lifetime.Use(delegate { pulse.Set(); arm.Set(); }, false); }
        internal void DisableHeartbeat() { lifetime.DisableHeartbeat(); }
        // All callers receive the same task. True confirms child exit (or no child was started).
        // After a 3s confirmation budget, false retains the snapshot; local handles are disposed.
        // Cleanup is attempted once on either result; no infinite stop observer is left behind.
        internal Task<bool> StopAsync() { return lifetime.StopAsync(); }
        internal void Stop() { lifetime.Stop(); }
        private void Cleanup()
        {
            DisposeHandle(stop); DisposeHandle(pulse); DisposeHandle(arm); DisposeHandle(ready);
        }
        private static void DisposeHandle(EventWaitHandle handle)
        {
            if (handle == null) return;
            try { handle.Dispose(); }
            catch (Exception ex) { RecoveryTermination.Log(Program.Log, "guardian_handle_cleanup_error: " + ex.Message); }
        }
        private static string Quote(string s) { return "\"" + s + "\""; }

        private sealed class GuardianChild : IGuardianChild
        {
            private readonly Process child;
            internal GuardianChild(Process child) { this.child = child; }
            public bool HasExited { get { return child.HasExited; } }
            public bool WaitForExit(int milliseconds) { return child.WaitForExit(milliseconds); }
            public void Dispose() { child.Dispose(); }
        }

        private sealed class RecoveryParent : IRecoveryParent
        {
            private readonly Process parent;
            private readonly int pid;
            private readonly long ticks;
            internal RecoveryParent(Process parent)
            {
                this.parent = parent;
                // Pin this process object's OS handle before recording identity. Never reopen by PID.
                IntPtr pinned = parent.Handle;
                pid = parent.Id;
                ticks = parent.StartTime.ToUniversalTime().Ticks;
                if (pinned == IntPtr.Zero) throw new InvalidOperationException("Parent handle unavailable.");
            }
            public int Id { get { return pid; } }
            public long StartTicks { get { return ticks; } }
            public bool HasExited { get { return parent.HasExited; } }
            public void Kill() { parent.Kill(); }
            public bool WaitForExit(int milliseconds) { return parent.WaitForExit(milliseconds); }
        }

        private sealed class RecoveryStop : IRecoveryStop
        {
            private readonly EventWaitHandle stop;
            private readonly Stopwatch clock = Stopwatch.StartNew();
            internal RecoveryStop(EventWaitHandle stop) { this.stop = stop; }
            public bool Requested { get { return stop.WaitOne(0); } }
            public long Milliseconds { get { return clock.ElapsedMilliseconds; } }
            public bool Wait(int milliseconds) { return stop.WaitOne(milliseconds); }
        }

        internal static int Run(string[] args)
        {
            if (args.Length != 6) return 2;
            int pid = Int32.Parse(args[1]), seconds = Int32.Parse(args[5]);
            long startTicks = Int64.Parse(args[2]);
            string snapshot = args[3], name = args[4];
            using (EventWaitHandle stop = EventWaitHandle.OpenExisting(name + ".stop"))
            using (EventWaitHandle pulse = EventWaitHandle.OpenExisting(name + ".pulse"))
            using (EventWaitHandle arm = EventWaitHandle.OpenExisting(name + ".arm"))
            using (EventWaitHandle ready = EventWaitHandle.OpenExisting(name + ".ready"))
            {
                Process parent = null;
                try {
                    if (stop.WaitOne(0)) return 0;
                    RecoveryParent exact;
                    try {
                        if (stop.WaitOne(0)) return 0;
                        parent = Process.GetProcessById(pid);
                        if (stop.WaitOne(0)) return 0;
                        exact = new RecoveryParent(parent);
                        if (exact.Id != pid || exact.StartTicks != startTicks) return 3;
                    } catch { return 3; }
                    RecoveryStop cancellation = new RecoveryStop(stop);
                    Stopwatch elapsed = Stopwatch.StartNew(); long lastPulse = 0, armedAt = -1;
                    if (cancellation.Requested) return 0;
                    ready.Set();
                    while (true) {
                        if (cancellation.Requested) return 0;
                        int signaled = WaitHandle.WaitAny(new WaitHandle[] { stop, pulse }, 200);
                        if (signaled == 0 || cancellation.Requested) return 0;
                        if (signaled == 1) lastPulse = elapsed.ElapsedMilliseconds;
                        if (armedAt < 0 && arm.WaitOne(0)) { armedAt = elapsed.ElapsedMilliseconds; lastPulse = armedAt; }
                        bool exited = false;
                        if (cancellation.Requested) return 0;
                        try { exited = exact.HasExited; } catch { }
                        if (armedAt < 0) { if (exited) return 0; continue; }
                        bool trialExpired = seconds > 0 && elapsed.ElapsedMilliseconds - armedAt >= seconds * 1000L;
                        bool hung = elapsed.ElapsedMilliseconds - lastPulse >= 8000;
                        if (!trialExpired && !hung && !exited) continue;
                        return RecoveryTermination.Run(exact, pid, startTicks, cancellation, delegate {
                            if (cancellation.Requested) return 0;
                            try {
                                DisplayBackend.Recover(snapshot);
                                Program.Log(trialExpired ? "trial_timeout_recovery_verified" : "watchdog_recovery_verified");
                                return 0;
                            } catch (Exception ex) {
                                if (cancellation.Requested) return 0;
                                Program.Log("snapshot_recovery_failed: " + ex.Message);
                                if (cancellation.Requested) return 0;
                                int code = Native.SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, 0x8f);
                                Program.Log("emergency_persisted_topology_return=" + code + "; exact_layout_not_verified");
                                return 1;
                            }
                        }, Program.Log);
                    }
                } finally { if (parent != null) parent.Dispose(); }
            }
        }
    }
}
