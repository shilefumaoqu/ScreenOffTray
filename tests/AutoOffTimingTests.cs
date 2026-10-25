using System;
using System.Reflection;

namespace ScreenOffTray
{
    internal static class AutoOffTimingTests
    {
        private sealed class FakeTime
        {
            internal long Now;
            internal long Read() { return Now; }
        }

        private static int assertions;
        private static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL " + name);
            ++assertions;
            Console.WriteLine("PASS " + name);
        }

        private static void Result(AutoOffTimingResult actual, uint idle,
            AutoOffPauseReason reason, string name)
        {
            Check(actual.IdleMilliseconds == idle && actual.PauseReason == reason &&
                actual.Blocked == (reason != AutoOffPauseReason.None), name);
        }

        private static void PhysicalInput()
        {
            var time = new FakeTime { Now = 12345 };
            var input = new PhysicalActivityClock(time.Read);
            Check(input.IdleMilliseconds == 0, "physical epoch starts at construction");
            time.Now += 300000;
            input.Observe(true);
            Check(input.IdleMilliseconds == 300000, "injected input does not reset physical idle");
            for (int i = 1; i <= 5; ++i) { time.Now += 1000; input.Observe(true); }
            Check(input.IdleMilliseconds == 305000, "repeated virtual/injected events cannot keep countdown alive");
            input.Observe(false);
            Check(input.IdleMilliseconds == 0, "physical keyboard or mouse resets idle");
            time.Now += 42;
            Check(input.IdleMilliseconds == 42, "physical idle advances from last physical event");
            time.Now -= 30;
            Check(input.IdleMilliseconds == 42, "backward clock cannot decrease physical idle");
            input.Observe(true);
            Check(input.IdleMilliseconds == 42, "injection during backward clock still does not reset");
            input.Observe(false);
            Check(input.IdleMilliseconds == 0, "real input resets at monotonic high-water time");
            time.Now += 31;
            Check(input.IdleMilliseconds == 1, "clock recovery does not double-count a backward interval");
        }

        private static void CountdownAndThreshold()
        {
            var time = new FakeTime { Now = 1000000 };
            var input = new PhysicalActivityClock(time.Read);
            var timing = new AutoOffTiming(time.Read);
            Result(timing.Evaluate(uint.MaxValue, false, false, true), 0,
                AutoOffPauseReason.None, "auto epoch starts at construction despite old physical idle");
            time.Now += 299999;
            var result = timing.Evaluate(input.IdleMilliseconds, false, false, true);
            Check(!result.Blocked && result.IdleMilliseconds < 5u * 60000,
                "five-minute automatic threshold is not reached early");
            time.Now += 1;
            input.Observe(true);
            result = timing.Evaluate(input.IdleMilliseconds, false, false, true);
            Check(!result.Blocked && result.IdleMilliseconds == 5u * 60000,
                "five-minute threshold reached despite injected input");
            input.Observe(false);
            Result(timing.Evaluate(input.IdleMilliseconds, false, false, true), 0,
                AutoOffPauseReason.None, "physical activity resets effective automatic idle");
            time.Now += 1700;
            Result(timing.Evaluate(input.IdleMilliseconds, false, false, true), 1700,
                AutoOffPauseReason.None, "effective automatic idle follows newer physical event");
            time.Now -= 900;
            Result(timing.Evaluate(1700, false, false, true), 1700,
                AutoOffPauseReason.None, "automatic monotonic clock ignores backward observations");
            time.Now += 900;
            timing.ResetCountdown();
            Result(timing.Evaluate(uint.MaxValue, false, false, true), 0,
                AutoOffPauseReason.None, "explicit reset discards accumulated idle");
            uint newThreshold = 2u * 60000;
            time.Now += newThreshold - 1;
            Check(timing.Evaluate(uint.MaxValue, false, false, true).IdleMilliseconds < newThreshold,
                "changed minute setting requires a fresh full interval after reset");
            time.Now += 1;
            Check(timing.Evaluate(uint.MaxValue, false, false, true).IdleMilliseconds == newThreshold,
                "changed minute setting reaches its new threshold exactly");
            Result(timing.Evaluate(9, false, false, true), 9,
                AutoOffPauseReason.None, "physical idle caps longer countdown idle");
        }

        private static void Protection()
        {
            for (int mask = 0; mask < 8; ++mask)
            {
                bool paused = (mask & 1) != 0;
                bool display = (mask & 2) != 0;
                bool known = (mask & 4) != 0;
                var time = new FakeTime();
                var timing = new AutoOffTiming(time.Read);
                time.Now = 600000;
                AutoOffPauseReason expected = paused ? AutoOffPauseReason.UserPause
                    : !known ? AutoOffPauseReason.StateUnknown
                    : display ? AutoOffPauseReason.DisplayRequired : AutoOffPauseReason.None;
                Result(timing.Evaluate(uint.MaxValue, paused, display, known),
                    expected == AutoOffPauseReason.None ? 600000u : 0u, expected,
                    "all pause/display/query-known combinations mask=" + mask);
                if (expected == AutoOffPauseReason.None) continue;
                time.Now += 900000;
                Result(timing.Evaluate(uint.MaxValue, paused, display, known), 0, expected,
                    "protection never accumulates automatic idle mask=" + mask);
                time.Now += 10000;
                Result(timing.Evaluate(uint.MaxValue, false, false, true), 0,
                    AutoOffPauseReason.None, "first recovery evaluation starts fresh mask=" + mask);
                time.Now += 59999;
                Result(timing.Evaluate(uint.MaxValue, false, false, true), 59999,
                    AutoOffPauseReason.None, "recovery waits the full configured minute mask=" + mask);
                time.Now += 1;
                Result(timing.Evaluate(uint.MaxValue, false, false, true), 60000,
                    AutoOffPauseReason.None, "full minute after recovery mask=" + mask);
            }

            var clock = new FakeTime();
            var flow = new AutoOffTiming(clock.Read);
            flow.Evaluate(uint.MaxValue, false, true, true);
            clock.Now = 90000;
            Result(flow.Evaluate(uint.MaxValue, true, false, true), 0,
                AutoOffPauseReason.UserPause, "switching protection reasons never resumes countdown");
            flow.ResetCountdown();
            clock.Now += 90000;
            Result(flow.Evaluate(uint.MaxValue, false, false, false), 0,
                AutoOffPauseReason.StateUnknown, "reset while blocked cannot bypass failed state query");
            clock.Now += 90000;
            Result(flow.Evaluate(uint.MaxValue, false, false, true), 0,
                AutoOffPauseReason.None, "chained protections resume only after every blocker clears");
            clock.Now += 700;
            Result(flow.Evaluate(uint.MaxValue, false, false, true), 700,
                AutoOffPauseReason.None, "system/execution keep-alive and audio alone supplied as display=false do not block");
        }

