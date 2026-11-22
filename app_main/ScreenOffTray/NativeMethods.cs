using System;
using System.Runtime.InteropServices;

namespace ScreenOffTray
{
    internal static class NativeMethods
    {
        internal const uint WM_SYSCOMMAND = 0x0112U;
        internal const int SC_MONITORPOWER = 0xF170;
        internal const int SystemExecutionState = 16;
        internal const uint PowerRequestSystemRequired = 1U; // 0 is DisplayRequired; never use it for screen-off.
        internal const uint PowerRequestExecutionRequired = 3U;
        internal const uint PowerRequestContextVersion = 0U;
        internal const uint PowerRequestContextSimpleString = 1U;
        internal const uint ES_SYSTEM_REQUIRED = 0x00000001U;
        internal const uint ES_DISPLAY_REQUIRED = 0x00000002U;
        internal const uint ES_CONTINUOUS = 0x80000000U;
        internal const uint SendMessageTimeoutAbortIfHung = 0x0002U;

        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);
        private static IntPtr _powerRequestHandle = IntPtr.Zero;
        private static bool _systemPowerRequestSet;
        private static bool _executionPowerRequestSet;

        // SYSTEM_POWER_CAPABILITIES is 76 bytes on both x86 and x64.
        // AoAc is a native BOOLEAN at offset 20 (not a four-byte Win32 BOOL).
        [StructLayout(LayoutKind.Explicit, Size = 76)]
        private struct SYSTEM_POWER_CAPABILITIES
        {
            [FieldOffset(20)]
            internal byte AoAc;
        }

        [DllImport("powrprof.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool GetPwrCapabilities(out SYSTEM_POWER_CAPABILITIES capabilities);

        internal static bool TryGetModernStandbySupport(out bool supported)
        {
            SYSTEM_POWER_CAPABILITIES capabilities;
            supported = false;
            if (!GetPwrCapabilities(out capabilities)) return false;
            supported = capabilities.AoAc != 0;
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct LASTINPUTINFO
        {
            internal uint cbSize;
            internal uint dwTime;
        }

        // This is the full native layout. The first pointer in the union is the
        // simple reason string used below; the remaining fields keep x86/x64 size
        // and alignment compatible with REASON_CONTEXT.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct REASON_CONTEXT
        {
            internal uint Version;
            internal uint Flags;
            internal IntPtr LocalizedReasonModule;
            internal uint LocalizedReasonId;
            internal uint ReasonStringCount;
            internal IntPtr ReasonStrings;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            uint msg,
            IntPtr wParam,
            IntPtr lParam,
            uint flags,
            uint timeoutMilliseconds,
            out UIntPtr result);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr PowerCreateRequest(ref REASON_CONTEXT context);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PowerSetRequest(IntPtr powerRequest, uint requestType);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PowerClearRequest(IntPtr powerRequest, uint requestType);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("powrprof.dll")]
        private static extern uint CallNtPowerInformation(
            int informationLevel,
            IntPtr inputBuffer,
            uint inputBufferLength,
            out uint outputBuffer,
            uint outputBufferLength);

        internal static bool TryGetLastInput(out uint idleMilliseconds, out uint lastInputTick)
        {
            var info = new LASTINPUTINFO();
            info.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            if (!GetLastInputInfo(ref info))
            {
                idleMilliseconds = 0U;
                lastInputTick = 0U;
                return false;
            }

            lastInputTick = info.dwTime;
            idleMilliseconds = unchecked((uint)Environment.TickCount - info.dwTime);
            return true;
        }

        internal static bool TryGetSystemExecutionState(out uint state)
        {
            state = 0U;
            return CallNtPowerInformation(
                SystemExecutionState,
                IntPtr.Zero,
                0U,
                out state,
                sizeof(uint)) == 0U;
        }

        internal static bool TryKeepSystemAwake()
        {
            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);

            if (_powerRequestHandle == IntPtr.Zero)
            {
                TryCreatePowerRequest();
            }

            if (_systemPowerRequestSet)
            {
                return true;
            }

            // A system request must be accepted. This does not make
            // SC_MONITORPOWER safe on Modern Standby systems; see the guard below.
            SetThreadExecutionState(ES_CONTINUOUS);
            return false;
        }

        internal static bool IsPowerRequestActive
        {
            get { return _systemPowerRequestSet; }
        }

        internal static void AllowSystemSleep()
        {
            if (_powerRequestHandle != IntPtr.Zero && _powerRequestHandle != InvalidHandleValue)
            {
                if (_systemPowerRequestSet)
                {
                    PowerClearRequest(_powerRequestHandle, PowerRequestSystemRequired);
                }

                if (_executionPowerRequestSet)
                {
                    PowerClearRequest(_powerRequestHandle, PowerRequestExecutionRequired);
                }

                CloseHandle(_powerRequestHandle);
            }

            _powerRequestHandle = IntPtr.Zero;
            _systemPowerRequestSet = false;
            _executionPowerRequestSet = false;
            SetThreadExecutionState(ES_CONTINUOUS);
        }

        // ponytail: send only to our own window; broadcasting lets unrelated media windows process it too.
        internal static bool TryTurnOffDisplays(IntPtr targetWindow)
        {
            // On S0 machines this command can enter Modern Standby even when
            // SystemRequired/ExecutionRequired requests were accepted.
            bool modernStandby;
            if (!TryGetModernStandbySupport(out modernStandby) || modernStandby)
            {
                return false;
            }

            if (targetWindow == IntPtr.Zero)
            {
                return false;
            }

            UIntPtr result;
            return SendMessageTimeout(
                targetWindow,
                WM_SYSCOMMAND,
                new IntPtr(SC_MONITORPOWER),
                new IntPtr(2),
                SendMessageTimeoutAbortIfHung,
                1000U,
                out result) != IntPtr.Zero;
        }

        private static void TryCreatePowerRequest()
        {
            IntPtr reason = IntPtr.Zero;
            IntPtr handle = IntPtr.Zero;

            try
            {
                reason = Marshal.StringToHGlobalUni(
                    "ScreenOffTray keeps audio and background work running while displays are off.");
                var context = new REASON_CONTEXT();
                context.Version = PowerRequestContextVersion;
                context.Flags = PowerRequestContextSimpleString;
                context.LocalizedReasonModule = reason;

                handle = PowerCreateRequest(ref context);
                if (handle == IntPtr.Zero || handle == InvalidHandleValue)
                {
                    return;
                }

                if (!PowerSetRequest(handle, PowerRequestSystemRequired))
                {
                    CloseHandle(handle);
                    return;
                }

                _powerRequestHandle = handle;
                _systemPowerRequestSet = true;
                // ExecutionRequired is supported on Windows 8+; system request
                // remains sufficient on older systems when this optional flag fails.
                _executionPowerRequestSet = PowerSetRequest(
                    handle,
                    PowerRequestExecutionRequired);
            }
            catch (DllNotFoundException)
            {
                // Fail closed if the system-level request API is unavailable.
            }
            catch (EntryPointNotFoundException)
            {
                // Fail closed if the system-level request API is unavailable.
            }
            finally
            {
                if (reason != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(reason);
                }
            }
        }
    }
}
