using System;
using System.Threading;
using System.Threading.Tasks;
using OledPrototype;

namespace ScreenOffTray
{
    // Only explicit enumeration/device-start gaps are retryable, and only in
    // the bounded post-restart loop. Permission, configuration and identity
    // errors retain their original types and fail immediately.
    internal sealed class VirtualModeNotReadyException : InvalidOperationException
    {
        internal VirtualModeNotReadyException(string message) : base(message) { }
        internal static Exception CcdError(string operation, params int[] errors)
        {
            bool temporary = false;
            foreach (int error in errors) {
                if (error == 0) continue;
                if (error != 21 && error != 122 && error != 1167) return new InvalidOperationException(operation + ": " + error);
                temporary = true;
            }
            return temporary ? (Exception)new VirtualModeNotReadyException(operation + ": display enumeration is not ready.") : new InvalidOperationException(operation);
        }
    }
    internal sealed class VirtualModeRequirement
    {
        internal uint Width, Height, Rotation;
        internal string PhysicalKey, VirtualDevice, Instance, DriverKey;
        internal bool Listed;
        internal string Key { get { return PhysicalKey + "|" + Width + "x" + Height + "|" + Rotation + "|" + VirtualDevice + "|" + Instance + "|" + DriverKey; } }
    }
    internal interface IVirtualModePlatform
    {
        VirtualModeRequirement Read();
        byte[] ReadConfig();
        string Commit(byte[] original, byte[] desired, VirtualModeRequirement requirement);
        void Restart(VirtualModeRequirement requirement);
        void Delay();
    }
    internal sealed class VirtualModeAdaptationResult
    {
        internal bool Success, Changed, Reloaded;
        internal string Diagnostics = "", Error = "";
        internal VirtualModeRequirement Requirement;
        internal Action Verify;
    }
    internal static class VirtualModeAdaptation
    {
        internal static void Check(VirtualModeRequirement expected, VirtualModeRequirement current)
        {
            if (expected == null || current == null || !String.Equals(expected.Key, current.Key, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("实体屏尺寸、方向或设备身份已变化，请重新准备 / Physical display dimensions, rotation or device identity changed; prepare again.");
        }
        private static void Cancel(Func<bool> cancelled)
        { if (cancelled()) throw new OperationCanceledException("适配已取消；已写入的模式会保留，关屏保持停用 / Adaptation cancelled; any added mode is retained, display off remains disabled."); }
        internal static VirtualModeAdaptationResult Run(IVirtualModePlatform platform, Func<bool> cancelled)
        {
            var result = new VirtualModeAdaptationResult();
            try {
                Cancel(cancelled);
                var initial = platform.Read(); result.Requirement = initial;
                result.Verify = delegate {
                    Cancel(cancelled); var current = platform.Read(); Check(initial, current);
                    if (!current.Listed) throw new InvalidOperationException("虚拟模式列表已变化，请重新检查 / Virtual mode list changed; check again.");
                };
                VirtualModeConfig.Dimensions(initial.Width, initial.Height);
                if (initial.Listed) {
                    Cancel(cancelled); var current = platform.Read(); Check(initial, current);
                    if (!current.Listed) throw new InvalidOperationException("虚拟模式列表已变化，请重新检查 / Virtual mode list changed; check again.");
                    result.Success = true;
                    result.Diagnostics = "Exact live virtual mode already listed; no configuration access or device reload."; return result;
                }
                byte[] original = platform.ReadConfig();
                byte[] desired = VirtualModeConfig.Add(original, initial.Width, initial.Height);
                Cancel(cancelled); Check(initial, platform.Read());
                if (!VirtualModeConfig.Same(original, desired)) {
                    result.Diagnostics = platform.Commit(original, desired, initial);
                    result.Changed = true;
                } else {
                    result.Diagnostics = "XML already contains the exact mode; no commit or write-permission check. A single explicit reload is required because Windows has not listed it.";
                }
                Cancel(cancelled); Check(initial, platform.Read());
                if (!VirtualModeConfig.Same(desired, platform.ReadConfig())) throw new InvalidOperationException("配置被外部修改，停止重载 / Configuration changed externally; reload stopped.");
                Cancel(cancelled);
                // No old CCD snapshot or setup guardian exists across this call.
                // The caller keeps its gate until even a cancelled restart returns.
                platform.Restart(initial); result.Reloaded = true;
                string lastNotReady = "";
                for (int n = 0; n < 40; n++) {
                    Cancel(cancelled);
                    VirtualModeRequirement current = null;
                    bool enumerationPending = false;
                    try { current = platform.Read(); }
                    catch (VirtualModeNotReadyException ex) { enumerationPending = true; lastNotReady = ex.Message; }
                    Cancel(cancelled);
                    if (!enumerationPending) Check(initial, current);
                    // Configuration/permission failures are never retried even
                    // while CCD or PnP is temporarily unable to enumerate.
                    if (!VirtualModeConfig.Same(desired, platform.ReadConfig())) throw new InvalidOperationException("重载期间配置被外部修改 / Configuration changed externally during reload.");
                    Cancel(cancelled);
                    if (current != null && current.Listed) {
                        result.Requirement = current; result.Success = true;
                        result.Diagnostics += "\r\nFresh device identity and exact virtual mode verified after reload. Global refresh rates preserved; DPI remains unproven.";
                        return result;
                    }
                    if (n < 39) platform.Delay();
                }
                throw new InvalidOperationException("重载后精确模式未生效或枚举仍未就绪；有界等待已结束，不会再次重载或重启 / Exact mode or display enumeration did not become ready within the bounded wait; no further reload or reboot. " + lastNotReady);
            } catch (Exception ex) {
                result.Error = ex.Message; result.Diagnostics += "\r\n" + ex;
                return result;
            }
        }
    }
    // Delegates recovery unchanged; the extra read-only preflight joins the
    // mode operation to a newly captured plan, without carrying any CCD arrays.
    internal sealed class ModeCheckedAutoSetup : IAutoSetupPlatform, IAutoSetupRecoveryCompletion
    {
        private readonly IAutoSetupPlatform inner;
        private readonly VirtualModeRequirement requirement;
        private readonly Action verify;
        internal ModeCheckedAutoSetup(IAutoSetupPlatform inner, VirtualModeRequirement requirement, Action verify)
        { this.inner = inner; this.requirement = requirement; this.verify = verify; }
        public AutoSetupPlan PlanCheck()
        {
            verify(); var plan = inner.PlanCheck(); verify();
            var physical = plan.PhysicalOnly;
            var source = DisplayAutoCcd.Source(physical, physical.Paths[0]);
            if (source.Width != requirement.Width || source.Height != requirement.Height || physical.Paths[0].Target.Rotation != requirement.Rotation ||
                !requirement.PhysicalKey.StartsWith(physical.DevicePaths[0] + "|", StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(plan.VirtualOnly.DevicePath, requirement.VirtualDevice, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("显示屏在适配后变化，停止准备 / Display changed after mode adaptation; preparation stopped.");
            return plan;
        }
        public DisplayBackend.Snapshot Capture() { return inner.Capture(); }
        public void Validate(DisplayBackend.Snapshot value) { inner.Validate(value); }
        public void Apply(DisplayBackend.Snapshot expected, DisplayBackend.Snapshot desired, Action beforeApply) { inner.Apply(expected, desired, beforeApply); }
        public DisplaySetupResult Probe() { return inner.Probe(); }
        public string SaveRecovery(DisplayBackend.Snapshot original) { return inner.SaveRecovery(original); }
        public IAutoSetupGuardian StartGuardian(string path) { return inner.StartGuardian(path); }
        public void Record(string path, string detail) { inner.Record(path, detail); }
        public Task<bool> CompleteRecoveryAsync(Task<bool> stopped, bool safe)
        { var completion = inner as IAutoSetupRecoveryCompletion; return completion == null ? Task.FromResult(false) : completion.CompleteRecoveryAsync(stopped, safe); }
    }
}
