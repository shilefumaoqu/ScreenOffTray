using System;
using System.Threading;
using System.Windows.Forms;

namespace ScreenOffTray
{
    internal static class Program
    {
        internal const string MutexName = @"Local\ScreenOffTray";

        internal enum EntryMode { Tray, Setup, SupportCheck, SelfTest, Watchdog, Recover, InstallationCheck, Invalid }
        internal static EntryMode SelectMode(string[] args)
        {
            if (args == null || args.Length == 0) return EntryMode.Tray;
            if (args.Length == 1 && String.Equals(args[0], "--setup", StringComparison.OrdinalIgnoreCase))
                return EntryMode.Setup;
            if ((args.Length == 2 || args.Length == 3) && String.Equals(args[0], "--installation-check", StringComparison.OrdinalIgnoreCase))
                return EntryMode.InstallationCheck;
            if (String.Equals(args[0], "--watchdog", StringComparison.OrdinalIgnoreCase))
                return args.Length == 6 ? EntryMode.Watchdog : EntryMode.Invalid;
            if (String.Equals(args[0], "--recover", StringComparison.OrdinalIgnoreCase))
                return args.Length == 2 ? EntryMode.Recover : EntryMode.Invalid;
            if (args.Length == 1 && String.Equals(args[0], "--check-display-off-support", StringComparison.OrdinalIgnoreCase))
                return EntryMode.SupportCheck;
            if (args.Length == 1 && String.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
                return EntryMode.SelfTest;
            return EntryMode.Invalid;
        }

        [STAThread]
        private static void Main(string[] args)
        {
            EntryMode mode = SelectMode(args);
            if (mode == EntryMode.InstallationCheck)
            {
                Environment.ExitCode = InstallationDiagnostics.Run(args[1], args.Length == 3 ? args[2] : null);
                return;
            }
            // Recovery children must never contend for the tray's singleton mutex or create UI.
            if (mode == EntryMode.Watchdog || mode == EntryMode.Recover)
            {
                try {
                    if (mode == EntryMode.Watchdog) Environment.ExitCode = OledPrototype.Guardian.Run(args);
                    else { OledPrototype.DisplayBackend.Recover(args[1]); OledPrototype.Program.Log("manual_recovery_verified"); }
                } catch (Exception ex) { OledPrototype.Program.Log("recovery_failed: " + ex.Message); Environment.ExitCode = 1; }
                finally {
                    // Recovery children have no UI context. Drain their final diagnostic
                    // for a bounded interval before their background log worker is lost.
                    try { OledPrototype.Program.FlushLogAsync(250).GetAwaiter().GetResult(); } catch { }
                }
                return;
            }
            if (mode == EntryMode.Invalid) { Environment.ExitCode = 2; return; }
            if (mode == EntryMode.SupportCheck)
            {
                bool modernStandby;
                Environment.ExitCode = !NativeMethods.TryGetModernStandbySupport(out modernStandby)
                    ? 3 : (modernStandby ? 2 : 0);
                return;
            }
            if (mode == EntryMode.SelfTest)
            {
                Environment.ExitCode = RunSelfTest();
                return;
            }

            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show(
                        "自动关屏程序已经在运行。",
                        "自动关屏",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApplicationContext(mode == EntryMode.Setup));
            }
        }

        private static int RunSelfTest()
        {
            bool modernStandby;
            if (!NativeMethods.TryGetModernStandbySupport(out modernStandby)) return 21;
            if (TrayApplicationContext.ShouldTrigger(false, false, 599999U, 10, false)) return 1;
            if (!TrayApplicationContext.ShouldTrigger(false, false, 600000U, 10, false)) return 2;
            if (TrayApplicationContext.ShouldTrigger(true, false, 600000U, 10, false)) return 3;
            if (TrayApplicationContext.ShouldTrigger(false, true, 600000U, 10, false)) return 4;
            if (TrayApplicationContext.ShouldTrigger(false, false, 600000U, 10, true)) return 5;
            if (TrayApplicationContext.TickDelta(0x00000010U, 0xFFFFFFF0U) != 0x20U) return 6;
            if (!TrayApplicationContext.ShouldTrigger(false, false, 86400000U, 1440, false)) return 7;
            if (TrayApplicationContext.ShouldTrigger(false, false, 86460000U, 1441, false)) return 8;
            if (TrayApplicationContext.ShouldPrepareKeepAwake(29999U, 1)) return 16;
            if (!TrayApplicationContext.ShouldPrepareKeepAwake(30000U, 1)) return 17;
            if (TrayApplicationContext.ShouldPrepareKeepAwake(269999U, 5)) return 18;
            if (!TrayApplicationContext.ShouldPrepareKeepAwake(270000U, 5)) return 19;
            if (TrayApplicationContext.ShouldPrepareKeepAwake(0U, 1441)) return 20;

            uint idleMilliseconds;
            uint lastInputTick;
            if (!NativeMethods.TryGetLastInput(out idleMilliseconds, out lastInputTick)) return 9;

            uint executionState;
            if (!NativeMethods.TryGetSystemExecutionState(out executionState)) return 10;

            if (!NativeMethods.TryKeepSystemAwake()) return 11;
            if (!NativeMethods.IsPowerRequestActive) return 15;
            NativeMethods.AllowSystemSleep();
            if (!NativeMethods.TryKeepSystemAwake()) return 12;
            NativeMethods.AllowSystemSleep();
            NativeMethods.AllowSystemSleep();
            return 0;
        }
    }
}
