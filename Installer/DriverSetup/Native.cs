using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ScreenOffTray.DriverSetup
{
    internal sealed class Device
    {
        public string InstanceId, Provider, Version, Inf, MatchingId, Marker;
        public string[] HardwareIds;
        public bool Present, Started, Available;
        public uint Problem;
        public string ClassGuid;
    }

    internal static class Native
    {
        internal static readonly Guid DisplayClass = new Guid("4d36e968-e325-11ce-bfc1-08002be10318");
        internal static readonly IntPtr Invalid = new IntPtr(-1);
        [StructLayout(LayoutKind.Sequential)]
        internal struct DevInfo
        {
            internal uint Size;
            internal Guid ClassGuid;
            internal uint DevInst;
            internal IntPtr Reserved;
            internal static DevInfo New() { return new DevInfo { Size = (uint)Marshal.SizeOf(typeof(DevInfo)) }; }
        }
        internal static void Check(bool ok, string operation)
        {
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
        }
        internal static string Id(IntPtr set, ref DevInfo d)
        {
            StringBuilder text = new StringBuilder(1024); uint size;
            Check(SetupDiGetDeviceInstanceId(set, ref d, text, (uint)text.Capacity, out size), "DeviceInstanceId");
            return text.ToString();
        }
        internal static string[] Property(IntPtr set, ref DevInfo d, uint property)
        {
            byte[] bytes = new byte[65536]; uint type, required;
            if (!SetupDiGetDeviceRegistryProperty(set, ref d, property, out type, bytes, (uint)bytes.Length, out required))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 13) return new string[0]; // An optional property is absent.
                throw new Win32Exception(error, "Device property " + property);
            }
            if (required > bytes.Length || (type != 1 && type != 7)) throw new InvalidOperationException("Unexpected device property type/length");
            return Encoding.Unicode.GetString(bytes, 0, (int)required).Split(new char[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        }
        internal static void SetText(IntPtr set, ref DevInfo d, uint property, string value, bool multi)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(value + (multi ? "\0\0" : "\0"));
            Check(SetupDiSetDeviceRegistryProperty(set, ref d, property, bytes, (uint)bytes.Length), "Set own device property");
        }
        private static HashSet<string> PresentIds()
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, 6); // ALLCLASSES | PRESENT
            if (set == Invalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                for (uint i = 0; ; i++)
                {
                    DevInfo d = DevInfo.New();
                    if (!SetupDiEnumDeviceInfo(set, i, ref d)) { int e = Marshal.GetLastWin32Error(); if (e == 259) break; throw new Win32Exception(e); }
                    if (Policy.Exact(Property(set, ref d, 1))) ids.Add(Id(set, ref d));
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return ids;
        }
        internal static List<Device> Enumerate()
        {
            HashSet<string> present = PresentIds();
            List<Device> found = new List<Device>();
            IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, 4); // Includes nonpresent.
            if (set == Invalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                for (uint i = 0; ; i++)
                {
                    DevInfo d = DevInfo.New();
                    if (!SetupDiEnumDeviceInfo(set, i, ref d)) { int e = Marshal.GetLastWin32Error(); if (e == 259) break; throw new Win32Exception(e); }
                    string[] hw = Property(set, ref d, 1);
                    if (!Policy.Exact(hw)) continue;
                    string id = Id(set, ref d);
                    uint status, problem;
                    uint cr = CM_Get_DevNode_Status(out status, out problem, d.DevInst, 0);
                    if (present.Contains(id) && cr != 0) throw new InvalidOperationException("Cannot read present device status: " + cr);
                    Device item = new Device { InstanceId = id, HardwareIds = hw, Present = present.Contains(id),
                        Started = cr == 0 && (status & 8) != 0, Problem = cr == 0 ? problem : UInt32.MaxValue,
                        ClassGuid = d.ClassGuid.ToString("B"), Marker = First(Property(set, ref d, 13)) };
                    string driver = First(Property(set, ref d, 9));
                    if (driver.Length != 0)
                    {
                        using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                        using (RegistryKey key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driver))
                        {
                            if (key != null)
                            {
                                item.Provider = Convert.ToString(key.GetValue("ProviderName"));
                                item.Version = Convert.ToString(key.GetValue("DriverVersion"));
                                item.Inf = Convert.ToString(key.GetValue("InfPath"));
                                item.MatchingId = Convert.ToString(key.GetValue("MatchingDeviceId"));
                            }
                        }
                    }
                    found.Add(item);
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            List<OledPrototype.DisplayTarget> targets = found.Count == 0 ? new List<OledPrototype.DisplayTarget>() : OledPrototype.DisplayBackend.ListTargets();
            foreach (Device item in found)
                foreach (OledPrototype.DisplayTarget target in targets)
                    if (target.Available && Policy.Same(OledPrototype.DisplayBackend.RootInstancePath(target.AdapterPath), item.InstanceId))
                        item.Available = true;
            return found;
        }
        private static string First(string[] a) { return a.Length == 0 ? "" : a[0]; }
        internal static bool Remove(Device expected)
        {
            IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
            if (set == Invalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                DevInfo d = DevInfo.New();
                Check(SetupDiOpenDeviceInfo(set, expected.InstanceId, IntPtr.Zero, 0, ref d), "Open exact owned device");
                if (!Policy.Exact(Property(set, ref d, 1)) || !Policy.Same(First(Property(set, ref d, 13)), expected.Marker) ||
                    d.ClassGuid != DisplayClass) throw new InvalidOperationException("Ownership changed before removal");
                bool reboot;
                Check(DiUninstallDevice(IntPtr.Zero, set, ref d, 0, out reboot), "DiUninstallDevice(own instance, flags=0)");
                return reboot;
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
        // Install is on the exact newly-created devinfo. NULL driver selects the best
        // staged compatible driver; flags=0, never FORCE and never a machine-wide HWID update.
        internal static bool Create(string inf, Receipt receipt, StateStore store)
        {
            IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
            if (set == Invalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                DevInfo d = DevInfo.New(); Guid cls = DisplayClass;
                Check(SetupDiCreateDeviceInfo(set, "ScreenOffTrayVDD", ref cls, "Virtual Display Driver", IntPtr.Zero, 1, ref d), "Create root devinfo");
                receipt.InstanceId = Id(set, ref d);
                RegisterNewDevice(receipt, store.Save,
                    delegate { SetText(set, ref d, 1, @"Root\MttVDD", true); },
                    // SPDRP_LOCATION_INFORMATION on the unregistered devinfo: registration
                    // must never expose a node before its journaled ownership marker exists.
                    delegate { SetText(set, ref d, 13, receipt.Marker, false); },
                    delegate {
                        if (Enumerate().Exists(delegate(Device item) { return !Policy.Same(item.InstanceId, receipt.InstanceId); }))
                            throw new InvalidOperationException("Another MttVDD appeared during installation");
                    },
                    delegate { Check(SetupDiCallClassInstaller(0x19, set, ref d), "DIF_REGISTERDEVICE"); });
                StringBuilder destination = new StringBuilder(1024); uint required;
                Check(SetupCopyOEMInf(inf, null, 1, 0, destination, (uint)destination.Capacity, out required, IntPtr.Zero), "Stage pinned INF");
                receipt.Phase = "bind-intent"; store.Save(receipt);
                List<Device> current = Enumerate();
                if (current.Count != 1 || !Policy.Same(current[0].InstanceId, receipt.InstanceId))
                    throw new InvalidOperationException("Hardware ID collision before bind");
                bool reboot;
                Check(DiInstallDevice(IntPtr.Zero, set, ref d, IntPtr.Zero, 0, out reboot), "DiInstallDevice(flags=0)");
                receipt.NeedsReboot = reboot;
                receipt.Phase = "bound"; store.Save(receipt);
                return reboot;
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
        internal static void RegisterNewDevice(Receipt receipt, Action<Receipt> save,
            Action setHardwareId, Action setMarker, Action checkCollision, Action register)
        {
            // Exact InstanceId is already assigned. Intent must be durable before any
            // property assignment/registration; exceptions deliberately stop the sequence.
            receipt.Phase = "register-intent"; save(receipt);
            setHardwareId(); setMarker();
            // Keep the collision check immediately before registration.
            checkCollision(); register();
            receipt.Phase = "registered"; save(receipt);
        }
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(IntPtr guid, string enumerator, IntPtr hwnd, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] internal static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr guid, IntPtr hwnd);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DevInfo info);
        [DllImport("setupapi.dll", SetLastError = true)] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DevInfo info, StringBuilder id, uint size, out uint required);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true)]
        private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref DevInfo info, uint property, out uint type, byte[] buffer, uint size, out uint required);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiSetDeviceRegistryPropertyW", SetLastError = true)]
        private static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set, ref DevInfo info, uint property, byte[] buffer, uint size);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiCreateDeviceInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiCreateDeviceInfo(IntPtr set, string name, ref Guid guid, string desc, IntPtr hwnd, uint flags, ref DevInfo info);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiCallClassInstaller(uint function, IntPtr set, ref DevInfo info);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiOpenDeviceInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiOpenDeviceInfo(IntPtr set, string id, IntPtr hwnd, uint flags, ref DevInfo info);
        [DllImport("setupapi.dll", EntryPoint = "SetupCopyOEMInfW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupCopyOEMInf(string inf, string media, uint mediaType, uint flags, StringBuilder destination, uint size, out uint required, IntPtr component);
        [DllImport("newdev.dll", SetLastError = true)] private static extern bool DiInstallDevice(IntPtr hwnd, IntPtr set, ref DevInfo info, IntPtr driver, uint flags, out bool reboot);
        [DllImport("newdev.dll", SetLastError = true)] private static extern bool DiUninstallDevice(IntPtr hwnd, IntPtr set, ref DevInfo info, uint flags, out bool reboot);
        [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint instance, uint flags);
    }
}
