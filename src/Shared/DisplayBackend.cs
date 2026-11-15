using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace OledPrototype
{
    public sealed class DisplayTarget
    {
        public string Name;
        public string DevicePath;
        public string AdapterPath;
        public uint Technology;
        public bool Available;
        public bool Active;
    }

    // Windows CCD backend. Construction and the pure helpers have no native side
    // effects. Prepare uses queries + VALIDATE only; TurnOff/Restore/Recover are
    // the explicit, display-changing entry points. No database flags are used.
    // Serializes this instance only: the caller owns cross-process/watchdog/UI
    // coordination, recovery-file lifetime, and confirmation of actual OLED power.
    public sealed class DisplayBackend
    {
        internal const uint Active = 1, VirtualMode = 8, Invalid = 0xffffffff;
        internal const uint QueryAll = 1, QueryActive = 2, QueryVirtualAware = 0x10;
        internal const uint UseSupplied = 0x20, ValidateFlag = 0x40,
            ApplyFlag = 0x80, AllowChanges = 0x400, SetVirtualAware = 0x8000;
        internal const uint BaseSetFlags = UseSupplied | SetVirtualAware;
        internal const int MaxPaths = 256, MaxModes = 1024, MaxFileBytes = 1024 * 1024;
        private const ulong RecoveryMagic = 0x3144434344454c4fUL;
        private const uint RecoveryVersion = 1;
        private readonly object sync = new object();
        private Snapshot original;
        private Plan prepared;
        private bool offAttempted;
        private bool restoredVerified;

        public bool IsPrepared { get { lock (sync) { return prepared != null && !offAttempted; } } }
        public bool HasRecoverySnapshot { get { lock (sync) { return original != null; } } }
        public bool RestorationVerified { get { lock (sync) { return restoredVerified; } } }
        public bool ValidationAllowsChanges
        {
            get { lock (sync) { return prepared != null && prepared.AllowModeChanges; } }
        }
        public string PreparationSummary
        {
            get
            {
                lock (sync)
                {
                    if (prepared == null) return "尚未完成只读准备。";
                    SourceMode s = prepared.Modes[0].Source;
                    return String.Format("virtual={0}; source={1}x{2} @ ({3},{4}); {5}; " +
                        "仅VALIDATE通过，尚不证明显示切换或OLED断电。", prepared.DevicePath,
                        s.Width, s.Height, s.X, s.Y, prepared.ValidationDescription);
                }
            }
        }

        public static List<DisplayTarget> ListTargets()
        {
            CheckLayout();
            List<Route> routes = ReadRoutes(Query(QueryAll));
            List<DisplayTarget> result = new List<DisplayTarget>();
            foreach (Route route in routes)
            {
                DisplayTarget item = result.Find(delegate(DisplayTarget t)
                {
                    return !String.IsNullOrEmpty(t.DevicePath)
                        ? SameText(t.DevicePath, route.Device.DevicePath)
                        : String.IsNullOrEmpty(route.Device.DevicePath) &&
                          t.Technology == route.Device.Technology && SameText(t.AdapterPath, route.Device.AdapterPath);
                });
                if (item == null)
                {
                    result.Add(new DisplayTarget {
                        Name = route.Device.Name, DevicePath = route.Device.DevicePath,
                        AdapterPath = route.Device.AdapterPath, Technology = route.Device.Technology,
                        Available = route.Device.Available, Active = route.Device.Active });
                }
                else
                {
                    item.Available |= route.Device.Available;
                    item.Active |= route.Device.Active;
                }
            }
            return result;
        }

        public void Prepare(string targetDevicePath)
        {
            lock (sync)
            {
                if (original != null)
                    throw new InvalidOperationException("仍保留旧恢复快照。先Restore并核验，" +
                        "或在原拓扑未变时直接ConfirmRestored；确认成功后才能再次Prepare。");
                if (String.IsNullOrWhiteSpace(targetDevicePath))
                    throw new ArgumentException("必须显式指定虚拟输出的完整DevicePath。", "targetDevicePath");
                CheckLayout();
                Snapshot baseline = CaptureActive();
                Plan plan = BuildPlan(baseline, ReadRoutes(Query(QueryAll)), targetDevicePath);
                ValidatePlan(plan);
                VerifySnapshot(baseline, CaptureActive());
                // No snapshot replacement until the complete read-only preparation
                // and final topology recheck have succeeded.
                original = baseline;
                prepared = plan;
                offAttempted = false;
                restoredVerified = false;
            }
        }

        public void SaveRecovery(string path)
        {
            lock (sync)
            {
                RequireSnapshot();
                byte[] data;
                using (MemoryStream memory = new MemoryStream())
                {
                    WriteSnapshot(memory, original);
                    data = memory.ToArray();
                }
                string destination = Path.GetFullPath(path);
                if (File.Exists(destination))
                    throw new IOException("恢复文件已存在，拒绝覆盖：" + destination);
                // Same-directory rename publishes only a fully flushed snapshot.
                // File.Move refuses replacement even if another writer won a race.
                string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
                bool ownedTemporary = false;
                try
                {
                    using (FileStream stream = new FileStream(temporary, FileMode.CreateNew,
                        FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough))
                    {
                        ownedTemporary = true;
                        stream.Write(data, 0, data.Length);
                        stream.Flush(true);
                    }
                    File.Move(temporary, destination);
                    ownedTemporary = false;
                }
                finally
                {
                    if (ownedTemporary)
                    {
                        // Do not obscure the save error if temporary cleanup fails.
                        try { File.Delete(temporary); } catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
            }
        }

        public void TurnOff()
        {
            lock (sync)
            {
                RequireSnapshot();
                if (prepared == null || offAttempted)
                    throw new InvalidOperationException("本次准备已使用或不完整；先恢复并确认，不能重复切换。");
                // Refresh availability and validation without replacing the original.
                VerifySnapshot(original, CaptureActive());
                Plan current = BuildPlan(original, ReadRoutes(Query(QueryAll)), prepared.DevicePath);
                ValidatePlan(current);
                VerifySnapshot(original, CaptureActive());
                offAttempted = true;
                restoredVerified = false;
                try
                {
                    Set(current.Paths, current.Modes, BaseSetFlags | ApplyFlag |
                        (current.AllowModeChanges ? AllowChanges : 0), "切换到指定virtual-only拓扑");
                    VerifyOff(current, CaptureActive());
                    prepared = current;
                }
                catch (Exception operationError)
                {
                    Exception recoveryError = null;
                    try { RestoreCore(original); restoredVerified = true; }
                    catch (Exception ex) { recoveryError = ex; }
                    string message = "virtual-only切换/回读失败：" + operationError.Message +
                        (recoveryError == null ? "；已应用原快照并回读核验恢复，快照仍保留。" :
                         "；原快照恢复也失败：" + recoveryError.Message + "；保留恢复快照，请交给看门狗处理。");
                    throw new InvalidOperationException(message, recoveryError == null ? operationError :
                        new AggregateException(operationError, recoveryError));
                }
            }
        }

        public void Restore()
        {
            lock (sync)
            {
                RequireSnapshot();
                restoredVerified = false;
                // Disallow further TurnOff even if restore validation/apply fails.
                offAttempted = true;
                RestoreCore(original);
                restoredVerified = true;
            }
        }

        // Extra acknowledgment API: reads the real topology again, performs no
        // APPLY, and only then permits the caller to replace this snapshot.
        // This also permits abandoning a prepared but never-applied plan safely.
        public void ConfirmRestored()
        {
            lock (sync)
            {
                RequireSnapshot();
                restoredVerified = false;
                VerifySnapshot(original, CaptureActive());
                restoredVerified = true;
                original = null;
                prepared = null;
                offAttempted = false;
            }
        }

        public static void Recover(string path)
        {
            CheckLayout();
            Snapshot snapshot;
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                snapshot = ReadSnapshot(stream);
            RestoreCore(snapshot);
            // Never delete or overwrite recovery evidence here.
        }

        private void RequireSnapshot()
        {
            if (original == null) throw new InvalidOperationException("没有已准备的恢复快照。");
        }

        internal sealed class Snapshot
        {
            internal PathInfo[] Paths;
            internal ModeInfo[] Modes;
            internal string[] DevicePaths;
            internal string[] AdapterPaths;
        }

        internal sealed class Route
        {
            internal PathInfo Path;
            internal DisplayTarget Device;
        }

        internal sealed class Plan
        {
            internal PathInfo[] Paths;
            internal ModeInfo[] Modes;
            internal string DevicePath;
            internal string AdapterPath;
            internal bool AllowModeChanges;
            internal string ValidationDescription;
        }

        internal static bool IsKnownVirtual(uint technology, string adapterPath)
        {
            return IsKnownVirtual(technology, adapterPath, ReadHardwareIds);
        }

        internal static bool IsKnownVirtual(uint technology, string adapterPath, Func<string, string[]> readHardwareIds)
        {
            if (technology == 17) return true;
            // The installed MttVDD reports HDMI (5), not INDIRECT_WIRED (16).
            // Neither connector type identifies a virtual adapter by itself.
            if (technology != 5 && technology != 16) return false;
            string instance = RootInstancePath(adapterPath);
            if (instance == null) return false;
            return IsKnownVirtualHardware(readHardwareIds(instance));
        }

        private static string[] ReadHardwareIds(string instance)
        {
            // Query the actual PnP hardware ID. ROOT#DISPLAY and friendly names
            // alone cannot identify a virtual adapter. IDs below were verified
            // in the installed drivers or the candidate driver's official INF.
            try {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + instance))
                    return key == null ? null : key.GetValue("HardwareID") as string[];
            } catch { return null; }
        }

        internal static string RootInstancePath(string path)
        {
            if (String.IsNullOrEmpty(path) || !path.StartsWith(@"\\?\ROOT#", StringComparison.OrdinalIgnoreCase)) return null;
            string[] parts = path.Substring(4).Split('#');
            Guid deviceInterface;
            if (parts.Length != 4 || parts[1].Length == 0 || parts[2].Length == 0 ||
                !Guid.TryParse(parts[3], out deviceInterface) ||
                deviceInterface != new Guid("5b45201d-f2f2-4f3b-85bb-30ff1f953599")) return null;
            for (int i = 0; i < 3; i++)
                foreach (char c in parts[i]) if (!Char.IsLetterOrDigit(c) && c != '_' && c != '&' && c != '-') return null;
            return parts[0] + @"\" + parts[1] + @"\" + parts[2];
        }

        internal static bool IsKnownVirtualHardware(string[] ids)
        {
            if (ids == null) return false;
            foreach (string id in ids)
                if (SameText(id, @"Root\GameViewerIddDriver") || SameText(id, @"Root\tdIdd") ||
                    SameText(id, @"Root\MttVDD") || SameText(id, "MttVDD")) return true;
            return false;
        }

        internal static bool IsPhysical(uint technology, string adapterPath)
        {
            if (IsKnownVirtual(technology, adapterPath)) return false;
            // Unknown, Miracast and ambiguous indirect-wired outputs are refused
            // as the physical baseline in this deliberately limited first version.
            return technology <= 6 || (technology >= 8 && technology <= 14) ||
                technology == 18 || technology == 0x80000000;
        }

        internal static Plan BuildPlan(Snapshot baseline, List<Route> all, string devicePath)
        {
            ValidateSnapshot(baseline);
            if (String.IsNullOrWhiteSpace(devicePath))
                throw new InvalidOperationException("未显式指定DevicePath，不自动选择输出。");
            Route chosen = null;
            bool found = false;
            foreach (Route route in all)
            {
                if (!SameText(route.Device.DevicePath, devicePath)) continue;
                found = true;
                if (!route.Device.Available) continue;
                if (!IsKnownVirtual(route.Device.Technology, route.Device.AdapterPath))
                    throw new InvalidOperationException("指定输出未证实为虚拟屏：technology=" +
                        route.Device.Technology + "; adapter=" + route.Device.AdapterPath +
                        "。要求INDIRECT_VIRTUAL(17)或已核实硬件ID的ROOT虚拟驱动；HDMI(5)/16本身不足以确认。");
                if (route.Path.Target.Technology != route.Device.Technology ||
                    route.Path.Target.Available == 0)
                    throw new InvalidOperationException("输出路径和设备可用性/technology不一致，重新探测。");
                if (chosen != null && !SameTarget(chosen.Path, route.Path))
                    throw new InvalidOperationException("DevicePath对应多个不同target，拒绝歧义选择。");
                // Prefer the already-active route; otherwise first OS-priority route.
                if (chosen == null || (route.Path.Flags & Active) != 0) chosen = route;
            }
            if (chosen == null)
                throw new InvalidOperationException(found ? "指定虚拟输出存在但目前不可用。" :
                    "QDC_ALL_PATHS未找到指定DevicePath；没有自动选择其他显示器。");

            int physical = -1;
            for (int i = 0; i < baseline.Paths.Length; i++)
            {
                PathInfo path = baseline.Paths[i];
                if (IsPhysical(path.Target.Technology, baseline.AdapterPaths[i])) physical = i;
                else if (!SameTarget(path, chosen.Path) ||
                    !SameText(baseline.DevicePaths[i], devicePath))
                    throw new InvalidOperationException("原active含未选中的虚拟输出；第一版只支持一个物理屏及可选的指定虚拟屏。");
            }
            SourceMode source = GetSource(baseline, baseline.Paths[physical]);
            if (SameTarget(baseline.Paths[physical], chosen.Path))
                throw new InvalidOperationException("指定虚拟target与原物理target身份重合，环境探测不一致。");
            PathInfo replacement = chosen.Path;
            replacement.Flags = (replacement.Flags & VirtualMode) | Active;
            replacement.Source.ModeIndex = (replacement.Flags & VirtualMode) != 0 ? 0x0000ffffU : 0U;
            replacement.Source.Status = 0;
            replacement.Target.ModeIndex = Invalid; // Both virtual target/desktop halves INVALID.
            replacement.Target.Rotation = 1; // Virtual desktop orientation is represented by source dimensions.
            replacement.Target.Scaling = 1;
            replacement.Target.Refresh = new Rational();
            replacement.Target.ScanLine = 0; // Required for an unspecified 0/0 refresh rate.
            replacement.Target.Status = 0;
            ModeInfo mode = new ModeInfo();
            mode.InfoType = 1;
            mode.Id = replacement.Source.Id;
            mode.Adapter = replacement.Source.Adapter;
            mode.Source = source;
            return new Plan { Paths = new PathInfo[] { replacement }, Modes = new ModeInfo[] { mode },
                DevicePath = chosen.Device.DevicePath, AdapterPath = chosen.Device.AdapterPath };
        }

        private static void ValidatePlan(Plan plan)
        {
            int strict = SetCode(plan.Paths, plan.Modes, BaseSetFlags | ValidateFlag);
            if (strict == 0)
            {
                plan.AllowModeChanges = false;
                plan.ValidationDescription = "严格VALIDATE通过";
                return;
            }
            int flexible = SetCode(plan.Paths, plan.Modes, BaseSetFlags | ValidateFlag | AllowChanges);
            if (flexible != 0)
                throw new InvalidOperationException("virtual-only校验失败；严格校验：" + Error(strict) +
                    "；ALLOW_CHANGES校验：" + Error(flexible) + "。当前驱动/源模式无法完成此计划。");
            plan.AllowModeChanges = true;
            plan.ValidationDescription = "严格VALIDATE失败(" + Error(strict) +
                ")；ALLOW_CHANGES校验通过，应用后必须核尺寸/位置，不符即恢复";
        }

        internal static void VerifyOff(Plan plan, Snapshot current)
        {
            ValidateArrayShapes(current);
            if (current.Paths.Length != 1 || !SameTarget(current.Paths[0], plan.Paths[0]) ||
                !SameText(current.DevicePaths[0], plan.DevicePath) ||
                !SameText(current.AdapterPaths[0], plan.AdapterPath) ||
                !IsKnownVirtual(current.Paths[0].Target.Technology, current.AdapterPaths[0]) ||
                current.Paths[0].Target.Available == 0)
                throw new InvalidOperationException("回读未证实仅指定虚拟target active、原物理target不active。");
            SourceMode expected = plan.Modes[0].Source;
            SourceMode actual = GetSource(current, current.Paths[0]);
            if (!SameSource(expected, actual))
                throw new InvalidOperationException(String.Format("虚拟源尺寸/位置/像素格式改变：要求{0}x{1}@({2},{3})，" +
                    "实际{4}x{5}@({6},{7})。拒绝静默降分辨率。", expected.Width, expected.Height,
                    expected.X, expected.Y, actual.Width, actual.Height, actual.X, actual.Y));
        }

        internal static void VerifySnapshot(Snapshot expected, Snapshot actual)
        {
            ValidateSnapshot(expected);
            ValidateArrayShapes(actual);
            if (expected.Paths.Length != actual.Paths.Length)
                throw new InvalidOperationException("active路径数量与原快照不一致。");
            bool[] matched = new bool[actual.Paths.Length];
            for (int i = 0; i < expected.Paths.Length; i++)
            {
                PathInfo wanted = expected.Paths[i];
                int match = -1;
                for (int j = 0; j < actual.Paths.Length; j++)
                    if (!matched[j] && SameRoute(wanted, actual.Paths[j]) &&
                        SameText(expected.DevicePaths[i], actual.DevicePaths[j]) &&
                        SameText(expected.AdapterPaths[i], actual.AdapterPaths[j])) { match = j; break; }
                if (match < 0) throw new InvalidOperationException("原target/source身份未恢复：" + expected.DevicePaths[i]);
                matched[match] = true;
                PathInfo got = actual.Paths[match];
                if (got.Target.Available == 0 || got.Target.Technology != wanted.Target.Technology ||
                    !SameSource(GetSource(expected, wanted), GetSource(actual, got)) ||
                    wanted.Target.Rotation != got.Target.Rotation || wanted.Target.Scaling != got.Target.Scaling ||
                    !SameRatio(wanted.Target.Refresh, got.Target.Refresh) ||
                    wanted.Target.ScanLine != got.Target.ScanLine ||
                    !SameSignal(GetTarget(expected, wanted), GetTarget(actual, got)))
                    throw new InvalidOperationException("原target虽active，但尺寸/位置/像素格式/旋转/时序未恢复：" +
                        expected.DevicePaths[i]);
            }
        }

        private static void RestoreCore(Snapshot snapshot)
        {
            ValidateSnapshot(snapshot);
            List<Route> all = ReadRoutes(Query(QueryAll));
            for (int i = 0; i < snapshot.Paths.Length; i++)
            {
                bool available = false;
                foreach (Route route in all)
                    if (SameRoute(snapshot.Paths[i], route.Path) && route.Device.Available &&
                        SameText(snapshot.DevicePaths[i], route.Device.DevicePath) &&
                        SameText(snapshot.AdapterPaths[i], route.Device.AdapterPath)) { available = true; break; }
                if (!available) throw new InvalidOperationException("原快照路径当前不可用或身份已改变（例如重启后LUID变化）：" +
                    snapshot.DevicePaths[i] + "；拒绝将旧快照应用到其他设备。");
            }
            Set(snapshot.Paths, snapshot.Modes, BaseSetFlags | ValidateFlag, "校验原快照恢复");
            Set(snapshot.Paths, snapshot.Modes, BaseSetFlags | ApplyFlag, "应用原path+mode快照恢复");
            VerifySnapshot(snapshot, CaptureActive());
        }

        private static Snapshot CaptureActive()
        {
            Snapshot snapshot = Query(QueryActive);
            List<Route> routes = ReadRoutes(snapshot);
            snapshot.DevicePaths = new string[routes.Count];
            snapshot.AdapterPaths = new string[routes.Count];
            for (int i = 0; i < routes.Count; i++)
            {
                snapshot.DevicePaths[i] = routes[i].Device.DevicePath;
                snapshot.AdapterPaths[i] = routes[i].Device.AdapterPath;
            }
            return snapshot;
        }

        private static Snapshot Query(uint type)
        {
            if (type != QueryAll && type != QueryActive) throw new ArgumentOutOfRangeException("type");
            uint flags = type | QueryVirtualAware;
            // At most three complete buffer-size/query attempts, no database query.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint paths, modes;
                int error = Native.GetDisplayConfigBufferSizes(flags, out paths, out modes);
                if (error == 122) continue;
                if (error != 0) throw NativeError("GetDisplayConfigBufferSizes", error);
                if (paths > MaxPaths || modes > MaxModes)
                    throw new InvalidOperationException("显示配置规模超出原型上限：paths=" + paths + "; modes=" + modes);
                PathInfo[] pathArray = new PathInfo[paths];
                ModeInfo[] modeArray = new ModeInfo[modes];
                uint allocatedPaths = paths, allocatedModes = modes;
                error = Native.QueryDisplayConfig(flags, ref paths, pathArray, ref modes, modeArray, IntPtr.Zero);
                if (error == 122) continue;
                if (error != 0) throw NativeError("QueryDisplayConfig", error);
                if (paths > allocatedPaths || modes > allocatedModes)
                    throw new InvalidOperationException("QueryDisplayConfig返回越界数量。");
                Array.Resize(ref pathArray, (int)paths);
                Array.Resize(ref modeArray, (int)modes);
                return new Snapshot { Paths = pathArray, Modes = modeArray };
            }
            throw new InvalidOperationException("显示拓扑持续变化：buffer不足(122)，三次探测仍未稳定，请稍后重新准备。");
        }

        private static List<Route> ReadRoutes(Snapshot snapshot)
        {
            List<Route> result = new List<Route>();
            Dictionary<string, DisplayTarget> names = new Dictionary<string, DisplayTarget>();
            foreach (PathInfo path in snapshot.Paths)
            {
                string key = TargetKey(path);
                DisplayTarget name;
                if (!names.TryGetValue(key, out name))
                {
                    bool strict = path.Target.Available != 0 || (path.Flags & Active) != 0;
                    TargetName target = new TargetName();
                    target.Header = Header(2, Marshal.SizeOf(typeof(TargetName)), path.Target.Adapter, path.Target.Id);
                    int targetError = Native.GetTargetName(ref target);
                    AdapterName adapter = new AdapterName();
                    adapter.Header = Header(4, Marshal.SizeOf(typeof(AdapterName)), path.Target.Adapter, 0);
                    int adapterError = Native.GetAdapterName(ref adapter);
                    if (strict && (targetError != 0 || adapterError != 0))
                        throw new InvalidOperationException("读取可用显示设备身份失败：" + key +
                            "; TargetName=" + Error(targetError) + "; AdapterName=" + Error(adapterError));
                    name = new DisplayTarget {
                        Name = targetError == 0 ? target.FriendlyName : "不可用target，身份读取失败：" + Error(targetError),
                        DevicePath = targetError == 0 ? target.DevicePath : String.Empty,
                        AdapterPath = adapterError == 0 ? adapter.DevicePath : String.Empty,
                        Technology = path.Target.Technology };
                    names.Add(key, name);
                }
                // Preserve one route per source/target combination. QDC_ALL_PATHS
                // may return multiple routes to a target; it is not a monitor list.
                result.Add(new Route { Path = path, Device = new DisplayTarget {
                    Name = name.Name, DevicePath = name.DevicePath, AdapterPath = name.AdapterPath,
                    Technology = path.Target.Technology, Available = path.Target.Available != 0,
                    Active = (path.Flags & Active) != 0 } });
            }
            return result;
        }

        // Optional read-only source-name diagnostic for the host, using type 1.
        public string GetPreparedSourceName()
        {
            lock (sync)
            {
                if (prepared == null) throw new InvalidOperationException("尚未准备。");
                PathInfo path = prepared.Paths[0];
                SourceName name = new SourceName();
                name.Header = Header(1, Marshal.SizeOf(typeof(SourceName)), path.Source.Adapter, path.Source.Id);
                int error = Native.GetSourceName(ref name);
                if (error != 0) throw NativeError("DisplayConfigGetDeviceInfo(SourceName)", error);
                return name.GdiName;
            }
        }

        private static DeviceHeader Header(uint type, int size, Luid adapter, uint id)
        {
            return new DeviceHeader { Type = type, Size = (uint)size, Adapter = adapter, Id = id };
        }

        private static int SetCode(PathInfo[] paths, ModeInfo[] modes, uint flags)
        {
            // Native validation is permitted to negotiate internally; never let
            // marshaling expose the retained recovery arrays to an in/out buffer.
            PathInfo[] p = (PathInfo[])paths.Clone();
            ModeInfo[] m = (ModeInfo[])modes.Clone();
            return Native.SetDisplayConfig((uint)p.Length, p, (uint)m.Length, m, flags);
        }

        private static void Set(PathInfo[] paths, ModeInfo[] modes, uint flags, string action)
        {
            int error = SetCode(paths, modes, flags);
            if (error != 0) throw NativeError(action, error);
        }

        private static Exception NativeError(string action, int error)
        {
            return new InvalidOperationException(action + "失败：" + Error(error) +
                (error == 5 ? "；需要本机交互控制台会话，远程会话/权限可能不满足。" : String.Empty));
        }

        private static string Error(int code) { return code + " (" + new Win32Exception(code).Message + ")"; }
        private static bool SameText(string a, string b) { return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        private static bool SameLuid(Luid a, Luid b) { return a.Low == b.Low && a.High == b.High; }
        private static string TargetKey(PathInfo p) { return p.Target.Adapter.High + ":" + p.Target.Adapter.Low + ":" + p.Target.Id; }
        private static bool SameTarget(PathInfo a, PathInfo b)
        {
            return SameLuid(a.Target.Adapter, b.Target.Adapter) && a.Target.Id == b.Target.Id;
        }
        private static bool SameRoute(PathInfo a, PathInfo b)
        {
            return SameTarget(a, b) && SameLuid(a.Source.Adapter, b.Source.Adapter) && a.Source.Id == b.Source.Id;
        }
        private static bool SameSource(SourceMode a, SourceMode b)
        {
            return a.Width == b.Width && a.Height == b.Height && a.X == b.X && a.Y == b.Y && a.PixelFormat == b.PixelFormat;
        }
        private static bool SameRatio(Rational a, Rational b)
        {
            if (a.Denominator == 0 || b.Denominator == 0)
                return a.Numerator == b.Numerator && a.Denominator == b.Denominator;
            return (ulong)a.Numerator * b.Denominator == (ulong)b.Numerator * a.Denominator;
        }
        private static bool SameSignal(Signal a, Signal b)
        {
            return a.PixelRate == b.PixelRate && SameRatio(a.HSync, b.HSync) && SameRatio(a.VSync, b.VSync) &&
                a.ActiveWidth == b.ActiveWidth && a.ActiveHeight == b.ActiveHeight &&
                a.TotalWidth == b.TotalWidth && a.TotalHeight == b.TotalHeight &&
                a.VideoStandard == b.VideoStandard && a.ScanLine == b.ScanLine;
        }

        internal static uint SourceIndex(PathInfo path)
        {
            return (path.Flags & VirtualMode) != 0 ? path.Source.ModeIndex >> 16 : path.Source.ModeIndex;
        }
        private static uint TargetIndex(PathInfo path)
        {
            return (path.Flags & VirtualMode) != 0 ? path.Target.ModeIndex >> 16 : path.Target.ModeIndex;
        }
        private static SourceMode GetSource(Snapshot snapshot, PathInfo path)
        {
            uint index = SourceIndex(path);
            ModeInfo mode = GetMode(snapshot, index, 1, path.Source.Adapter, path.Source.Id);
            if (mode.Source.Width == 0 || mode.Source.Height == 0 || mode.Source.PixelFormat < 1 || mode.Source.PixelFormat > 5)
                throw new InvalidOperationException("source尺寸/像素格式无效。");
            return mode.Source;
        }
        private static Signal GetTarget(Snapshot snapshot, PathInfo path)
        {
            return GetMode(snapshot, TargetIndex(path), 2, path.Target.Adapter, path.Target.Id).Target;
        }
        private static ModeInfo GetMode(Snapshot snapshot, uint index, uint type, Luid adapter, uint id)
        {
            if (index == Invalid || index == 0xffff || index >= snapshot.Modes.Length)
                throw new InvalidOperationException("active path的mode索引缺失或越界。");
            ModeInfo mode = snapshot.Modes[index];
            if (mode.InfoType != type || mode.Id != id || !SameLuid(mode.Adapter, adapter))
                throw new InvalidOperationException("mode类型或adapter/id与path不匹配。");
            return mode;
        }

        private static void ValidateArrayShapes(Snapshot snapshot)
        {
            if (snapshot == null || snapshot.Paths == null || snapshot.Modes == null ||
                snapshot.DevicePaths == null || snapshot.AdapterPaths == null || snapshot.Paths.Length == 0 ||
                snapshot.Paths.Length > MaxPaths || snapshot.Modes.Length == 0 || snapshot.Modes.Length > MaxModes ||
                snapshot.DevicePaths.Length != snapshot.Paths.Length || snapshot.AdapterPaths.Length != snapshot.Paths.Length)
                throw new InvalidOperationException("快照数组缺失、数量越界或元数据数量不一致。");
            HashSet<string> targets = new HashSet<string>();
            for (int i = 0; i < snapshot.Paths.Length; i++)
            {
                PathInfo path = snapshot.Paths[i];
                if ((path.Flags & Active) == 0 || !targets.Add(TargetKey(path)) ||
                    String.IsNullOrWhiteSpace(snapshot.DevicePaths[i]) || String.IsNullOrWhiteSpace(snapshot.AdapterPaths[i]))
                    throw new InvalidOperationException("快照包含inactive/重复target或缺少设备身份。");
                GetSource(snapshot, path);
            }
        }

        internal static void ValidateSnapshot(Snapshot snapshot)
        {
            ValidateArrayShapes(snapshot);
            if (snapshot.Paths.Length > 2)
                throw new InvalidOperationException("第一版拒绝多屏：原active最多一个物理target加一个指定虚拟target。");
            int physical = 0, virtualCount = 0;
            for (int i = 0; i < snapshot.Paths.Length; i++)
            {
                PathInfo path = snapshot.Paths[i];
                if (path.Target.Available == 0 || (path.Target.Status & 0x20) != 0)
                    throw new InvalidOperationException("原active target已不可用或为HMD，不能建立可靠恢复快照。");
                if (IsPhysical(path.Target.Technology, snapshot.AdapterPaths[i])) physical++;
                else if (IsKnownVirtual(path.Target.Technology, snapshot.AdapterPaths[i])) virtualCount++;
                else throw new InvalidOperationException("原active输出类型无法确认物理/虚拟身份：technology=" + path.Target.Technology);
                GetTarget(snapshot, path);
                if ((path.Flags & VirtualMode) != 0)
                {
                    uint desktop = path.Target.ModeIndex & 0xffff;
                    // The documented desktop entry is per-path; do not infer
                    // undocumented source/target ID semantics for its header.
                    if (desktop != 0xffff && (desktop >= snapshot.Modes.Length ||
                        snapshot.Modes[desktop].InfoType != 3))
                        throw new InvalidOperationException("desktop-image mode索引越界或类型错误。");
                }
            }
            if (physical != 1 || virtualCount > 1)
                throw new InvalidOperationException("第一版要求恰好一个原active物理target，另可含一个指定虚拟target。");
        }

        internal static void WriteSnapshot(Stream stream, Snapshot snapshot)
        {
            CheckLayout();
            ValidateSnapshot(snapshot);
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(RecoveryMagic); writer.Write(RecoveryVersion);
                writer.Write(72); writer.Write(64);
                writer.Write(snapshot.Paths.Length); writer.Write(snapshot.Modes.Length);
                foreach (PathInfo path in snapshot.Paths) writer.Write(StructBytes(path));
                foreach (ModeInfo mode in snapshot.Modes) writer.Write(StructBytes(mode));
                for (int i = 0; i < snapshot.Paths.Length; i++)
                {
                    WriteText(writer, snapshot.DevicePaths[i]);
                    WriteText(writer, snapshot.AdapterPaths[i]);
                }
            }
        }

        internal static Snapshot ReadSnapshot(Stream stream)
        {
            CheckLayout();
            if (!stream.CanSeek || stream.Length - stream.Position > MaxFileBytes)
                throw new InvalidDataException("恢复文件必须可定位且长度不超过1MiB。");
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                if (reader.ReadUInt64() != RecoveryMagic || reader.ReadUInt32() != RecoveryVersion ||
                    reader.ReadInt32() != 72 || reader.ReadInt32() != 64)
                    throw new InvalidDataException("恢复文件魔数、版本或结构尺寸不匹配。");
                int paths = reader.ReadInt32(), modes = reader.ReadInt32();
                if (paths < 1 || paths > 2 || modes < 1 || modes > MaxModes)
                    throw new InvalidDataException("恢复文件path/mode数量越界。");
                long minimum = (long)paths * 72 + (long)modes * 64 + (long)paths * 8;
                if (stream.Length - stream.Position < minimum) throw new InvalidDataException("恢复文件截断。");
                Snapshot snapshot = new Snapshot { Paths = new PathInfo[paths], Modes = new ModeInfo[modes],
                    DevicePaths = new string[paths], AdapterPaths = new string[paths] };
                for (int i = 0; i < paths; i++) snapshot.Paths[i] = ReadStruct<PathInfo>(reader);
                for (int i = 0; i < modes; i++) snapshot.Modes[i] = ReadStruct<ModeInfo>(reader);
                for (int i = 0; i < paths; i++)
                {
                    snapshot.DevicePaths[i] = ReadText(reader);
                    snapshot.AdapterPaths[i] = ReadText(reader);
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("恢复文件含额外尾部数据。");
                ValidateSnapshot(snapshot);
                return snapshot;
            }
        }

        private static void WriteText(BinaryWriter writer, string value)
        {
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(value);
            if (bytes.Length < 1 || bytes.Length > 2048) throw new InvalidDataException("设备身份字符串长度越界。");
            writer.Write(bytes.Length); writer.Write(bytes);
        }
        private static string ReadText(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 1 || length > 2048) throw new InvalidDataException("设备身份字符串长度越界。");
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new InvalidDataException("设备身份字符串截断。");
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        internal static byte[] StructBytes<T>(T value) where T : struct
        {
            int size = Marshal.SizeOf(typeof(T));
            IntPtr pointer = Marshal.AllocHGlobal(size);
            try
            {
                // All persisted structures are blittable and explicitly laid out;
                // zeroing also keeps any ABI padding deterministic.
                byte[] bytes = new byte[size];
                Marshal.Copy(bytes, 0, pointer, size);
                Marshal.StructureToPtr(value, pointer, false);
                Marshal.Copy(pointer, bytes, 0, size);
                return bytes;
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        private static T ReadStruct<T>(BinaryReader reader) where T : struct
        {
            int size = Marshal.SizeOf(typeof(T));
            byte[] bytes = reader.ReadBytes(size);
            if (bytes.Length != size) throw new InvalidDataException("结构数据截断。");
            IntPtr pointer = Marshal.AllocHGlobal(size);
            try { Marshal.Copy(bytes, 0, pointer, size); return (T)Marshal.PtrToStructure(pointer, typeof(T)); }
            finally { Marshal.FreeHGlobal(pointer); }
        }

        internal static void CheckLayout()
        {
            if (Marshal.SizeOf(typeof(Luid)) != 8 || Marshal.SizeOf(typeof(PathSource)) != 20 ||
                Marshal.SizeOf(typeof(PathTarget)) != 48 || Marshal.SizeOf(typeof(PathInfo)) != 72 ||
                Marshal.SizeOf(typeof(ModeInfo)) != 64 || Marshal.SizeOf(typeof(TargetName)) != 420 ||
                Marshal.SizeOf(typeof(SourceName)) != 84 || Marshal.SizeOf(typeof(AdapterName)) != 276 ||
                Marshal.OffsetOf(typeof(PathInfo), "Target").ToInt32() != 20 ||
                Marshal.OffsetOf(typeof(PathInfo), "Flags").ToInt32() != 68)
                throw new InvalidOperationException("CCD互操作结构尺寸/偏移与Windows ABI不一致，拒绝调用API。");
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Luid { internal uint Low; internal int High; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rational { internal uint Numerator, Denominator; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct PathSource { internal Luid Adapter; internal uint Id, ModeIndex, Status; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct PathTarget
        {
            internal Luid Adapter;
            internal uint Id, ModeIndex, Technology, Rotation, Scaling;
            internal Rational Refresh;
            internal uint ScanLine;
            internal int Available; // Win32 BOOL, not a one-byte bool.
            internal uint Status;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct PathInfo { internal PathSource Source; internal PathTarget Target; internal uint Flags; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct SourceMode { internal uint Width, Height, PixelFormat; internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Signal
        {
            internal ulong PixelRate;
            internal Rational HSync, VSync;
            internal uint ActiveWidth, ActiveHeight, TotalWidth, TotalHeight, VideoStandard, ScanLine;
        }
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        internal struct ModeInfo
        {
            [FieldOffset(0)] internal uint InfoType;
            [FieldOffset(4)] internal uint Id;
            [FieldOffset(8)] internal Luid Adapter;
            [FieldOffset(16)] internal SourceMode Source;
            // Covers the entire 48-byte union, including desktop-image data and
            // source padding when round-tripping a QueryDisplayConfig snapshot.
            [FieldOffset(16)] internal Signal Target;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct DeviceHeader { internal uint Type, Size; internal Luid Adapter; internal uint Id; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct TargetName
        {
            internal DeviceHeader Header;
            internal uint Flags, Technology;
            internal ushort Manufacturer, Product;
            internal uint Connector;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string FriendlyName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DevicePath;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct SourceName
        {
            internal DeviceHeader Header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string GdiName;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct AdapterName
        {
            internal DeviceHeader Header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DevicePath;
        }

        // Official definitions checked against Microsoft Learn:
        // winuser/nf-winuser-{querydisplayconfig,setdisplayconfig,displayconfiggetdeviceinfo}
        // wingdi/ns-wingdi-displayconfig_{path_info,mode_info,target_device_name}
        private static class Native
        {
            [DllImport("user32.dll", ExactSpelling = true)]
            internal static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
            [DllImport("user32.dll", ExactSpelling = true)]
            internal static extern int QueryDisplayConfig(uint flags, ref uint pathCount,
                [Out] PathInfo[] paths, ref uint modeCount, [Out] ModeInfo[] modes, IntPtr topologyId);
            [DllImport("user32.dll", ExactSpelling = true)]
            internal static extern int SetDisplayConfig(uint pathCount, [In] PathInfo[] paths,
                uint modeCount, [In] ModeInfo[] modes, uint flags);
            [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true, CharSet = CharSet.Unicode)]
            internal static extern int GetSourceName(ref SourceName name);
            [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true, CharSet = CharSet.Unicode)]
            internal static extern int GetTargetName(ref TargetName name);
            [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true, CharSet = CharSet.Unicode)]
            internal static extern int GetAdapterName(ref AdapterName name);
        }
    }
}
