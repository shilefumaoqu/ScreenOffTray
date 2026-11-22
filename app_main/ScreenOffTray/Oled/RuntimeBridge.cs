using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OledPrototype
{
    // No Main here: ScreenOffTray.Program is the sole application entry point.
    internal static class Program
    {
        // Program Files is read-only for normal users. Recovery children use this same
        // per-user location, independent of the current working directory.
        internal static readonly string Runtime = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenOffTray", "运行记录");
        private static readonly Lazy<BoundedRuntimeLog> log = new Lazy<BoundedRuntimeLog>(
            delegate { return new BoundedRuntimeLog(Runtime); });
        internal static void Log(string value)
        {
            try { log.Value.Enqueue(value); } catch { }
        }

        // Optional shutdown tail. Caller awaits asynchronously with its own exit
        // budget; this does not synchronously drain or block the UI/hook thread.
        internal static Task<bool> FlushLogAsync(int timeoutMilliseconds)
        {
            try { return log.IsValueCreated ? log.Value.FlushAsync(timeoutMilliseconds) : Task.FromResult(true); }
            catch { return Task.FromResult(false); }
        }
    }

    internal static class Native
    {
        internal delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] internal struct KeyboardData
        { internal uint vkCode, scanCode, flags, time; internal UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] internal struct MouseData
        { internal int x, y; internal uint mouseData, flags, time; internal UIntPtr extra; }
        [DllImport("user32.dll", SetLastError=true)] internal static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);
        [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError=true)] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("kernel32.dll")] internal static extern uint SetThreadExecutionState(uint flags);
        [DllImport("user32.dll")] internal static extern int SetDisplayConfig(uint paths, IntPtr pathArray, uint modes, IntPtr modeArray, uint flags);
    }
}
