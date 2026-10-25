using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Reflection;
using System.Xml;
using Microsoft.Win32;
using ScreenOffTray.Properties;

namespace ScreenOffTray
{
    internal sealed class SettingsMigrationResult
    {
        internal readonly List<int> Choices = new List<int>();
        internal bool NeedsChoice, CanFinish = true;
        internal string Warning;
    }

    internal static class SettingsMigration
    {
        internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "ScreenOffTray";
        internal static SettingsMigrationResult Begin()
        {
            var result = new SettingsMigrationResult();
            try {
                if (Settings.Default.MigrationComplete) return result;
                string destination = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal).FilePath;
                bool present; int ignored;
                ReadTimeout(destination, out present, out ignored);
                if (!present) {
                    bool failed;
                    var values = FindLegacyTimeouts(new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) }, destination, out failed);
                    result.Choices.AddRange(values);
                    if (failed) { result.CanFinish = false; result.Warning = "部分旧设置无法读取 / Some previous settings could not be read."; }
                    else if (values.Count == 1) { Settings.Default.TimeoutMinutes = result.Choices[0]; }
                    else if (values.Count > 1) result.NeedsChoice = true;
                }
                // No enable-on-install. An existing, proven old executable alone can be rewritten.
                MigrateStartup();
                if (!result.NeedsChoice && result.CanFinish) Finish();
            } catch (Exception ex) {
                result.CanFinish = false;
                result.Warning = "设置迁移未完成 / Settings migration incomplete: " + ex.Message;
            }
            return result;
        }
        internal static void Finish() { Settings.Default.MigrationComplete = true; Settings.Default.Save(); }
        internal static SortedSet<int> FindLegacyTimeouts(string[] roots, string destination, out bool failed)
        {
            var values = new SortedSet<int>(); failed = false;
            foreach (string root in roots) {
                try {
                    foreach (string directory in Directory.GetDirectories(root, "ScreenOffTray*", SearchOption.TopDirectoryOnly)) {
                        string name = Path.GetFileName(directory);
                        if (name != "ScreenOffTray" && !name.StartsWith("ScreenOffTray.exe_", StringComparison.OrdinalIgnoreCase)) continue;
                        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0) ScanOwned(directory, 0, destination, values, ref failed);
                    }
                } catch (IOException) { failed = true; } catch (UnauthorizedAccessException) { failed = true; }
            }
            return values;
        }
        private static void ScanOwned(string directory, int depth, string destination, ISet<int> values, ref bool failed)
        {
            // Only the product's own top-level AppData directories, bounded depth.
            // Never scan unrelated applications, load arbitrary config sections, or delete old files.
            try {
                string config = Path.Combine(directory, "user.config");
                if (File.Exists(config) && !String.Equals(Path.GetFullPath(config), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) {
                    bool present; int value;
                    ReadTimeout(config, out present, out value);
                    if (present && value >= 1 && value <= 1440) values.Add(value);
                }
                if (depth >= 3) return;
                foreach (string child in Directory.GetDirectories(directory)) {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) ScanOwned(child, depth + 1, destination, values, ref failed);
                }
            } catch (IOException) { failed = true; } catch (UnauthorizedAccessException) { failed = true; }
            catch (XmlException) { failed = true; }
        }
        internal static void ReadTimeout(string path, out bool present, out int value)
        {
            present = false; value = 0;
            if (!File.Exists(path)) return;
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 };
            var doc = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(path, settings)) doc.Load(reader);
            XmlNodeList nodes = doc.SelectNodes("/configuration/userSettings/ScreenOffTray.Properties.Settings/setting[@name='TimeoutMinutes']/value");
            if (nodes == null || nodes.Count == 0) return;
            present = true; // Presence takes precedence, even if the destination needs a later explicit repair.
            if (nodes.Count == 1) Int32.TryParse(nodes[0].InnerText, out value);
        }
        internal static bool IsOwnedCommand(string command, string currentPath)
        {
            string path;
            if (!TryExecutable(command, out path)) return false;
            if (String.Equals(path, Path.GetFullPath(currentPath), StringComparison.OrdinalIgnoreCase)) return true;
            try {
                if (!File.Exists(path) || !String.Equals(Path.GetFileName(path), "ScreenOffTray.exe", StringComparison.OrdinalIgnoreCase)) return false;
                if (AssemblyName.GetAssemblyName(path).Name != "ScreenOffTray") return false;
                var assembly = Assembly.ReflectionOnlyLoadFrom(path);
                return assembly.GetType("ScreenOffTray.TrayApplicationContext", false) != null &&
                    assembly.GetType("ScreenOffTray.Properties.Settings", false) != null;
            } catch { return false; }
        }
        internal static bool TryExecutable(string command, out string path)
        {
            path = null;
            if (String.IsNullOrWhiteSpace(command)) return false;
            string raw = command.Trim();
            if (raw.StartsWith("\"", StringComparison.Ordinal)) {
                if (raw.Length < 3 || !raw.EndsWith("\"", StringComparison.Ordinal)) return false;
                raw = raw.Substring(1, raw.Length - 2);
            }
            if (raw.IndexOf('"') >= 0 || !raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !Path.IsPathRooted(raw)) return false;
            try { path = Path.GetFullPath(raw); return true; } catch { return false; }
        }
        private static void MigrateStartup()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true)) {
                if (key == null) return;
                string value = key.GetValue(RunName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                if (value != null && IsOwnedCommand(value, System.Windows.Forms.Application.ExecutablePath))
                    key.SetValue(RunName, "\"" + System.Windows.Forms.Application.ExecutablePath + "\"", RegistryValueKind.String);
            }
        }
    }
}
