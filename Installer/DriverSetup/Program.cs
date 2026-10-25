using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using OledPrototype;

namespace ScreenOffTray.DriverSetup
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            try
            {
                if (args.Length == 1 && args[0] == "--self-test") { SelfTest.Run(); return 0; }
                if (!Environment.Is64BitProcess) throw new InvalidOperationException("x64 helper required");
                if (args.Length == 1 && args[0] == "--probe") return Probe();
                if (args.Length != 3 || (args[0] != "install" && args[0] != "uninstall") || args[1] != "--bundle-dir" || !Path.IsPathRooted(args[2]))
                {
                    Output(new { status = "usage", contract = "--probe | --self-test | install --bundle-dir ABSOLUTE_DRIVER_DIRECTORY | uninstall --bundle-dir ABSOLUTE_DRIVER_DIRECTORY" }); return 2;
                }
                if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new UnauthorizedAccessException("Run install/uninstall elevated; probe/self-test require no elevation");
                // Both operations require the original recovery payload to remain available.
                string bundle = Path.GetFullPath(args[2]); Policy.Bundle(bundle);
                using (StateStore store = new StateStore(StateStore.DefaultRoot))
                {
                    Receipt receipt = StateStore.Read(store.Root);
                    return args[0] == "install" ? Install(bundle, store, receipt) : Uninstall(store, receipt);
                }
            }
            catch (UnauthorizedAccessException ex) { Output(new { status = "permission-error", error = ex.Message }); return 5; }
            catch (Exception ex) { Output(new { status = "failed", error = ex.Message, recovery = StateStore.DefaultRoot }); return 20; }
        }
        internal static void Output(object value) { Console.WriteLine(new JavaScriptSerializer().Serialize(value)); }
        private static int Probe()
        {
            List<Device> items = Native.Enumerate();
            Receipt r = StateStore.Read(StateStore.DefaultRoot);
            string topologyError = null; uint width = 0, height = 0;
            try { Topology.Resolution(Topology.Capture(), out width, out height); }
            catch (Exception ex) { topologyError = ex.Message; }
            Output(new { schema = Policy.Product, readOnly = true, selection = Policy.Select(items), devices = items,
                receipt = r, receiptPath = Path.Combine(StateStore.DefaultRoot, "receipt.json"), physicalWidth = width,
                physicalHeight = height, freshInstallTopologyError = topologyError });
            return 0;
        }
        private static int Install(string bundle, StateStore store, Receipt previous)
        {
            List<Device> items = Native.Enumerate();
            if (previous != null && previous.Owned && previous.Phase != "removed" && previous.Phase != "rolled-back")
            {
                if ((previous.Phase != "installed" && previous.Phase != "reboot-pending") || items.Count != 1) throw new InvalidOperationException("Pending/changed owned installation; uninstall/recover it before retrying");
                Policy.AssertOwned(previous, items[0]);
                if (Policy.Select(items) != "reuse") throw new InvalidOperationException("Owned driver is not usable");
                store.RequireOwnedConfiguration(previous);
                if (previous.Phase == "reboot-pending")
                {
                    DisplayBackend.Snapshot current = Topology.Capture();
                    foreach (string adapter in current.AdapterPaths)
                        if (Policy.Same(DisplayBackend.RootInstancePath(adapter), previous.InstanceId))
                            throw new InvalidOperationException("Pending driver is active in desktop layout; restore physical-only layout first");
                    previous.Phase = "installed"; previous.NeedsReboot = false; store.Save(previous);
                }
                Output(new { status = "already-owned", instanceId = previous.InstanceId, needsReboot = previous.NeedsReboot });
                return previous.NeedsReboot ? 3010 : 0;
            }
            string selection = Policy.Select(items);
            if (selection == "conflict") throw new InvalidOperationException("Exact MttVDD hardware ID is ambiguous, nonpresent, incompatible or unusable; no changes made");
            Receipt r = new Receipt { Token = Guid.NewGuid().ToString("N"), Phase = "reused", Owned = false };
            if (selection == "reuse")
            {
                r.InstanceId = items[0].InstanceId;
                store.Save(r);
                Output(new { status = "reused", instanceId = r.InstanceId, owned = false, configUntouched = true }); return 0;
            }
            DisplayBackend.Snapshot baseline = Topology.Capture();
            Topology.Resolution(baseline, out r.Width, out r.Height);
            r.Owned = true; r.Marker = "ScreenOffTray:" + r.Token;
            r.ConfigDirectory = Path.Combine(store.Root, "Config-" + r.Token); r.Phase = "prepared";
            store.Save(r); Topology.Save(store.SnapshotPath(r), baseline);
            try
            {
                store.SaveRecoveryPayload(r, bundle);
                store.PrepareConfig(r);
                // Bind exclusively from the protected, re-hashed copy, closing the
                // hash-to-use race when an admin supplies a user-writable bundle path.
                Native.Create(Path.Combine(store.RecoveryPath(r), "MttVDD.inf"), r, store);
                // Give PnP a bounded chance to expose a usable CCD target. Never toggle or enable displays.
                Device device = null;
                for (int i = 0; i < 20; i++)
                {
                    items = Native.Enumerate();
                    if (items.Count != 1 || !Policy.Same(items[0].InstanceId, r.InstanceId)) throw new InvalidOperationException("MttVDD identity collision after creation");
                    Policy.AssertOwned(r, items[0]); device = items[0];
                    if (Policy.Select(items) == "reuse") break;
                    if (i != 19) Thread.Sleep(250);
                }
                // Restore even when binding requested reboot or the new output is unusable.
                Topology.Restore(store.SnapshotPath(r));
                if (device == null || !Policy.Compatible(device)) throw new InvalidOperationException("Selected driver does not satisfy compatibility policy");
                r.BoundInf = device.Inf; r.BoundVersion = device.Version;
                if (Policy.Select(items) != "reuse" && !r.NeedsReboot) throw new InvalidOperationException("New driver has no usable target");
                r.Phase = r.NeedsReboot ? "reboot-pending" : "installed"; store.Save(r);
                Output(new { status = r.Phase, owned = true, instanceId = r.InstanceId, needsReboot = r.NeedsReboot, layoutRestored = true });
                return r.NeedsReboot ? 3010 : 0;
            }
            catch (Exception installError)
            {
                throw RollbackInstall(r, installError, Native.Enumerate, Native.Remove,
                    delegate { Topology.Restore(store.SnapshotPath(r)); },
                    delegate { store.RestoreRegistry(r); }, store.Save);
            }
        }
        // Production rollback coordinator; delegates also allow pure fault injection.
        // Bounded order: diagnostic save, exact owned removal, baseline restore (always),
        // then configuration restore only after removal/layout success and no shared user.
        internal static InvalidOperationException RollbackInstall(Receipt r, Exception installError,
            Func<List<Device>> enumerate, Func<Device, bool> remove, Action restoreLayout,
            Action restoreRegistry, Action<Receipt> save)
        {
            r.Error = installError.Message; r.Phase = "failed";
            SaveRollbackDiagnostic(r, save);
            bool deviceSafe = false, layoutRestored = false;
            try
            {
                Device own = enumerate().Find(delegate(Device d) { return Policy.Same(d.InstanceId, r.InstanceId); });
                if (own != null)
                {
                    Policy.AssertOwned(r, own);
                    r.NeedsReboot |= remove(own);
                    if (enumerate().Exists(delegate(Device d) { return Policy.Same(d.InstanceId, r.InstanceId); }))
                        throw new InvalidOperationException("Own rollback removal pending/failed");
                }
                deviceSafe = true;
            }
            catch (Exception ex) { r.Error += "; rollback: " + ex.Message; }
            // Neither a journal failure nor a removal/ownership/enumeration failure may skip this.
            try { restoreLayout(); layoutRestored = true; r.Error += "; original layout restored"; }
            catch (Exception ex) { r.Error += "; layout: " + ex.Message; }
            if (deviceSafe && layoutRestored)
            {
                try
                {
                    if (enumerate().Count != 0) throw new InvalidOperationException("Another MttVDD now uses global configuration");
                    restoreRegistry(); r.Phase = "rolled-back";
                }
                catch (Exception ex) { r.Error += "; rollback configuration: " + ex.Message; }
            }
            SaveRollbackDiagnostic(r, save);
            return new InvalidOperationException(r.Error + "; recovery phase=" + r.Phase + "; materials retained", installError);
        }
        private static void SaveRollbackDiagnostic(Receipt r, Action<Receipt> save)
        {
            // Only failure diagnostics are best-effort. All mutation-intent saves stay fail-closed.
            try { save(r); }
            catch (Exception ex) { r.Error += "; rollback journal: " + ex.Message; }
        }
        private static int Uninstall(StateStore store, Receipt r)
        {
            if (r == null) { Output(new { status = "no-receipt", removed = false }); return 0; }
            if (!r.Owned) { Output(new { status = "reused-preserved", removed = false, instanceId = r.InstanceId }); return 0; }
            if (r.Phase == "removed" || r.Phase == "rolled-back") { Output(new { status = r.Phase, removed = true }); return 0; }
            List<Device> items = Native.Enumerate();
            foreach (Device d in items)
            {
                if (!Policy.Same(d.InstanceId, r.InstanceId)) throw new InvalidOperationException("Another MttVDD instance uses shared configuration; preserving all recovery material");
                Policy.AssertOwned(r, d);
            }
            store.CheckRegistry(r);
            // Current layout is read before removal. Refuse virtual-only or unknown physical topology.
            DisplayBackend.Snapshot current = Topology.Capture();
            uint w, h; Topology.Resolution(current, out w, out h);
            // A normal installed device may be removed only while its output is inactive.
            // This prevents uninstall from removing the user's current desktop target.
            foreach (string adapter in current.AdapterPaths)
                if (Policy.Same(DisplayBackend.RootInstancePath(adapter), r.InstanceId))
                    throw new InvalidOperationException("Owned virtual output is active; restore the physical-only desktop before uninstall");
            r.Phase = "remove-intent"; store.Save(r);
            if (items.Count == 1) r.NeedsReboot |= Native.Remove(items[0]);
            items = Native.Enumerate();
            if (items.Count != 0)
            {
                r.Phase = "remove-pending"; store.Save(r);
                throw new InvalidOperationException("Device removal pending/failed; reboot manually if requested, then retry uninstall; files retained");
            }
            DisplayBackend.VerifySnapshot(current, Topology.Capture());
            store.RestoreRegistry(r);
            r.Phase = "removed"; store.Save(r);
            Output(new { status = "removed", instanceId = r.InstanceId, needsReboot = r.NeedsReboot, driverStorePackageRetained = true, userSettingsRetained = true });
            // Reboot requests retain installer recovery material until a later clean retry.
            if (r.NeedsReboot) { r.NeedsReboot = false; store.Save(r); return 3010; }
            return 0;
        }
    }
}
