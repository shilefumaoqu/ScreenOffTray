using System;

namespace OledPrototype
{
    internal enum SessionPhase { Awake, Preparing, SwitchingOff, Off, Restoring, Draining }

    // UI-thread state shared by the real session and the hardware-free integration checks.
    internal sealed class SessionFlow
    {
        internal SessionPhase Phase { get; private set; }
        internal bool EndRequested { get; private set; }
        internal bool ExitRequested { get; private set; }
        private long? idleEpoch;
        internal bool Busy { get { return Phase != SessionPhase.Awake; } }
        internal bool NeedsRecovery { get { return Phase == SessionPhase.SwitchingOff || Phase == SessionPhase.Off || Phase == SessionPhase.Restoring; } }
        internal bool ReadyToExit { get { return ExitRequested && !Busy; } }

        internal bool TryBegin(bool ready, bool externalDisplayRequired, bool automatic = true)
        {
            if (!ready || (automatic && externalDisplayRequired) || Busy || ExitRequested) return false;
            EndRequested = false;
            Phase = SessionPhase.Preparing;
            return true;
        }
        internal void RequestEnd(bool exit)
        {
            EndRequested = true;
            ExitRequested |= exit;
        }
        internal bool BeginSwitch()
        {
            if (Phase != SessionPhase.Preparing || EndRequested) return false;
            Phase = SessionPhase.SwitchingOff;
            return true;
        }
        internal void MarkOff()
        {
            if (Phase != SessionPhase.SwitchingOff) throw new InvalidOperationException("Unexpected off completion.");
            Phase = SessionPhase.Off;
        }
        internal bool BeginRestore()
        {
            if (Phase != SessionPhase.Off) return false;
            Phase = SessionPhase.Restoring;
            return true;
        }
        internal bool BeginRecovery()
        {
            if (Phase != SessionPhase.SwitchingOff && Phase != SessionPhase.Off && Phase != SessionPhase.Restoring) return false;
            Phase = SessionPhase.Restoring;
            return true;
        }
        internal void Complete(long now, bool heldInput)
        {
            idleEpoch = now;
            Phase = heldInput ? SessionPhase.Draining : SessionPhase.Awake;
        }
        internal bool Drain(long now, bool heldInput)
        {
            if (Phase != SessionPhase.Draining || heldInput) return false;
            Complete(now, false);
            return true;
        }
        internal uint EffectiveIdle(uint systemIdle, long now)
        {
            if (!idleEpoch.HasValue) return systemIdle;
            long elapsed = Math.Max(0L, now - idleEpoch.Value);
            return (uint)Math.Min((long)systemIdle, Math.Min((long)UInt32.MaxValue, elapsed));
        }
    }
}