        private static void LargeAndExtremeTime()
        {
            var time = new FakeTime();
            var input = new PhysicalActivityClock(time.Read);
            var timing = new AutoOffTiming(time.Read);
            time.Now = (long)uint.MaxValue - 1;
            Check(input.IdleMilliseconds == uint.MaxValue - 1, "physical idle just below uint limit");
            Result(timing.Evaluate(uint.MaxValue, false, false, true), uint.MaxValue - 1,
                AutoOffPauseReason.None, "automatic idle just below uint limit");
            time.Now += 1;
            Check(input.IdleMilliseconds == uint.MaxValue, "physical idle at uint limit");
            time.Now += 1;
            Check(input.IdleMilliseconds == uint.MaxValue, "physical idle saturates instead of wrapping");
            Result(timing.Evaluate(uint.MaxValue, false, false, true), uint.MaxValue,
                AutoOffPauseReason.None, "automatic idle saturates instead of wrapping");
            input.Observe(true);
            Check(input.IdleMilliseconds == uint.MaxValue, "injection cannot escape saturated idle");
            input.Observe(false);
            timing.ResetCountdown();
            time.Now += 7;
            Check(input.IdleMilliseconds == 7, "physical reset after saturation is precise");
            Result(timing.Evaluate(uint.MaxValue, false, false, true), 7,
                AutoOffPauseReason.None, "explicit reset after saturation is precise");

            time.Now = long.MinValue;
            input = new PhysicalActivityClock(time.Read);
            timing = new AutoOffTiming(time.Read);
            time.Now = long.MinValue + 19;
            Check(input.IdleMilliseconds == 19, "negative monotonic epoch supported");
            Result(timing.Evaluate(uint.MaxValue, false, false, true), 19,
                AutoOffPauseReason.None, "automatic negative epoch supported");
            time.Now = long.MaxValue;
            Check(input.IdleMilliseconds == uint.MaxValue, "full signed-long span cannot overflow physical idle");
            Result(timing.Evaluate(uint.MaxValue, false, false, true), uint.MaxValue,
                AutoOffPauseReason.None, "full signed-long span cannot overflow automatic idle");
            time.Now = long.MinValue;
            Check(input.IdleMilliseconds == uint.MaxValue, "extreme regression retains saturated physical idle");
            Result(timing.Evaluate(uint.MaxValue, false, false, true), uint.MaxValue,
                AutoOffPauseReason.None, "extreme regression retains saturated automatic idle");
        }

        private static void ManualSeparationAndContract()
        {
            var time = new FakeTime();
            var timing = new AutoOffTiming(time.Read);
            int manualRequests = 0;
            Action manualRequest = delegate { ++manualRequests; };
            // This is a pure caller harness, not a display operation or product integration.
            for (int mask = 0; mask < 8; ++mask)
            {
                AutoOffTimingResult result = timing.Evaluate(uint.MaxValue,
                    (mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0);
                if (mask != 4) Check(result.Blocked, "automatic branch protected in manual harness mask=" + mask);
                manualRequest();
                Check(manualRequests == mask + 1, "manual caller remains independent of auto result mask=" + mask);
            }
            Check(typeof(AutoOffTiming).GetMethod("Evaluate", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetParameters().Length == 4, "automatic API accepts only idle and classified protection flags");
            AssemblyName[] references = typeof(AutoOffTiming).Assembly.GetReferencedAssemblies();
            Check(references.Length == 1 && references[0].Name == "mscorlib",
                "isolated assembly references only mscorlib; no UI/native/power product modules");
            foreach (Type type in new[] { typeof(AutoOffTiming), typeof(PhysicalActivityClock), typeof(AutoOffMonotonicClock) })
            {
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    Check((method.Attributes & MethodAttributes.PinvokeImpl) == 0,
                        type.Name + "." + method.Name + " has no native import");
            }
            bool inputRejected = false;
            bool timingRejected = false;
            try { new PhysicalActivityClock(null); } catch (ArgumentNullException) { inputRejected = true; }
            try { new AutoOffTiming(null); } catch (ArgumentNullException) { timingRejected = true; }
            Check(inputRejected && timingRejected, "constructors reject missing virtual-time provider");
        }

        private static int Main()
        {
            try
            {
                Console.WriteLine("Virtual-time isolation only; CLR=" + Environment.Version);
                PhysicalInput();
                CountdownAndThreshold();
                Protection();
                LargeAndExtremeTime();
                ManualSeparationAndContract();
                Console.WriteLine("PASS assertions=" + assertions + "; product_started=false; real_display_test=false");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
