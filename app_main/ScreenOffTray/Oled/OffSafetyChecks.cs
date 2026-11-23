using System;
using System.Threading.Tasks;

namespace OledPrototype
{
    internal enum OffOutputReading { NoMatch, PhysicalActive, Unknown }

    // Owned by the session's UI thread. A slow native read never shares the
    // queue used to restore the display and never causes a replacement read.
    internal sealed class OffOutputCheck
    {
        internal const long Interval = 30 * 60 * 1000;
        internal const long QueryLimit = 30 * 1000;
        private readonly Func<Func<bool>, Task<bool>> start;
        private Func<bool> read;
        private Task<bool> pending;
        private long next, expires;
        private bool checkOnArm;

        internal OffOutputCheck() : this(query => Task.Run(query)) { }
        internal OffOutputCheck(Func<Func<bool>, Task<bool>> start) { this.start = start; }
        internal bool Armed { get { return read != null; } }

        internal void Arm(long now, Func<bool> query)
        {
            expires = 0;
            read = query;
            next = checkOnArm ? now : now + Interval;
            checkOnArm = false;
        }

        internal void Disarm()
        {
            read = null;
            expires = 0;
            checkOnArm = false;
        }

        internal void CheckSoon(long now)
        {
            if (!Armed) {
                // A topology change can arrive between native off verification
                // and the UI-thread transition into the verified-off phase.
                checkOnArm = true;
                return;
            }
            // A display change supersedes any result that has not been consumed.
            expires = 0;
            next = Math.Min(next, now);
        }

        internal OffOutputReading? Poll(long now)
        {
            if (pending != null && pending.IsCompleted) {
                Task<bool> done = pending;
                pending = null;
                if (done.IsFaulted) { Exception observed = done.Exception; }
                if (expires != 0) {
                    bool valid = now < expires && done.Status == TaskStatus.RanToCompletion;
                    expires = 0;
                    return !valid ? OffOutputReading.Unknown :
                        done.Result ? OffOutputReading.PhysicalActive : OffOutputReading.NoMatch;
                }
            }
            if (expires != 0 && now >= expires) {
                expires = 0;
                return OffOutputReading.Unknown;
            }
            if (pending == null && Armed && now >= next) {
                next = now + Interval;
                expires = now + QueryLimit;
                pending = start(read);
            }
            return null;
        }
    }

    internal enum DisplayRetryAction { None, CountingDown, Cancelled, Unavailable, Retry }

    internal sealed class UnexpectedDisplayRetry
    {
        internal const long QuietPeriod = 30000;
        private long recoveryLimit;
        private long? retryAt;
        private uint activity;
        internal bool Pending { get; private set; }

        internal void Arm(long now, uint physicalInputVersion)
        {
            activity = physicalInputVersion;
            recoveryLimit = now + QuietPeriod;
            retryAt = null;
            Pending = true;
        }

        internal void Cancel() { Pending = false; }

        internal DisplayRetryAction Poll(long now, uint physicalInputVersion, bool recovered, bool ready, bool checking)
        {
            if (!Pending) return DisplayRetryAction.None;
            if (activity != physicalInputVersion) return Finish(DisplayRetryAction.Cancelled);
            if (!retryAt.HasValue) {
                if (now >= recoveryLimit) return Finish(DisplayRetryAction.Unavailable);
                if (!recovered) return DisplayRetryAction.None;
                retryAt = now + QuietPeriod;
                return DisplayRetryAction.CountingDown;
            }
            if (!recovered || now >= retryAt.Value + 3000) return Finish(DisplayRetryAction.Unavailable);
            if (now < retryAt.Value || (checking && !ready)) return DisplayRetryAction.None;
            return Finish(ready ? DisplayRetryAction.Retry : DisplayRetryAction.Unavailable);
        }

        private DisplayRetryAction Finish(DisplayRetryAction action)
        {
            Cancel();
            return action;
        }
    }
}
