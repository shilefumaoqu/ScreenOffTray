namespace OledPrototype
{
    // UI heartbeat alone cannot detect a native call hung on a worker thread.
    // Applies only during a transition; stable screen-off has no time limit.
    internal sealed class TransitionDeadline
    {
        private bool active;
        private long started;
        internal void Start(long now) { started = now; active = true; }
        internal void Complete() { active = false; }
        internal bool TryExpire(long now)
        {
            if (!active || now < started || unchecked((ulong)now - (ulong)started) < 8000UL) return false;
            active = false;
            return true;
        }
    }
}
