using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OledPrototype
{
    // Constructors do no disk IO. Product code creates only one logger, lazily on Log.
    internal sealed class BoundedRuntimeLog
    {
        internal const int MaximumFileBytes = 1024 * 1024;
        internal const int MaximumRecordBytes = 16 * 1024;
        internal const int DefaultCapacity = 64;
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private readonly object gate = new object();
        private readonly Queue<string> queue = new Queue<string>();
        private readonly string directory;
        private string mutexName;
        private readonly int capacity, mutexWaitMilliseconds;
        private readonly Action beforeWrite;
        private bool running;
        private TaskCompletionSource<bool> idle = Completed();
        private long droppedQueue, droppedMutex, droppedWrite;

        internal BoundedRuntimeLog(string directory, string mutexName = null,
            int capacity = DefaultCapacity, Action beforeWrite = null, int mutexWaitMilliseconds = 25)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException("capacity");
            if (mutexWaitMilliseconds < 0 || mutexWaitMilliseconds > 1000)
                throw new ArgumentOutOfRangeException("mutexWaitMilliseconds");
            this.directory = RuntimePaths.Canonical(directory);
            this.mutexName = mutexName;
            this.capacity = capacity;
            this.beforeWrite = beforeWrite;
            this.mutexWaitMilliseconds = mutexWaitMilliseconds;
        }

        internal void Enqueue(string value)
        {
            // Even a huge exception string is inspected/copied only up to a fixed bound.
            string record;
            try { record = DateTimeOffset.Now.ToString("o") + " " + LimitUtf8(value ?? "", MaximumRecordBytes - 256); }
            catch { Interlocked.Increment(ref droppedWrite); return; }
            if (!Monitor.TryEnter(gate)) { Interlocked.Increment(ref droppedQueue); return; }
            try
            {
                if (queue.Count >= capacity) { Interlocked.Increment(ref droppedQueue); return; }
                queue.Enqueue(record);
                if (!running)
                {
                    running = true;
                    idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    try
                    {
                        if (!ThreadPool.QueueUserWorkItem(delegate { Drain(); })) throw new InvalidOperationException("worker unavailable");
                    }
                    catch
                    {
                        Interlocked.Add(ref droppedWrite, queue.Count);
                        queue.Clear(); running = false; idle.TrySetResult(true);
                    }
                }
            }
            finally { Monitor.Exit(gate); }
        }

        // True means this queue drained (including bounded attempts that were dropped),
        // not a durability/zero-loss promise. A timeout never waits for the IO worker.
        internal async Task<bool> FlushAsync(int timeoutMilliseconds)
        {
            if (timeoutMilliseconds < 0 || timeoutMilliseconds > 60000)
                throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            Task drained;
            if (!Monitor.TryEnter(gate)) return false;
            try { drained = idle.Task; } finally { Monitor.Exit(gate); }
            if (drained.IsCompleted) return true;
            using (CancellationTokenSource timer = new CancellationTokenSource())
            {
                Task delay = Task.Delay(timeoutMilliseconds, timer.Token);
                if (await Task.WhenAny(drained, delay).ConfigureAwait(false) != drained) return false;
                timer.Cancel(); return true;
            }
        }

        private void Drain()
        {
            // No recursive retry, waiting event or permanent thread/stream/mutex handle.
            while (true)
            {
                string record;
                lock (gate)
                {
                    if (queue.Count == 0) { running = false; idle.TrySetResult(true); return; }
                    record = queue.Dequeue();
                }
                try { Write(record); }
                catch { Interlocked.Increment(ref droppedWrite); }
            }
        }

        private void Write(string record)
        {
            // Resolve SID/hash only on the worker, never in the first hook/UI Log.
            if (mutexName == null) mutexName = RuntimePaths.MutexName(directory, "log");
            using (Mutex mutex = new Mutex(false, mutexName))
            {
                bool acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(mutexWaitMilliseconds); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) { Interlocked.Increment(ref droppedMutex); return; }
                    if (beforeWrite != null) beforeWrite();
                    RuntimePaths.EnsureDirectory(directory);
                    string path = Path.Combine(directory, "事件.log");
                    string first = path + ".1", second = path + ".2";
                    RuntimePaths.CheckFile(path); RuntimePaths.CheckFile(first); RuntimePaths.CheckFile(second);
                    TrimLegacy(path); TrimLegacy(first); TrimLegacy(second);
                    // Snapshot counters. Concurrent drops remain pending; only successful
                    // append acknowledges these values, so a failed append loses none.
                    long full = Interlocked.Read(ref droppedQueue), busy = Interlocked.Read(ref droppedMutex), failed = Interlocked.Read(ref droppedWrite);
                    string drops = full == 0 && busy == 0 && failed == 0 ? "" :
                        "[dropped queue=" + full.ToString(CultureInfo.InvariantCulture) +
                        " mutex=" + busy.ToString(CultureInfo.InvariantCulture) +
                        " write=" + failed.ToString(CultureInfo.InvariantCulture) + "] ";
                    byte[] bytes = Utf8.GetBytes(LimitUtf8(drops + record, MaximumRecordBytes - 2) + "\r\n");
                    if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > MaximumFileBytes)
                    {
                        if (File.Exists(second)) File.Delete(second);
                        if (File.Exists(first)) File.Move(first, second);
                        File.Move(path, first);
                    }
                    using (FileStream stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                        stream.Write(bytes, 0, bytes.Length);
                    Interlocked.Add(ref droppedQueue, -full); Interlocked.Add(ref droppedMutex, -busy); Interlocked.Add(ref droppedWrite, -failed);
                }
                finally { if (acquired) mutex.ReleaseMutex(); }
            }
        }

        private static void TrimLegacy(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length <= MaximumFileBytes) return;
            // A previous version could have left an unbounded log. Keep only complete
            // tail lines, at most 1 MiB, before putting it into the bounded rotation.
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                byte[] tail = new byte[MaximumFileBytes];
                stream.Seek(-MaximumFileBytes, SeekOrigin.End);
                int count = 0, read;
                while (count < tail.Length && (read = stream.Read(tail, count, tail.Length - count)) != 0) count += read;
                int start = Array.IndexOf(tail, (byte)'\n', 0, count);
                start = start < 0 ? count : start + 1;
                stream.Position = 0; stream.Write(tail, start, count - start); stream.SetLength(count - start);
            }
        }

        internal static string LimitUtf8(string value, int maximumBytes)
        {
            const string marker = "[truncated]";
            int budget = maximumBytes - marker.Length, used = 0, end = 0;
            while (end < value.Length)
            {
                char c = value[end];
                int chars = char.IsHighSurrogate(c) && end + 1 < value.Length && char.IsLowSurrogate(value[end + 1]) ? 2 : 1;
                int bytes = chars == 2 ? 4 : c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
                if (used + bytes > budget) break;
                used += bytes; end += chars;
            }
            return end == value.Length ? value : value.Substring(0, end) + marker;
        }

        private static TaskCompletionSource<bool> Completed()
        {
            TaskCompletionSource<bool> result = new TaskCompletionSource<bool>(); result.SetResult(true); return result;
        }
    }

    internal static class RuntimePaths
    {
        internal static string Canonical(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("directory");
            string path = Path.GetFullPath(directory);
            return path.Length == Path.GetPathRoot(path).Length ? path : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        internal static string MutexName(string directory, string purpose)
        {
            string user;
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) user = identity.User.Value;
            using (SHA256 hash = SHA256.Create())
            {
                byte[] digest = hash.ComputeHash(Encoding.UTF8.GetBytes(user + "\n" + Canonical(directory).ToUpperInvariant() + "\n" + purpose));
                return "Global\\ScreenOffTray-" + purpose + "-" + BitConverter.ToString(digest).Replace("-", "");
            }
        }

        internal static void CheckDirectory(string directory)
        {
            DirectoryInfo current = new DirectoryInfo(directory);
            while (current != null)
            {
                // File.GetAttributes throws on access denied (Exists would hide it).
                try
                {
                    FileAttributes attributes = File.GetAttributes(current.FullName);
                    if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                        throw new IOException("Unsafe runtime directory: " + current.FullName);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                current = current.Parent;
            }
        }

        internal static void EnsureDirectory(string directory)
        {
            CheckDirectory(directory); Directory.CreateDirectory(directory); CheckDirectory(directory);
        }

        internal static void CheckFile(string path)
        {
            CheckDirectory(Path.GetDirectoryName(path));
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new IOException("Unsafe runtime file: " + path);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal sealed class RuntimeMaintenance
    {
        internal sealed class OwnedSnapshot
        {
            internal readonly string Path;
            private readonly object ownershipKey;
            internal int State;
            private OwnedSnapshot(object ownershipKey, string path) { this.ownershipKey = ownershipKey; Path = path; }
            internal static OwnedSnapshot New(object ownershipKey, string path) { return new OwnedSnapshot(ownershipKey, path); }
            internal bool BelongsTo(object candidate) { return object.ReferenceEquals(ownershipKey, candidate); }
        }

        private sealed class Entry { internal string Name; internal long Order; }
        private const string Header = "ScreenOffTray completed snapshots v1";
        private readonly string runtime, completed, mutexName;
        private readonly Action<string> log;
        private readonly Action<string> beforeOperation;
        // The unexported key authenticates minted tokens without retaining them.
        // Fault/stop=false/IO failures preserve disk material, not a global token list.
        private readonly object ownershipKey = new object();

        internal RuntimeMaintenance(string runtime, Action<string> log, string mutexName = null, Action<string> beforeOperation = null)
        {
            this.runtime = RuntimePaths.Canonical(runtime);
            completed = Path.Combine(this.runtime, "已完成恢复");
            this.log = log;
            this.mutexName = mutexName ?? RuntimePaths.MutexName(completed, "archive");
            this.beforeOperation = beforeOperation;
        }

        // Call on the existing preparation worker. The callback must CREATE NEW,
        // never overwrite (DisplayBackend.SaveRecovery already has that contract).
        // No adoption API: no token can be obtained for an old/unknown snapshot.
        internal OwnedSnapshot CreateSnapshot(Action<string> createNewSnapshot)
        {
            if (createNewSnapshot == null) throw new ArgumentNullException("createNewSnapshot");
            RuntimePaths.EnsureDirectory(runtime);
            string path = Path.Combine(runtime, "恢复-" + Guid.NewGuid().ToString("N") + ".bin");
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Snapshot collision");
            createNewSnapshot(path);
            RuntimePaths.CheckFile(path);
            if (!File.Exists(path)) throw new IOException("Snapshot was not created");
            return OwnedSnapshot.New(ownershipKey, path);
        }

        // safeToArchive is a caller assertion: restored AND verified, or never APPLY.
        // No file IO occurs before stopped has successfully yielded true. Never use
        // Wait/Result on UI; this method catches stop/callback/IO errors and logs only.
        internal async Task<bool> ArchiveCompletedAsync(OwnedSnapshot snapshot, Task<bool> stopped, bool safeToArchive)
        {
            if (!safeToArchive || snapshot == null || !snapshot.BelongsTo(ownershipKey) || stopped == null) return false;
            try
            {
                if (!await stopped.ConfigureAwait(false)) return false;
                if (Interlocked.CompareExchange(ref snapshot.State, 1, 0) != 0) return false;
                try
                {
                    bool result = await Task.Run(delegate { return Archive(snapshot); }).ConfigureAwait(false);
                    Interlocked.Exchange(ref snapshot.State, result ? 2 : 0); return result;
                }
                catch { Interlocked.Exchange(ref snapshot.State, 0); throw; }
            }
            catch (Exception ex) { Report("snapshot_archive_preserved: " + ex.GetType().Name); return false; }
        }

        private bool Archive(OwnedSnapshot snapshot)
        {
            using (Mutex mutex = new Mutex(false, mutexName))
            {
                bool acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(50); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) { Report("snapshot_archive_preserved: mutex_busy"); return false; }
                    RuntimePaths.CheckFile(snapshot.Path);
                    if (!File.Exists(snapshot.Path)) return false;
                    RuntimePaths.EnsureDirectory(completed);
                    string index = Path.Combine(completed, "归档登记.txt");
                    List<Entry> entries = ReadIndex(index);
                    string name = "已恢复-" + Guid.NewGuid().ToString("N") + ".bin";
                    string destination = Path.Combine(completed, name);
                    RuntimePaths.CheckFile(destination);
                    if (beforeOperation != null) beforeOperation("move");
                    RuntimePaths.CheckFile(snapshot.Path); RuntimePaths.CheckFile(destination);
                    File.Move(snapshot.Path, destination);
                    long order = DateTime.UtcNow.Ticks;
                    foreach (Entry entry in entries) order = Math.Max(order, entry.Order + 1);
                    entries.Add(new Entry { Name = name, Order = order });
                    try { SaveIndex(index, entries); }
                    catch
                    {
                        // Catalog failure must never make an unregistered file eligible
                        // for cleanup. Try returning it to its original recovery path.
                        try { RuntimePaths.CheckFile(destination); RuntimePaths.CheckFile(snapshot.Path); File.Move(destination, snapshot.Path); }
                        catch { Report("snapshot_archive_preserved: catalog_failed_rollback_failed"); }
                        throw;
                    }
                    try { Prune(index, entries); }
                    catch (Exception ex) { Report("snapshot_cleanup_deferred: " + ex.GetType().Name); }
                    return true;
                }
                finally { if (acquired) mutex.ReleaseMutex(); }
            }
        }

        private List<Entry> ReadIndex(string index)
        {
            RuntimePaths.CheckFile(index);
            List<Entry> entries = new List<Entry>();
            if (!File.Exists(index)) return entries;
            if (new FileInfo(index).Length > 256 * 1024) throw new IOException("Archive catalog too large");
            string[] lines = File.ReadAllLines(index, Encoding.UTF8);
            if (lines.Length == 0 || lines[0] != Header) throw new IOException("Unknown archive catalog");
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                string[] parts = lines[i].Split('|'); long order; Guid id;
                if (parts.Length != 2 || parts[0].Length != 40 || !parts[0].StartsWith("已恢复-", StringComparison.Ordinal) ||
                    !parts[0].EndsWith(".bin", StringComparison.Ordinal) || !Guid.TryParseExact(parts[0].Substring(4, 32), "N", out id) ||
                    !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out order) ||
                    order < 0 || order > DateTime.MaxValue.Ticks || !names.Add(parts[0])) throw new IOException("Invalid archive catalog entry");
                entries.Add(new Entry { Name = parts[0], Order = order });
            }
            return entries;
        }

        private void SaveIndex(string index, List<Entry> entries)
        {
            RuntimePaths.CheckFile(index);
            string temporary = index + ".tmp-" + Guid.NewGuid().ToString("N");
            bool owned = false;
            try
            {
                if (beforeOperation != null) beforeOperation("catalog");
                RuntimePaths.CheckFile(temporary);
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    owned = true;
                    using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
                    {
                        writer.WriteLine(Header);
                        foreach (Entry entry in entries) writer.WriteLine(entry.Name + "|" + entry.Order.ToString(CultureInfo.InvariantCulture));
                        writer.Flush();
                    }
                    stream.Flush(true);
                }
                RuntimePaths.CheckFile(index); RuntimePaths.CheckFile(temporary);
                if (File.Exists(index)) File.Replace(temporary, index, null); else File.Move(temporary, index);
                owned = false;
            }
            finally
            {
                if (owned) try { RuntimePaths.CheckFile(temporary); File.Delete(temporary); } catch { }
            }
        }

        private void Prune(string index, List<Entry> entries)
        {
            entries.Sort(delegate(Entry a, Entry b) { return a.Order.CompareTo(b.Order); });
            bool changed = false;
            // Only committed catalog entries under completed are eligible. Never
            // scan runtime GUIDs or adopt unregistered files, even in completed.
            for (int i = 0; i < entries.Count; )
            {
                string path = Path.Combine(completed, entries[i].Name);
                RuntimePaths.CheckFile(path);
                if (!File.Exists(path)) { entries.RemoveAt(i); changed = true; } else i++;
            }
            while (entries.Count > 20)
            {
                string path = Path.Combine(completed, entries[0].Name);
                if (beforeOperation != null) beforeOperation("delete");
                RuntimePaths.CheckFile(path); File.Delete(path);
                entries.RemoveAt(0); changed = true;
            }
            if (changed) SaveIndex(index, entries);
        }

        private void Report(string message) { try { if (log != null) log(message); } catch { } }
    }
}
