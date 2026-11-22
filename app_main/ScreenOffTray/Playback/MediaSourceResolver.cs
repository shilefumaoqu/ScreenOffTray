using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ScreenOffTray.Playback
{
    // A successful read supplies a fresh image path and the exact Win32 AppModel result.
    internal delegate bool ReadMediaProcessIdentity(int processId, out string exePath,
        out string appId, out int appModelResult);
    // true promises a complete current list, scoped to this full executable basename.
    internal delegate bool EnumerateMediaProcessIds(string executableName, int limit, out int[] processIds);

    internal sealed class WindowsMediaSourceResolver : IMediaSourceResolver
    {
        internal const int MaximumProcesses = 32;
        internal const int NoApplication = 15703;
        private readonly ReadMediaProcessIdentity readIdentity;
        private readonly EnumerateMediaProcessIds enumerateSameName;

        internal WindowsMediaSourceResolver() : this(ReadIdentity, EnumerateSameName) { }

        internal WindowsMediaSourceResolver(ReadMediaProcessIdentity readIdentity,
            EnumerateMediaProcessIds enumerateSameName)
        {
            if (readIdentity == null) throw new ArgumentNullException("readIdentity");
            if (enumerateSameName == null) throw new ArgumentNullException("enumerateSameName");
            this.readIdentity = readIdentity;
            this.enumerateSameName = enumerateSameName;
        }

        public bool TryResolve(AudioSource source, out string appId)
        {
            appId = null;
            try { return TryResolveCore(source, out appId); }
            catch (Exception)
            {
                // Identity uncertainty must leave this source's audio protection in place.
                appId = null;
                return false;
            }
        }

        private bool TryResolveCore(AudioSource source, out string appId)
        {
            appId = null;
            if (source == null) return false;
            string expectedPath = NormalizeAbsoluteExe(source.ExePath);
            int[] suppliedOwners = source.ProcessIds;
            if (expectedPath == null || suppliedOwners == null || suppliedOwners.Length == 0 ||
                suppliedOwners.Length > MaximumProcesses) return false;
            int[] owners = (int[])suppliedOwners.Clone();
            HashSet<int> ownerSet = new HashSet<int>();
            string exactId = null;
            bool? unpackaged = null;
            foreach (int pid in owners)
            {
                if (pid <= 0 || !ownerSet.Add(pid)) return false;
                string id;
                int result;
                if (!ReadMatchingIdentity(pid, expectedPath, out id, out result)) return false;
                bool desktop = result == NoApplication;
                if (unpackaged.HasValue && unpackaged.Value != desktop) return false;
                unpackaged = desktop;
                if (!desktop)
                {
                    if (exactId != null && !String.Equals(exactId, id, StringComparison.Ordinal)) return false;
                    exactId = id;
                }
            }
            if (unpackaged == false)
            {
                appId = exactId;
                return true;
            }

            string basename = Path.GetFileName(expectedPath);
            int[] sameName;
            if (!enumerateSameName(basename, MaximumProcesses, out sameName) || sameName == null ||
                sameName.Length == 0 || sameName.Length > MaximumProcesses) return false;
            sameName = (int[])sameName.Clone();
            HashSet<int> current = new HashSet<int>();
            foreach (int pid in sameName)
            {
                if (pid <= 0 || !current.Add(pid)) return false;
                string id;
                int result;
                // Owners are deliberately read again: no cross-enumeration identity cache.
                if (!ReadMatchingIdentity(pid, expectedPath, out id, out result) || result != NoApplication)
                    return false;
            }
            if (!ownerSet.IsSubsetOf(current)) return false;

            // Application-level association only: current image uniqueness plus the media
            // source's full-filename convention. This is not proof of session-instance identity
            // or a universal desktop-app mapping. MediaPauseFilter must compare IDs ordinally.
            appId = basename;
            return true;
        }

        private bool ReadMatchingIdentity(int pid, string expectedPath, out string id, out int result)
        {
            string path;
            if (!readIdentity(pid, out path, out id, out result)) return false;
            string actualPath = NormalizeAbsoluteExe(path);
            if (actualPath == null || !String.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                return false;
            if (result == NoApplication) return String.IsNullOrEmpty(id);
            return result == 0 && !String.IsNullOrWhiteSpace(id) && id.IndexOf('\0') < 0;
        }

        private static string NormalizeAbsoluteExe(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return null;
            // IsPathRooted alone also accepts drive-relative and current-drive-rooted paths.
            bool driveAbsolute = path.Length >= 3 &&
                ((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z')) &&
                path[1] == ':' && (path[2] == '\\' || path[2] == '/');
            bool unc = path.StartsWith(@"\\", StringComparison.Ordinal) &&
                !path.StartsWith(@"\\?\", StringComparison.Ordinal) &&
                !path.StartsWith(@"\\.\", StringComparison.Ordinal);
            if (!driveAbsolute && !unc) return null;
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.IndexOf('*') >= 0 || path.IndexOf('?') >= 0)
                return null;
            // Reject alternate-stream and other colon aliases rather than guessing an image.
            if (path.IndexOf(':', driveAbsolute ? 2 : 0) >= 0) return null;
            string normalized = ExeIdentity.Normalize(path);
            if (normalized == null) return null;
            if (unc)
            {
                string root = Path.GetPathRoot(normalized);
                string[] parts = root.Trim('\\').Split('\\');
                if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) return null;
            }
            return normalized;
        }

        private static bool EnumerateSameName(string executableName, int limit, out int[] processIds)
        {
            processIds = null;
            // No GetProcesses(), MainModule access, or unrelated-program identity reads.
            Process[] matches = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executableName));
            try
            {
                if (matches.Length == 0 || matches.Length > limit) return false;
                int[] ids = new int[matches.Length];
                for (int i = 0; i < matches.Length; i++) ids[i] = matches[i].Id;
                processIds = ids;
                return true;
            }
            finally
            {
                foreach (Process process in matches) process.Dispose();
            }
        }

        private static bool ReadIdentity(int pid, out string exePath, out string appId, out int appModelResult)
        {
            exePath = null;
            appId = null;
            appModelResult = -1;
            // Path and AppID come from the same freshly opened process handle.
            using (IdentityProcessHandle handle = OpenProcess(0x1000, false, pid))
            {
                if (handle == null || handle.IsInvalid) return false;
                if (!IsRunning(handle)) return false;
                StringBuilder path = new StringBuilder(32768);
                uint pathLength = (uint)path.Capacity;
                if (!QueryFullProcessImageName(handle, 0, path, ref pathLength) || pathLength == 0)
                    return false;
                uint length = 0;
                int result = GetApplicationUserModelId(handle, ref length, null);
                if (result == NoApplication)
                {
                    appModelResult = result;
                }
                else
                {
                    // One bounded size query and one read; no resize loop or retries.
                    if (result != 122 || length <= 1 || length > 32768) return false;
                    StringBuilder id = new StringBuilder((int)length);
                    result = GetApplicationUserModelId(handle, ref length, id);
                    if (result != 0 || length > id.Capacity) return false;
                    appModelResult = result;
                    appId = id.ToString();
                }
                if (!IsRunning(handle)) return false;
                exePath = path.ToString();
                return true;
            }
        }

        private static bool IsRunning(IdentityProcessHandle handle)
        {
            // Exit code STILL_ACTIVE (259) can also be an actual exit code. Use the
            // kernel's zero exit timestamp instead, with the same query-limited handle.
            System.Runtime.InteropServices.ComTypes.FILETIME created, exited, kernel, user;
            return GetProcessTimes(handle, out created, out exited, out kernel, out user) &&
                exited.dwLowDateTime == 0 && exited.dwHighDateTime == 0;
        }

        private sealed class IdentityProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public IdentityProcessHandle() : base(true) { }
            protected override bool ReleaseHandle() { return CloseHandle(handle); }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IdentityProcessHandle OpenProcess(uint access,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(IdentityProcessHandle handle,
            out System.Runtime.InteropServices.ComTypes.FILETIME creationTime,
            out System.Runtime.InteropServices.ComTypes.FILETIME exitTime,
            out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime,
            out System.Runtime.InteropServices.ComTypes.FILETIME userTime);
        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(IdentityProcessHandle handle, uint flags,
            StringBuilder path, ref uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetApplicationUserModelId(IdentityProcessHandle handle,
            ref uint length, StringBuilder appId);
    }
}
