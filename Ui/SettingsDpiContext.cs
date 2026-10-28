using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace ScreenOffTray.Ui
{
    // Window-local DPI only. Never changes the process default or any display setting.
    // Keep scopes synchronous; the coordinator supplies its captured host context for
    // callbacks that leave the view. Nested scopes restore their immediate caller.
    internal sealed class SettingsDpiContext : IDisposable
    {
        private static readonly IntPtr PerMonitorV2 = new IntPtr(-4);
        private readonly int thread = Thread.CurrentThread.ManagedThreadId;
        private IntPtr previous;

        private SettingsDpiContext(IntPtr context)
        {
            if (context == IntPtr.Zero) throw new ArgumentException("A valid DPI context is required.", "context");
            previous = SetThreadDpiAwarenessContext(context);
            if (previous == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot enter the settings DPI context.");
        }

        internal static IntPtr CaptureCurrent()
        {
            IntPtr context = GetThreadDpiAwarenessContext();
            if (context == IntPtr.Zero) throw new InvalidOperationException("Cannot capture the current DPI context.");
            return context;
        }

        internal static SettingsDpiContext EnterUi() { return new SettingsDpiContext(PerMonitorV2); }
        internal static SettingsDpiContext EnterHost(IntPtr context) { return new SettingsDpiContext(context); }

        public void Dispose()
        {
            if (previous == IntPtr.Zero) return;
            if (thread != Thread.CurrentThread.ManagedThreadId) throw new InvalidOperationException("A DPI scope must end on its originating thread.");
            if (SetThreadDpiAwarenessContext(previous) == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot restore the calling DPI context.");
            previous = IntPtr.Zero;
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    }
}
