using System;
using System.Collections.Generic;

namespace OledPrototype
{
    public enum GatePhase { Awake, Guarding, Armed, Waking }
    public enum InputKind { KeyDown, KeyUp, ButtonDown, ButtonUp, Move, Wheel }

    public sealed class InputDecision
    {
        public bool Block;
        public bool RequestWake;
    }

    // Pure state only. The caller serializes all calls and supplies monotonic
    // milliseconds (including across repeated StartOff calls).
    // This class makes no claim about intercepting power keys or Ctrl+Alt+Del.
    public sealed class InputGate
    {
        private readonly HashSet<int> physicalHeld = new HashSet<int>();
        // Downs already delivered to the application, including the StartOff
        // snapshot. Their corresponding physical ups must always be delivered.
        private readonly HashSet<int> preHeld = new HashSet<int>();
        // Survives display-on, cancellation and repeated StartOff. A preHeld key
        // can also be here if its repeat was blocked; its up still passes.
        private readonly HashSet<int> blockedHeld = new HashSet<int>();
        private GatePhase phase = GatePhase.Awake;
        private long guardStartedAt;

        public GatePhase Phase { get { return phase; } }

        // A swallowed wake key retains its pairing until physical release,
        // even after the screen is on. It cannot become a fresh hotkey repeat.
        public bool CanStartHotkey(int id)
        {
            return phase == GatePhase.Awake && !physicalHeld.Contains(id) && !blockedHeld.Contains(id);
        }

        public void StartOff(long nowMilliseconds, IEnumerable<int> currentlyHeldIds)
        {
            // Validate/enumerate before mutation: a failing snapshot must leave
            // the existing gate intact so the caller can safely call Cancel.
            HashSet<int> snapshot = new HashSet<int>();
            if (currentlyHeldIds != null)
            {
                foreach (int id in currentlyHeldIds)
                {
                    if (!IsKey(id) && !IsButton(id))
                        throw new ArgumentOutOfRangeException("currentlyHeldIds");
                    snapshot.Add(id);
                }
            }

            foreach (int id in snapshot)
            {
                physicalHeld.Add(id);
                // Never promote a previously swallowed down into a delivered
                // down, even if it appears in a subsequent physical snapshot.
                if (!blockedHeld.Contains(id)) preHeld.Add(id);
            }
            guardStartedAt = nowMilliseconds;
            phase = GatePhase.Guarding;
        }

        public InputDecision Handle(InputKind kind, int id, bool injected,
                                    long nowMilliseconds)
        {
            // Injected sequences never consume or create physical pairing state.
            if (injected) return Decision(false, false);

            Validate(kind, id);
            Tick(nowMilliseconds);
            if (kind == InputKind.Move || kind == InputKind.Wheel)
                return Decision(phase != GatePhase.Awake, false);

            bool down = kind == InputKind.KeyDown || kind == InputKind.ButtonDown;
            if (!down)
            {
                bool wasBlocked = blockedHeld.Remove(id);
                bool mustDeliverUp = preHeld.Remove(id);
                physicalHeld.Remove(id);
                return Decision(!mustDeliverUp &&
                                (wasBlocked || phase != GatePhase.Awake), false);
            }

            bool fresh = physicalHeld.Add(id);
            if (phase == GatePhase.Awake && !blockedHeld.Contains(id))
            {
                preHeld.Add(id);
                return Decision(false, false);
            }

            blockedHeld.Add(id);
            bool requestWake = phase == GatePhase.Armed && fresh;
            if (requestWake) phase = GatePhase.Waking;
            return Decision(true, requestWake);
        }

        public void MarkDisplayOn(long nowMilliseconds)
        {
            // No clock-dependent release: only actual physical ups clear debt.
            Cancel();
        }

        public void Cancel()
        {
            // Restore unrelated input immediately but retain swallowed holds.
            // Recovery must keep this instance receiving input through release;
            // discarding it or removing a hook cannot preserve this guarantee.
            phase = GatePhase.Awake;
        }

        public void Tick(long nowMilliseconds)
        {
            // Ordered check avoids arming on a backward timestamp; unsigned
            // subtraction avoids overflow at the endpoints of Int64.
            if (phase == GatePhase.Guarding && nowMilliseconds >= guardStartedAt &&
                unchecked((ulong)nowMilliseconds - (ulong)guardStartedAt) >= 3000UL)
                phase = GatePhase.Armed;
        }

        private static bool IsKey(int id) { return id >= 1 && id <= 255; }
        private static bool IsButton(int id) { return id >= 257 && id <= 261; }

        private static void Validate(InputKind kind, int id)
        {
            switch (kind)
            {
                case InputKind.KeyDown:
                case InputKind.KeyUp:
                    if (!IsKey(id)) throw new ArgumentOutOfRangeException("id");
                    break;
                case InputKind.ButtonDown:
                case InputKind.ButtonUp:
                    if (!IsButton(id)) throw new ArgumentOutOfRangeException("id");
                    break;
                case InputKind.Move:
                case InputKind.Wheel:
                    break;
                default:
                    throw new ArgumentOutOfRangeException("kind");
            }
        }

        private static InputDecision Decision(bool block, bool requestWake)
        {
            return new InputDecision { Block = block, RequestWake = requestWake };
        }
    }
}
