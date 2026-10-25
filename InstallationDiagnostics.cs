using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using OledPrototype;

namespace ScreenOffTray
{
    // Read-only display preparation, per-user file IO, and an UNARMED recovery child.
    // This mode never registers input hooks or invokes TurnOff/Recover/APPLY.
    internal static class InstallationDiagnostics
    {
        internal static int Run(string reportPath, string snapshotFixture)
        {
            var result = new Dictionary<string, object>();
            string checkDir = Path.Combine(OledPrototype.Program.Runtime, "安装检查-" + Guid.NewGuid().ToString("N"));
            try
            {
                string local = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                string runtime = Path.GetFullPath(OledPrototype.Program.Runtime);
                if (!runtime.StartsWith(local.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Runtime is not under LocalAppData.");
                Directory.CreateDirectory(checkDir);
                string sentinel = Path.Combine(checkDir, "带空格 路径检查.txt");
                File.WriteAllText(sentinel, "ScreenOffTray 0.1.0 路径检查", Encoding.UTF8);
                result["runtime_write_read"] = File.ReadAllText(sentinel, Encoding.UTF8) == "ScreenOffTray 0.1.0 路径检查";
                result["runtime_directory"] = runtime;
                result["executable"] = Process.GetCurrentProcess().MainModule.FileName;
                result["working_directory"] = Environment.CurrentDirectory;
                result["administrator"] = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

                // The child receives a real quoted path and signals ready without arming.
                var guardian = new Guardian(sentinel, 0);
                try
                {
                    result["unarmed_recovery_child_ready"] = guardian.IsAlive;
                    guardian.Pulse();
                }
                finally { guardian.Stop(); }

                if (snapshotFixture != null)
                {
                    DisplayBackend.Snapshot saved;
                    using (var stream = File.OpenRead(snapshotFixture)) saved = DisplayBackend.ReadSnapshot(stream);
                    string copy = Path.Combine(checkDir, "恢复 快照.bin");
                    using (var stream = File.Create(copy)) DisplayBackend.WriteSnapshot(stream, saved);
                    using (var stream = File.OpenRead(copy)) DisplayBackend.ReadSnapshot(stream);
                    result["snapshot_write_parse"] = true;
                    result["snapshot_source"] = "saved_fixture_replay_without_display_access";
                }
                else ProbeSnapshot(checkDir, result);
                result["display_apply_performed"] = false;
                result["ok"] = (bool)result["runtime_write_read"] && (bool)result["unarmed_recovery_child_ready"] &&
                    (bool)result["snapshot_write_parse"];
            }
            catch (Exception ex) { result["ok"] = false; result["error"] = ex.Message; }
            finally
            {
                // Only this invocation's generated files are eligible for cleanup.
                try
                {
                    File.Delete(Path.Combine(checkDir, "带空格 路径检查.txt"));
                    File.Delete(Path.Combine(checkDir, "恢复 快照.bin"));
                    Directory.Delete(checkDir, false);
                }
                catch { }
            }
            File.WriteAllText(Path.GetFullPath(reportPath), new JavaScriptSerializer().Serialize(result), Encoding.UTF8);
            return (bool)result["ok"] ? 0 : 1;
        }

        private static void ProbeSnapshot(string checkDir, Dictionary<string, object> result)
        {
            List<DisplayTarget> targets = DisplayBackend.ListTargets();
            List<DisplayTarget> mtt = targets.FindAll(delegate(DisplayTarget t) {
                if (!t.Available) return false;
                string instance = DisplayBackend.RootInstancePath(t.AdapterPath);
                if (instance == null) return false;
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + instance))
                {
                    string[] ids = key == null ? null : key.GetValue("HardwareID") as string[];
                    if (ids == null) return false;
                    foreach (string id in ids)
                        if (String.Equals(id, @"Root\MttVDD", StringComparison.OrdinalIgnoreCase) ||
                            String.Equals(id, "MttVDD", StringComparison.OrdinalIgnoreCase)) return true;
                    return false;
                }
            });
            if (mtt.Count == 1)
            {
                try
                {
                    var backend = new DisplayBackend();
                    backend.Prepare(mtt[0].DevicePath);
                    string snapshot = Path.Combine(checkDir, "恢复 快照.bin");
                    backend.SaveRecovery(snapshot);
                    using (var stream = File.OpenRead(snapshot)) DisplayBackend.ReadSnapshot(stream);
                    result["snapshot_write_parse"] = true;
                }
                catch (Exception ex)
                {
                    result["snapshot_write_parse"] = false;
                    result["snapshot_error"] = ex.Message;
                }
            }
            else
            {
                result["snapshot_write_parse"] = false;
                result["snapshot_error"] = "A unique available MttVDD target was not found.";
            }
        }
    }
}
