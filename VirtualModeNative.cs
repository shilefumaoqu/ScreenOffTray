using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace ScreenOffTray
{
    // No elevated file writer. Unknown/protected third-party configurations stop
    // with an actionable error. Elevation is only Windows' exact restart command.
    internal sealed class VirtualModeNative : IVirtualModePlatform
    {
        private string configPath;
        private VirtualModeRequirement verified;
        public VirtualModeRequirement Read()
        {
            VirtualModeRequirement requirement;
            try { requirement = DisplaySetupProbe.ReadModeRequirement(verified); }
            catch (VirtualModeNotReadyException) {
                // Missing CCD data must not hide a confirmed PnP replacement or
                // driver identity change while waiting for enumeration.
                if (verified != null) VerifyDevice(verified.Instance, verified.DriverKey);
                throw;
            }
            requirement.DriverKey = VerifyDevice(requirement.Instance, verified == null ? null : verified.DriverKey);
            if (verified != null) VirtualModeAdaptation.Check(verified, requirement);
            else verified = requirement;
            return requirement;
        }
        internal static void RequireStarted(uint locate, uint statusResult, uint status, uint problem)
        {
            if (locate != 0 || statusResult != 0) {
                if ((locate == 0 || locate == 13) && (statusResult == 0 || statusResult == 13))
                    throw new VirtualModeNotReadyException("PnP 设备节点尚未就绪 / PnP device node is not ready.");
                throw new InvalidOperationException("无法读取 PnP 设备状态 / Cannot read PnP device status: " + locate + "/" + statusResult);
            }
            if ((status & 0x100) != 0 || problem != 0)
                throw new InvalidOperationException("虚拟设备异常或需要重启 / Virtual device has a problem or requires restart: " + problem);
            if ((status & 8) == 0) throw new VirtualModeNotReadyException("虚拟设备尚未启动 / Virtual device has not started yet.");
        }
        internal static string VerifyDevice(string expected, string expectedDriver = null)
        {
            if (String.IsNullOrEmpty(expected) || !Regex.IsMatch(expected, @"\AROOT\\[A-Za-z0-9_-]+\\[A-Za-z0-9_&-]+\z", RegexOptions.IgnoreCase))
                throw new InvalidOperationException("无法核实精确设备实例 / Cannot verify exact device instance.");
            var found = new List<string>();
            using (var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT")) {
                if (root == null) throw new InvalidOperationException("Root device registry unavailable.");
                foreach (string group in root.GetSubKeyNames()) using (var key = root.OpenSubKey(group)) {
                    if (key == null) continue;
                    foreach (string id in key.GetSubKeyNames()) using (var instance = key.OpenSubKey(id)) {
                        var ids = instance == null ? null : instance.GetValue("HardwareID") as string[];
                        if (ids != null && Array.Exists(ids, delegate(string hw) { return Same(hw, @"Root\MttVDD") || Same(hw, "MttVDD"); }))
                            found.Add(@"ROOT\" + group + "\\" + id);
                    }
                }
            }
            if (found.Count == 0) {
                using (var existing = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + expected)) {
                    var ids = existing == null ? null : existing.GetValue("HardwareID") as string[];
                    if (ids != null && ids.Length > 0 && !Array.Exists(ids, delegate(string id) { return Same(id, @"Root\MttVDD") || Same(id, "MttVDD"); }))
                        throw new InvalidOperationException("原设备硬件身份已变化 / Original device hardware identity changed.");
                }
                throw new VirtualModeNotReadyException("MttVDD 设备尚未枚举 / MttVDD device has not been enumerated yet.");
            }
            if (found.Count != 1 || !Same(found[0], expected)) throw new InvalidOperationException("MttVDD 实例不唯一或身份变化 / MttVDD instance is ambiguous or changed.");
            uint dev, status = 0, problem = 0;
            uint locate = CM_Locate_DevNode(out dev, expected, 0);
            uint statusResult = locate == 0 ? CM_Get_DevNode_Status(out status, out problem, dev, 0) : 0;
            using (var instance = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + expected)) {
                if (instance == null || !Same(Convert.ToString(instance.GetValue("ClassGUID")), "{4d36e968-e325-11ce-bfc1-08002be10318}"))
                    throw new InvalidOperationException("虚拟设备类别不匹配 / Unexpected virtual device class.");
                string driver = Convert.ToString(instance.GetValue("Driver"));
                if (!Regex.IsMatch(driver, @"\A\{4d36e968-e325-11ce-bfc1-08002be10318\}\\[0-9]{4}\z", RegexOptions.IgnoreCase))
                    throw new InvalidOperationException("驱动注册身份无法核实 / Driver registry identity cannot be verified.");
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driver)) {
                    Version version;
                    string provider = key == null ? "" : Convert.ToString(key.GetValue("ProviderName"));
                    string inf = key == null ? "" : Convert.ToString(key.GetValue("InfPath"));
                    string matching = key == null ? "" : Convert.ToString(key.GetValue("MatchingDeviceId"));
                    string text = key == null ? "" : Convert.ToString(key.GetValue("DriverVersion"));
                    if (!Same(provider, "MikeTheTech") || (!Same(matching, @"Root\MttVDD") && !Same(matching, "MttVDD")) ||
                        !Regex.IsMatch(inf, @"\Aoem[0-9]+\.inf\z", RegexOptions.IgnoreCase) || !Version.TryParse(text, out version) || version < new Version("11.30.4.434"))
                        throw new InvalidOperationException("驱动提供商或版本不受支持 / Unsupported driver provider or version.");
                    string identity = driver + "|" + provider + "|" + inf + "|" + text;
                    if (expectedDriver != null && !Same(expectedDriver, identity)) throw new InvalidOperationException("重载期间驱动身份已变化 / Driver identity changed during reload.");
                    RequireStarted(locate, statusResult, status, problem);
                    return identity;
                }
            }
        }
        private static bool Same(string a, string b) { return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        internal static string ResolvePath()
        {
            string directory = @"C:\VirtualDisplayDriver";
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = hklm.OpenSubKey(@"SOFTWARE\MikeTheTech\VirtualDisplayDriver")) {
                if (key != null && Array.Exists(key.GetValueNames(), delegate(string name) { return Same(name, "VDDPATH"); })) {
                    var kind = key.GetValueKind("VDDPATH");
                    if (kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString) throw new InvalidOperationException("VDDPATH is not a path.");
                    directory = key.GetValue("VDDPATH", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    if (directory != null && kind == RegistryValueKind.ExpandString) directory = Environment.ExpandEnvironmentVariables(directory);
                }
            }
            return ConfigFile(directory);
        }
        internal static string ConfigFile(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory) || !Regex.IsMatch(directory, @"\A[A-Za-z]:\\") || directory.IndexOf(':', 2) >= 0 || directory.IndexOf('%') >= 0 || directory.IndexOf('/') >= 0)
                throw new InvalidOperationException("虚拟屏配置路径未知或不受支持 / Unknown or unsupported virtual display configuration path.");
            string canonical = Path.GetFullPath(directory).TrimEnd('\\');
            if (!Same(canonical, directory.TrimEnd('\\')) || canonical.Length <= 3)
                throw new InvalidOperationException("配置路径不是规范本地目录 / Configuration path is not a canonical local directory.");
            string file = Path.Combine(canonical, "vdd_settings.xml"); NoReparse(file);
            if (!File.Exists(file)) throw new InvalidOperationException("虚拟屏配置文件不存在，保留现场 / Virtual display configuration is missing; preserving existing state.");
            return file;
        }
        internal static void NoReparse(string path)
        {
            for (string item = Path.GetFullPath(path); !String.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
                if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("拒绝重解析配置路径 / Reparse configuration path refused: " + item);
        }
        private string CurrentPath()
        {
            string current = ResolvePath();
            if (configPath != null && !Same(current, configPath)) throw new InvalidOperationException("VDDPATH changed externally; preserving configuration.");
            configPath = current; return current;
        }
        internal static byte[] Bytes(FileStream stream)
        {
            if (stream.Length <= 0 || stream.Length > VirtualModeConfig.Limit) throw new InvalidOperationException("Invalid virtual configuration size.");
            byte[] value = new byte[(int)stream.Length]; stream.Position = 0; int offset = 0;
            while (offset < value.Length) { int count = stream.Read(value, offset, value.Length - offset); if (count == 0) throw new EndOfStreamException(); offset += count; }
            return value;
        }
        public byte[] ReadConfig()
        {
            using (var stream = new FileStream(CurrentPath(), FileMode.Open, FileAccess.Read, FileShare.Read)) return Bytes(stream);
        }
        private static byte[] ReadDuringReplace(string file)
        { using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) return Bytes(stream); }
        internal static string WriteConfig(string file, string journalDirectory, byte[] original, byte[] desired, VirtualModeRequirement requirement, Action verify)
        { return WriteConfig(file, journalDirectory, original, desired, requirement, verify, delegate(string staged, string target, string replaced) { File.Replace(staged, target, replaced, false); }); }
        // Injectable replace is used only by isolated file-system failure tests.
        internal static string WriteConfig(string file, string journalDirectory, byte[] original, byte[] desired, VirtualModeRequirement requirement, Action verify, Action<string, string, string> replace)
        {
            NoReparse(file);
            // Hold writers/renames through staging and the last identity/byte
            // check, then release for File.Replace. Windows has no file-content
            // CAS: retain/check the ACTUAL replaced bytes to detect the final race.
            // ReadWrite checks ordinary write permission on the original. No
            // bytes are written through this handle; only the staged file is written.
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                if (!VirtualModeConfig.Same(original, Bytes(stream))) throw new InvalidOperationException("配置已被外部修改 / Configuration changed externally.");
                verify();
                if (VirtualModeConfig.Same(original, desired)) return "XML already contains the exact mode; no XML write. A single explicit reload is still required because Windows has not listed it.";
                if ((File.GetAttributes(file) & FileAttributes.ReadOnly) != 0) throw new UnauthorizedAccessException("Configuration is read-only.");
                NoReparse(journalDirectory); Directory.CreateDirectory(journalDirectory);
                string token = Guid.NewGuid().ToString("N");
                string record = Path.Combine(journalDirectory, token);
                string staged = Path.Combine(Path.GetDirectoryName(file), ".ScreenOffTray-" + token + ".tmp");
                string replaced = Path.Combine(Path.GetDirectoryName(file), ".ScreenOffTray-" + token + ".replaced.xml");
                using (var backup = new FileStream(record + ".original.xml", FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    backup.Write(original, 0, original.Length); backup.Flush(true);
                }
                File.WriteAllText(record + ".txt", "path=" + file + "\r\nmode=" + requirement.Width + "x" + requirement.Height + "\r\nrotation=" + requirement.Rotation +
                    "\r\ninstance=" + requirement.Instance + "\r\nactual_replaced_backup=" + replaced + "\r\nshared_ownership_unchanged=true\r\nstatus=write-intent\r\n", Encoding.UTF8);
                bool replacementStarted = false, replacementReturned = false;
                try {
                    using (var temp = new FileStream(staged, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough)) {
                        temp.Write(desired, 0, desired.Length); temp.Flush(true);
                        byte[] readback = Bytes(temp);
                        if (!VirtualModeConfig.Same(desired, readback) || !VirtualModeConfig.Same(readback, VirtualModeConfig.Add(readback, requirement.Width, requirement.Height)))
                            throw new IOException("Staged configuration XML/readback differs.");
                    }
                    verify(); NoReparse(file); NoReparse(staged);
                    if (!VirtualModeConfig.Same(original, Bytes(stream))) throw new InvalidOperationException("配置在替换前被外部修改 / Configuration changed externally before replacement.");
                    stream.Dispose(); // The only unlocked boundary; actual backup is checked below.
                    replacementStarted = true;
                    replace(staged, file, replaced);
                    replacementReturned = true;
                    NoReparse(file); NoReparse(replaced);
                    if (!VirtualModeConfig.Same(original, ReadDuringReplace(replaced)) || !VirtualModeConfig.Same(desired, ReadDuringReplace(file)))
                        throw new IOException("替换期间检测到外部配置更新，保留实际被替换文件；不会重载或自动回滚 / External configuration update detected during replacement; actual replaced file retained. No reload or automatic rollback.");
                    File.AppendAllText(record + ".txt", "status=atomically-replaced-and-readback-verified\r\n", Encoding.UTF8);
                    return "Configuration atomically replaced and read back; original and change record: " + record + "; actual replaced backup: " + replaced;
                } catch (Exception ex) {
                    if (replacementStarted) {
                        string status = replacementReturned ? "XML 已原子替换，后续核验或记录未完成 / XML was atomically replaced; subsequent verification or logging incomplete." :
                            "已尝试原子替换，XML 提交状态未确认 / Atomic replacement was attempted; XML commit status is unconfirmed.";
                        throw new IOException(status + " 不重载、不自动回滚 / No reload or automatic rollback. Record: " + record + "; actual replaced backup: " + replaced + "; " + ex.Message, ex);
                    }
                    if (ex is UnauthorizedAccessException) throw new UnauthorizedAccessException("配置目录受到保护，未完成原子替换；记录 / Configuration directory is protected; atomic replacement incomplete. Record: " + record, ex);
                    throw new IOException("模式配置提交未完成；保留原件与实际替换备份，不重载 / Mode configuration commit incomplete; original and actual replacement backup retained, no reload. Record: " + record + "; " + ex.Message, ex);
                } finally {
                    // Only our uncommitted staging file. Never restore/delete a
                    // destination or actual replaced backup after this boundary.
                    try { if (File.Exists(staged)) File.Delete(staged); } catch { }
                }
            }
        }
        public string Commit(byte[] original, byte[] desired, VirtualModeRequirement requirement)
        {
            try {
                string file = CurrentPath();
                return WriteConfig(file, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenOffTray", "模式适配记录"),
                    original, desired, requirement, delegate { VirtualModeAdaptation.Check(requirement, Read()); CurrentPath(); });
            } catch (UnauthorizedAccessException ex) {
                throw new InvalidOperationException("配置受到保护，未写入也未重载。请由配置所有者补充精确模式，再重新检查；本工具不会提权写入共享配置 / Configuration is protected; no write or reload. Ask its owner to add the exact mode and check again. No elevated shared-file writer is provided.", ex);
            }
        }
        public void Restart(VirtualModeRequirement requirement)
        {
            VirtualModeAdaptation.Check(requirement, Read()); CurrentPath();
            string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.Is64BitProcess ? @"System32\pnputil.exe" : @"Sysnative\pnputil.exe");
            try {
                using (var process = Process.Start(new ProcessStartInfo(executable, "/restart-device \"" + requirement.Instance + "\"") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })) {
                    if (process == null) throw new InvalidOperationException("Cannot observe device restart process.");
                    // Worker only; never terminate a pending UAC/device operation.
                    process.WaitForExit();
                    if (process.ExitCode == 3010 || process.ExitCode == 1641)
                        throw new InvalidOperationException("设备要求重启，准备未完成；不会自动重启 / Device requires reboot; preparation incomplete. No automatic reboot.");
                    if (process.ExitCode != 0) throw new InvalidOperationException("设备重载失败 / Device restart failed: " + process.ExitCode);
                }
            } catch (Win32Exception ex) {
                if (ex.NativeErrorCode == 1223) throw new OperationCanceledException("管理员确认已取消，未完成设备重载 / Administrator confirmation cancelled; device restart did not complete.", ex);
                throw;
            }
        }
        public void Delay() { Thread.Sleep(250); }
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Locate_DevNodeW")] private static extern uint CM_Locate_DevNode(out uint dev, string id, uint flags);
        [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint dev, uint flags);
    }
}
