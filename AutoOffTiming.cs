using System;

namespace ScreenOffTray
{
    internal enum AutoOffPauseReason { None, UserPause, DisplayRequired, StateUnknown, Playback, AudioUnknown, PlaybackSettingsUnknown }

    internal struct PlaybackProtectionState
    {
        internal AutoOffPauseReason PauseReason { get; private set; }
        internal bool LimitReached { get; private set; }
        internal string SourceName { get; private set; }
        internal bool Blocked { get { return PauseReason != AutoOffPauseReason.None; } }
        internal PlaybackProtectionState(AutoOffPauseReason reason, bool limitReached, string sourceName) : this()
        { PauseReason = reason; LimitReached = limitReached; SourceName = sourceName; }
    }

    internal struct AutoOffTimingResult
    {
        internal uint IdleMilliseconds { get; private set; }
        internal bool Blocked { get { return PauseReason != AutoOffPauseReason.None; } }
        internal AutoOffPauseReason PauseReason { get; private set; }
        internal bool PlaybackLimitReached { get; private set; }
        internal string SourceName { get; private set; }

        internal AutoOffTimingResult(uint idleMilliseconds, AutoOffPauseReason pauseReason)
            : this()
        {
            IdleMilliseconds = idleMilliseconds;
            PauseReason = pauseReason;
        }

        internal AutoOffTimingResult(uint idleMilliseconds, PlaybackProtectionState state)
            : this(idleMilliseconds, state.PauseReason)
        { PlaybackLimitReached = state.LimitReached; SourceName = state.SourceName; }
    }

    // Caller-serialized, normally on the UI thread. No timers, locks or native calls.
    internal sealed class AutoOffMonotonicClock
    {
        private readonly Func<long> milliseconds;
        private long last;

        internal AutoOffMonotonicClock(Func<long> monotonicMilliseconds)
        {
            if (monotonicMilliseconds == null) throw new ArgumentNullException("monotonicMilliseconds");
            milliseconds = monotonicMilliseconds;
            last = milliseconds();
        }

        internal long Read()
        {
            long now = milliseconds();
            if (now > last) last = now;
            return last;
        }

        internal static uint Elapsed(long now, long epoch)
        {
            if (now <= epoch) return 0;
            // Unsigned subtraction also handles MinValue -> MaxValue without
            // signed overflow. Saturation prevents a long idle from wrapping.
            ulong elapsed = unchecked((ulong)now - (ulong)epoch);
            return elapsed >= uint.MaxValue ? uint.MaxValue : (uint)elapsed;
        }
    }

    internal sealed class PhysicalActivityClock
    {
        private readonly AutoOffMonotonicClock clock;
        private long activityEpoch;

        internal PhysicalActivityClock(Func<long> monotonicMilliseconds)
        {
            clock = new AutoOffMonotonicClock(monotonicMilliseconds);
            activityEpoch = clock.Read();
        }

        // The input adapter supplies the injected/virtual classification.
        // This helper cannot authenticate hardware or classify an input source.
        internal void Observe(bool injected)
        {
            if (!injected) activityEpoch = clock.Read();
        }

        internal uint IdleMilliseconds
        {
            get { return AutoOffMonotonicClock.Elapsed(clock.Read(), activityEpoch); }
        }
    }

    // Automatic countdown only. The caller owns the N-minute threshold and all
    // manual display-off actions; neither is implemented or invoked here.
    internal sealed class AutoOffTiming
    {
        private readonly AutoOffMonotonicClock clock;
        private long countdownEpoch;
        private bool wasBlocked;

        internal AutoOffTiming(Func<long> monotonicMilliseconds)
        {
            clock = new AutoOffMonotonicClock(monotonicMilliseconds);
            countdownEpoch = clock.Read();
        }

        // Call explicitly when changing N or resuming an externally tracked pause.
        internal void ResetCountdown()
        {
            countdownEpoch = clock.Read();
        }

        internal AutoOffTimingResult EvaluatePlayback(uint physicalIdle, PlaybackProtectionState state)
        {
            long now = clock.Read();
            if (state.Blocked) {
                if (!wasBlocked) countdownEpoch = now;
                wasBlocked = true;
                return new AutoOffTimingResult(0, state);
            }
            // A playback cap ends protection at physical H, without granting
            // another full N. Normal pause/playback/unknown release grants N.
            if (wasBlocked && !state.LimitReached) countdownEpoch = now;
            wasBlocked = false;
            uint elapsed = state.LimitReached ? physicalIdle : AutoOffMonotonicClock.Elapsed(now, countdownEpoch);
            return new AutoOffTimingResult(Math.Min(physicalIdle, elapsed), state);
        }

        // displayRequired is already classified by the source adapter. System/
        // execution keep-alive or audio alone must not become displayRequired.
        // Unknown display-request state is protected even if displayRequired=false.
        internal AutoOffTimingResult Evaluate(uint physicalIdle, bool manualPaused,
            bool displayRequired, bool executionStateKnown)
        {
            long now = clock.Read();
            AutoOffPauseReason reason = manualPaused ? AutoOffPauseReason.UserPause
                : !executionStateKnown ? AutoOffPauseReason.StateUnknown
                : displayRequired ? AutoOffPauseReason.DisplayRequired
                : AutoOffPauseReason.None;

            if (reason != AutoOffPauseReason.None)
            {
                wasBlocked = true;
                countdownEpoch = now;
                return new AutoOffTimingResult(0, reason);
            }

            // The first observed unblocked evaluation starts a fresh full N.
            if (wasBlocked)
            {
                countdownEpoch = now;
                wasBlocked = false;
            }
            uint elapsed = AutoOffMonotonicClock.Elapsed(now, countdownEpoch);
            return new AutoOffTimingResult(Math.Min(physicalIdle, elapsed), AutoOffPauseReason.None);
        }
    }
}
