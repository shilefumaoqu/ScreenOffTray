using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace ScreenOffTray.DriverSetup
{
    internal static class Policy
    {
        internal const string Product = "ScreenOffTray.DriverSetup/1";
        internal const string RegistryPath = @"SOFTWARE\MikeTheTech\VirtualDisplayDriver";
        internal static readonly Version Minimum = new Version("11.30.4.434");
        internal static bool Same(string a, string b) { return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        internal static bool Exact(string[] ids)
        {
            if (ids == null) return false;
            foreach (string id in ids) if (Same(id, @"Root\MttVDD") || Same(id, "MttVDD")) return true;
            return false;
        }
        internal static bool Compatible(Device d)
        {
            Version v;
            return Exact(d.HardwareIds) && Same(d.ClassGuid, Native.DisplayClass.ToString("B")) &&
                Same(d.Provider, "MikeTheTech") && Exact(new string[] { d.MatchingId }) &&
                Version.TryParse(d.Version, out v) && v >= Minimum && !String.IsNullOrWhiteSpace(d.Inf);
        }
        internal static string Select(List<Device> items)
        {
            if (items.Count == 0) return "create";
            if (items.Count == 1 && items[0].Present && items[0].Started && items[0].Problem == 0 &&
                items[0].Available && Compatible(items[0])) return "reuse";
            return "conflict";
        }
        internal static void AssertOwned(Receipt r, Device d)
        {
            if (r == null || !r.Owned || String.IsNullOrEmpty(r.Token) ||
                !Same(r.InstanceId, d.InstanceId) || !Same(r.Marker, d.Marker) ||
                !Same(r.Marker, "ScreenOffTray:" + r.Token) || !Exact(d.HardwareIds) ||
                !Same(d.ClassGuid, Native.DisplayClass.ToString("B")) ||
                (!String.IsNullOrEmpty(r.BoundInf) && (!Same(r.BoundInf, d.Inf) || !Same(r.BoundVersion, d.Version) || !Compatible(d))))
                throw new InvalidOperationException("Owned device identity/driver changed; refusing removal");
        }
        internal static string Config(uint width, uint height)
        {
            if (width < 320 || height < 200 || width > 32768 || height > 32768) throw new InvalidOperationException("Invalid physical source resolution");
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<vdd_settings><monitors><count>1</count></monitors>" +
                "<gpu><friendlyname>default</friendlyname></gpu><global><g_refresh_rate>60</g_refresh_rate></global>" +
                "<resolutions><resolution><width>" + width + "</width><height>" + height +
                "</height><refresh_rate>60</refresh_rate></resolution></resolutions>" +
                "<options><CustomEdid>false</CustomEdid><PreventSpoof>false</PreventSpoof><EdidCeaOverride>false</EdidCeaOverride>" +
                "<HardwareCursor>true</HardwareCursor><SDR10bit>false</SDR10bit><HDRPlus>false</HDRPlus>" +
                "<logging>false</logging><debuglogging>false</debuglogging></options></vdd_settings>\n";
        }
        internal static string Hash(string path)
        {
            using (SHA256 algorithm = SHA256.Create()) using (FileStream s = File.OpenRead(path))
                return BitConverter.ToString(algorithm.ComputeHash(s)).Replace("-", "");
        }
        internal static void Bundle(string root)
        {
            string[] names = { "MttVDD.inf", "MttVDD.dll", "mttvdd.cat" };
            string[] hashes = {
                "550D211FE481E74DFE3F9D724ED78BE48B3A9113405965D683D9373E8D672F5D",
                "C9CA837F57A98FBD43BC416A7F535A95843626E7759EAF85CF0CD7CE334DBB05",
                "08A0093FC9B2E32B287A6F8A77CA4DE0A31830D29FC33D2B13A918DC859468F6" };
            for (int i = 0; i < names.Length; i++)
            {
                string file = Path.Combine(root, names[i]); StateStore.NoReparse(file);
                if (!Same(Hash(file), hashes[i])) throw new InvalidOperationException("Pinned driver payload mismatch: " + names[i]);
            }
        }
    }
    // Journal is deliberately retained after uninstall; it is never a receipt for reused hardware.
    public sealed class Receipt
    {
        public string Schema = Policy.Product, Token, Phase, InstanceId, Marker, BoundInf, BoundVersion;
        public bool Owned, NeedsReboot, RegistryIntent, PriorKey, PriorValue;
        public string PriorKind, PriorData, ConfigDirectory, ConfigHash, Error;
        public uint Width, Height;
    }
    internal sealed class StateStore : IDisposable
    {
        internal readonly string Root;
        internal string ReceiptPath { get { return Path.Combine(Root, "receipt.json"); } }
        internal string SnapshotPath(Receipt r) { return Path.Combine(Root, "before-create-" + r.Token + ".ccd"); }
        internal string RecoveryPath(Receipt r) { return Path.Combine(Root, "Recovery-" + r.Token); }
        private FileStream guard;
        private static readonly SecurityIdentifier Admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        private static readonly SecurityIdentifier SystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        internal static string DefaultRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"ScreenOffTray\DriverSetup"); } }
        internal StateStore(string path)
        {
            Root = Path.GetFullPath(path);
            // Fixed default only: callers cannot redirect an elevated receipt into a user-writable location.
            if (!Policy.Same(Root.TrimEnd('\\'), DefaultRoot.TrimEnd('\\'))) throw new InvalidOperationException("Receipt directory must be protected default ProgramData path");
            string parent = Path.GetDirectoryName(Root);
            SecureDirectory(parent); SecureDirectory(Root);
            string lockPath = Path.Combine(Root, "operation.lock");
            if (File.Exists(lockPath)) NoReparse(lockPath);
            guard = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        internal static void NoReparse(string path)
        {
            string p = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(p))
            {
                if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Reparse point refused: " + p);
                p = Path.GetDirectoryName(p);
            }
        }
        private static DirectorySecurity Acl()
        {
            DirectorySecurity acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(Admin);
            InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            acl.AddAccessRule(new FileSystemAccessRule(Admin, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            return acl;
        }
        internal static FileStream CreateFile(string path)
        {
            NoReparse(path);
            FileSecurity acl = new FileSecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(Admin);
            acl.AddAccessRule(new FileSystemAccessRule(Admin, FileSystemRights.FullControl, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            return new FileStream(path, FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.WriteThrough, acl);
        }
        internal static void SecureDirectory(string path)
        {
            NoReparse(path);
            if (!Directory.Exists(path)) Directory.CreateDirectory(path, Acl());
            VerifyAcl(path, true);
        }
        private static void VerifyAcl(string path, bool directory)
        {
            NoReparse(path);
            FileSystemSecurity acl = directory ? (FileSystemSecurity)Directory.GetAccessControl(path) : File.GetAccessControl(path);
            SecurityIdentifier owner = (SecurityIdentifier)acl.GetOwner(typeof(SecurityIdentifier));
            if (!owner.Equals(Admin) && !owner.Equals(SystemSid)) throw new InvalidOperationException("Untrusted state owner: " + path);
            FileSystemRights write = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
                FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & write) != 0 &&
                    !rule.IdentityReference.Equals(Admin) && !rule.IdentityReference.Equals(SystemSid))
                    throw new InvalidOperationException("State writable outside administrators/SYSTEM: " + path);
        }
        internal static Receipt Read(string root)
        {
            string path = Path.Combine(root, "receipt.json");
            if (!File.Exists(path)) return null;
            VerifyAcl(root, true); VerifyAcl(path, false);
            if (new FileInfo(path).Length > 65536) throw new InvalidOperationException("Receipt too large");
            Receipt r = new JavaScriptSerializer().Deserialize<Receipt>(File.ReadAllText(path));
            Guid token;
            if (r == null || r.Schema != Policy.Product || !Guid.TryParseExact(r.Token, "N", out token) ||
                (r.Owned && (!Policy.Same(r.Marker, "ScreenOffTray:" + r.Token) ||
                 !Policy.Same(r.ConfigDirectory, Path.Combine(root, "Config-" + r.Token)))))
                throw new InvalidOperationException("Receipt identity invalid");
            return r;
        }
        internal void Save(Receipt r)
        {
            string temp = Path.Combine(Root, "receipt-" + Guid.NewGuid().ToString("N") + ".tmp");
            byte[] bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(r));
            using (FileStream s = CreateFile(temp))
            { s.Write(bytes, 0, bytes.Length); s.Flush(true); }
            if (File.Exists(ReceiptPath)) { VerifyAcl(ReceiptPath, false); File.Replace(temp, ReceiptPath, null); }
            else File.Move(temp, ReceiptPath);
        }
        internal void PrepareConfig(Receipt r)
        {
            using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey key = hklm.OpenSubKey(Policy.RegistryPath))
            {
                r.PriorKey = key != null;
                if (key != null && Array.Exists(key.GetValueNames(), delegate(string n) { return Policy.Same(n, "VDDPATH"); }))
                {
                    r.PriorValue = true;
                    RegistryValueKind kind = key.GetValueKind("VDDPATH");
                    if (kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString)
                        throw new InvalidOperationException("Existing VDDPATH type is not a path; preserve and stop");
                    r.PriorKind = kind.ToString(); r.PriorData = (string)key.GetValue("VDDPATH", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                }
            }
            Save(r); SecureDirectory(r.ConfigDirectory);
            string file = Path.Combine(r.ConfigDirectory, "vdd_settings.xml");
            using (FileStream s = CreateFile(file))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(Policy.Config(r.Width, r.Height)); s.Write(bytes, 0, bytes.Length); s.Flush(true);
            }
            r.ConfigHash = Policy.Hash(file); r.RegistryIntent = true; Save(r);
            // Compare-and-set under our per-machine operation lock; preserve changes by other tools.
            using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey key = hklm.CreateSubKey(Policy.RegistryPath))
            {
                CheckPrior(key, r);
                key.SetValue("VDDPATH", r.ConfigDirectory, RegistryValueKind.String);
                key.Flush();
            }
            r.Phase = "configured"; Save(r);
        }
        private static void CheckPrior(RegistryKey key, Receipt r)
        {
            object current = key.GetValue("VDDPATH", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (r.PriorValue ? current == null || key.GetValueKind("VDDPATH").ToString() != r.PriorKind || !Object.Equals(current, r.PriorData) : current != null)
                throw new InvalidOperationException("VDDPATH changed concurrently; preserving it");
        }
        internal void CheckRegistry(Receipt r)
        {
            if (!r.RegistryIntent) return;
            using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey key = hklm.OpenSubKey(Policy.RegistryPath))
            {
                if (key == null) { if (r.PriorValue) throw new InvalidOperationException("VDDPATH was removed externally"); return; }
                object current = key.GetValue("VDDPATH", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (current is string && Policy.Same((string)current, r.ConfigDirectory) && key.GetValueKind("VDDPATH") == RegistryValueKind.String) return;
                CheckPrior(key, r); // Crash may have happened before our write or after a successful restore.
            }
        }
        internal void RequireOwnedConfiguration(Receipt r)
        {
            using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey key = hklm.OpenSubKey(Policy.RegistryPath))
            {
                if (key == null || key.GetValueKind("VDDPATH") != RegistryValueKind.String ||
                    !Policy.Same(key.GetValue("VDDPATH", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, r.ConfigDirectory))
                    throw new InvalidOperationException("Owned driver configuration path was changed; preserving it");
            }
            string config = Path.Combine(r.ConfigDirectory, "vdd_settings.xml");
            NoReparse(config);
            if (!File.Exists(config)) throw new InvalidOperationException("Owned driver configuration file is missing");
        }
        internal void RestoreRegistry(Receipt r)
        {
            if (!r.RegistryIntent) return;
            CheckRegistry(r);
            using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                using (RegistryKey key = hklm.OpenSubKey(Policy.RegistryPath, true))
                {
                    if (key != null)
                    {
                        object current = key.GetValue("VDDPATH", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                        if (current is string && Policy.Same((string)current, r.ConfigDirectory))
                        {
                            if (r.PriorValue) key.SetValue("VDDPATH", r.PriorData, (RegistryValueKind)Enum.Parse(typeof(RegistryValueKind), r.PriorKind));
                            else key.DeleteValue("VDDPATH", false);
                            key.Flush();
                        }
                    }
                }
                // Empty key intentionally retained: deleting a concurrently-used vendor key is unnecessary risk.
            }
            r.RegistryIntent = false; Save(r);
        }
        internal void SaveRecoveryPayload(Receipt r, string bundle)
        {
            string root = RecoveryPath(r);
            SecureDirectory(root);
            string[] sources = { System.Reflection.Assembly.GetExecutingAssembly().Location,
                Path.Combine(bundle, "MttVDD.inf"), Path.Combine(bundle, "MttVDD.dll"), Path.Combine(bundle, "mttvdd.cat") };
            foreach (string source in sources)
            {
                using (FileStream input = File.OpenRead(source))
                using (FileStream output = CreateFile(Path.Combine(root, Path.GetFileName(source))))
                { input.CopyTo(output); output.Flush(true); }
            }
            Policy.Bundle(root);
        }
        public void Dispose() { if (guard != null) guard.Dispose(); }
    }
}
