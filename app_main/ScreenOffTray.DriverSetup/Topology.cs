using System;
using System.IO;
using System.Runtime.InteropServices;
using OledPrototype;

namespace ScreenOffTray.DriverSetup
{
    internal static class Topology
    {
        // Shares the reviewed CCD ABI, serialization, identity checks and verification.
        // No Prepare/TurnOff calls are reachable from this helper.
        internal static DisplayBackend.Snapshot Capture()
        {
            DisplayBackend.CheckLayout();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint pc, mc;
                int e = GetDisplayConfigBufferSizes(0x12, out pc, out mc);
                if (e == 122) continue;
                Check(e);
                if (pc == 0 || pc > 256 || mc > 1024) throw new InvalidOperationException("Unsupported CCD size");
                DisplayBackend.PathInfo[] paths = new DisplayBackend.PathInfo[pc];
                DisplayBackend.ModeInfo[] modes = new DisplayBackend.ModeInfo[mc];
                e = QueryDisplayConfig(0x12, ref pc, paths, ref mc, modes, IntPtr.Zero);
                if (e == 122) continue;
                Check(e);
                if (pc > paths.Length || mc > modes.Length) throw new InvalidOperationException("CCD buffer overflow");
                Array.Resize(ref paths, (int)pc); Array.Resize(ref modes, (int)mc);
                DisplayBackend.Snapshot s = new DisplayBackend.Snapshot { Paths = paths, Modes = modes,
                    DevicePaths = new string[pc], AdapterPaths = new string[pc] };
                for (int i = 0; i < pc; i++)
                {
                    DisplayBackend.TargetName t = new DisplayBackend.TargetName {
                        Header = new DisplayBackend.DeviceHeader { Type = 2, Size = 420, Adapter = paths[i].Target.Adapter, Id = paths[i].Target.Id } };
                    DisplayBackend.AdapterName a = new DisplayBackend.AdapterName {
                        Header = new DisplayBackend.DeviceHeader { Type = 4, Size = 276, Adapter = paths[i].Target.Adapter } };
                    Check(GetTargetName(ref t)); Check(GetAdapterName(ref a));
                    s.DevicePaths[i] = t.DevicePath; s.AdapterPaths[i] = a.DevicePath;
                }
                DisplayBackend.ValidateSnapshot(s);
                return s;
            }
            throw new InvalidOperationException("CCD topology is changing; try again later");
        }
        internal static void Save(string path, DisplayBackend.Snapshot s)
        {
            using (FileStream file = StateStore.CreateFile(path))
            { DisplayBackend.WriteSnapshot(file, s); file.Flush(true); }
            DisplayBackend.VerifySnapshot(s, Capture());
        }
        internal static void Restore(string path)
        {
            StateStore.NoReparse(path);
            // Recover validates all original target + adapter identities and exact modes before APPLY.
            DisplayBackend.Recover(path);
            DisplayBackend.Snapshot s;
            using (FileStream file = File.OpenRead(path)) s = DisplayBackend.ReadSnapshot(file);
            // Persist ONLY the original layout, so Windows' automatic extended layout
            // caused by a new display is not retained in the CCD database.
            Check(SetDisplayConfig((uint)s.Paths.Length, s.Paths, (uint)s.Modes.Length, s.Modes, 0x82a0));
            DisplayBackend.VerifySnapshot(s, Capture());
        }
        internal static void Resolution(DisplayBackend.Snapshot s, out uint width, out uint height)
        {
            for (int i = 0; i < s.Paths.Length; i++)
                if (DisplayBackend.IsPhysical(s.Paths[i].Target.Technology, s.AdapterPaths[i]))
                {
                    uint index = DisplayBackend.SourceIndex(s.Paths[i]);
                    if (index >= s.Modes.Length || s.Modes[index].InfoType != 1) throw new InvalidOperationException("Invalid physical source mode");
                    width = s.Modes[index].Source.Width; height = s.Modes[index].Source.Height; return;
                }
            throw new InvalidOperationException("No known active physical display");
        }
        private static void Check(int e) { if (e != 0) throw new System.ComponentModel.Win32Exception(e, "CCD capture/restore"); }
        [DllImport("user32.dll", ExactSpelling = true)] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll", ExactSpelling = true)] private static extern int QueryDisplayConfig(uint flags, ref uint paths,
            [Out] DisplayBackend.PathInfo[] pathArray, ref uint modes, [Out] DisplayBackend.ModeInfo[] modeArray, IntPtr topologyId);
        [DllImport("user32.dll", ExactSpelling = true)] private static extern int SetDisplayConfig(uint paths,
            [In] DisplayBackend.PathInfo[] pathArray, uint modes, [In] DisplayBackend.ModeInfo[] modeArray, uint flags);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true)] private static extern int GetTargetName(ref DisplayBackend.TargetName name);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true)] private static extern int GetAdapterName(ref DisplayBackend.AdapterName name);
    }
}
