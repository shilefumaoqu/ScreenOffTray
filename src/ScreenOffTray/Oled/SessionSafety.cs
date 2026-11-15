using System;
using System.Threading;
using System.Threading.Tasks;

namespace OledPrototype
{
    // Cancellation and the APPLY boundary have one atomic winner. No native work lives here.
    internal sealed class PreparationAttempt
    {
        internal readonly long Id, StartedAt;
        internal readonly bool Automatic;
        private int state; // 0 preparing, 1 cancelled, 2 committed to the display operation
        internal PreparationAttempt(long id, long now, bool automatic)
        { Id = id; StartedAt = now; Automatic = automatic; }
        internal bool Pending { get { return Volatile.Read(ref state) == 0; } }
        internal bool Cancelled { get { return Volatile.Read(ref state) == 1; } }
        internal bool IsCurrent(PreparationAttempt current) { return ReferenceEquals(this, current) && Pending; }
        internal bool Cancel() { return Interlocked.CompareExchange(ref state, 1, 0) == 0; }
        internal bool Commit() { return Interlocked.CompareExchange(ref state, 2, 0) == 0; }
        internal bool Expired(long now) { return Pending && now >= StartedAt && now - StartedAt >= 8000; }
        internal bool ObservePhysicalActivity(InputKind kind, bool injected)
        {
            return Automatic && !injected && kind >= InputKind.KeyDown && kind <= InputKind.Wheel && Cancel();
        }
    }

    internal interface IRecoveryExecutor
    {
        bool IsAlive { get; }
        void Arm();
        void Pulse();
        void DisableHeartbeat();
        void Stop();
        Task<bool> StopAsync();
    }

    internal sealed class GuardianExecutor : IRecoveryExecutor
    {
        private readonly Guardian guardian;
        private int stopped;
        internal GuardianExecutor(Guardian guardian) { this.guardian = guardian; }
        public bool IsAlive { get { return Volatile.Read(ref stopped) == 0 && guardian.IsAlive; } }
        public void Arm() { guardian.Arm(); }
        public void Pulse() { guardian.Pulse(); }
        public void DisableHeartbeat() { guardian.DisableHeartbeat(); }
        public Task<bool> StopAsync() { Interlocked.Exchange(ref stopped, 1); return guardian.StopAsync(); }
        public void Stop() { Interlocked.Exchange(ref stopped, 1); guardian.Stop(); }
    }

    // Live/armed is an interface acceptance boundary, never a claim that restoration succeeded.
    internal sealed class RecoveryHandoff
    {
        private long version, pendingAt;
        private volatile bool pending;
        internal bool Pending { get { return pending; } }
        internal bool HandedOff { get; private set; }
        internal int Failures { get; private set; }
        internal long RetryAt { get; private set; }
        internal long Version { get { return Interlocked.Read(ref version); } }
        internal bool IsCurrent(long token) { return pending && Version == token; }
        internal bool AllowsRetry(long token, bool needsRecovery, bool unavailable)
        { return Version == token && needsRecovery && unavailable && !pending && !HandedOff; }
        internal bool Begin(long now, bool explicitRetry, out long token)
        {
            token = 0;
            if (pending || HandedOff || now < RetryAt || (!explicitRetry && Failures >= 3)) return false;
            if (explicitRetry && Failures >= 3) Failures = 0;
            pendingAt = now; token = Interlocked.Increment(ref version); pending = true;
            return true;
        }
        internal bool PendingExpired(long now)
        { return pending && now >= pendingAt && now - pendingAt >= 8000; }
        internal bool Fail(long token, long now)
        {
            if (!IsCurrent(token)) return false;
            RecordFailure(now);
            return true;
        }
        private void RecordFailure(long now)
        {
            pending = false; HandedOff = false; Failures++;
            RetryAt = now + Math.Min(5000, Failures * 1000);
        }
        internal bool TryTransfer(IRecoveryExecutor executor, bool alreadyArmed)
        {
            if (executor == null || !executor.IsAlive) return false;
            try {
                if (!alreadyArmed) executor.Arm();
                if (!executor.IsAlive) return false;
                executor.DisableHeartbeat();
                if (!executor.IsAlive) return false;
                pending = false; Interlocked.Increment(ref version); HandedOff = true;
                return true;
            } catch { return false; }
        }
        internal void LostExecutor(long now)
        {
            if (!HandedOff) return; // Duplicate/stale loss must not charge another generation.
            Interlocked.Increment(ref version);
            RecordFailure(now);
        }
        internal void Reset()
        {
            pending = false; Interlocked.Increment(ref version); HandedOff = false;
            Failures = 0; RetryAt = 0;
        }
    }

    // A fallback Restore cannot overtake a late native TurnOff/Restore. Never wait on this on the UI.
    internal sealed class SerialDisplayWork
    {
        private readonly object sync = new object();
        private Task tail = Task.FromResult(0);
        internal Task Enqueue(Action action)
        {
            lock (sync) {
                tail = tail.ContinueWith(delegate { action(); }, CancellationToken.None,
                    TaskContinuationOptions.None, TaskScheduler.Default);
                return tail;
            }
        }
    }
}
