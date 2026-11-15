using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace ScreenOffTray.Ui
{
    // One cache per panel. No eviction/retry/file watching during that lifetime:
    // reopening the panel retries failures and picks up replaced executable icons.
    internal sealed class ApplicationIconCache : IDisposable
    {
        private const int MaximumEntries = 256;
        private const int MaximumPixels = 256;
        private readonly object gate = new object();
        private readonly Dictionary<string, Entry> entries =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<Entry> pending = new Queue<Entry>();
        private readonly Func<string, int, Image> extract;
        private EventHandler changed;
        private bool workerRunning, disposed;

        internal ApplicationIconCache() : this(ExtractIcon) { }

        // Test seam. The extractor transfers ownership of each returned image.
        internal ApplicationIconCache(Func<string, int, Image> extractor)
        {
            if (extractor == null) throw new ArgumentNullException("extractor");
            extract = extractor;
        }

        // Background notification, including negative completions. Consumers marshal
        // to their UI thread and re-query the current row identity. An observer already
        // in flight may finish after Dispose; UI callbacks must check their lifetime.
        internal event EventHandler Changed
        {
            add { lock (gate) { if (!disposed) changed += value; } }
            remove { lock (gate) { changed -= value; } }
        }

        // BORROWED immutable image: callers must neither mutate nor dispose it.
        // All images stay alive until Dispose, which the owning panel calls only
        // after detaching its image consumers. Null means use a neutral fallback.
        // Only fully qualified drive paths to .exe files and 1..256 pixels qualify.
        // Normalization is lexical (case/dot segments), not a file/link lookup.
        internal Image GetOrQueue(string exePath, int pixels)
        {
            if (pixels < 1 || pixels > MaximumPixels) return null;
            string path = NormalizePath(exePath);
            if (path == null) return null;
            string key = path + '\0' + pixels.ToString(CultureInfo.InvariantCulture);
            lock (gate) {
                if (disposed) return null;
                Entry entry;
                if (entries.TryGetValue(key, out entry)) return entry.Image;
                // Pending and negative entries consume capacity too. Do not evict
                // images which a PictureBox may still be borrowing.
                if (entries.Count >= MaximumEntries) return null;
                entry = new Entry(path, pixels);
                entries.Add(key, entry);
                pending.Enqueue(entry);
                if (!workerRunning) {
                    workerRunning = true;
                    if (!ThreadPool.QueueUserWorkItem(Drain)) {
                        pending.Clear();
                        workerRunning = false;
                    }
                }
                return null;
            }
        }

        private static string NormalizePath(string path)
        {
            // Reject relative, UNC and device paths, rather than invoking Shell on
            // an ambiguous name or explicitly requesting a network icon.
            if (String.IsNullOrWhiteSpace(path) || path.Length < 4 ||
                !((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z')) ||
                path[1] != ':' || (path[2] != '\\' && path[2] != '/') ||
                path.IndexOf(':', 2) >= 0) return null;
            try {
                string fullPath = Path.GetFullPath(path);
                return String.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase)
                    ? fullPath : null;
            } catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
            catch (System.Security.SecurityException) { return null; }
        }

        private void Drain(object state)
        {
            while (true) {
                Entry entry;
                lock (gate) {
                    if (disposed || pending.Count == 0) {
                        workerRunning = false;
                        return;
                    }
                    entry = pending.Dequeue();
                }
                Image image = null;
                try { image = extract(entry.Path, entry.Pixels); }
                catch (Exception) { /* Missing, inaccessible or unreadable: neutral fallback. */ }
                bool published;
                lock (gate) {
                    published = !disposed;
                    if (published) { entry.Image = image; image = null; }
                }
                // Dispose never waits for Shell. A result completing after it owns
                // no UI consumer and is released here, on the worker.
                Release(image);
                if (published) NotifyChanged();
            }
        }

        private void NotifyChanged()
        {
            EventHandler observers;
            lock (gate) { if (disposed) return; observers = changed; }
            if (observers == null) return;
            foreach (EventHandler observer in observers.GetInvocationList()) {
                lock (gate) { if (disposed) return; }
                // Never hold the cache lock across external code. One faulty
                // observer must not suppress other observers or stop the drain.
                try { observer(this, EventArgs.Empty); }
                catch (Exception) { }
            }
        }

        private static Image ExtractIcon(string path, int pixels)
        {
            // All file access stays on this background drain. Read the executable's
            // own icon resource directly, without Shell association/logo lookup.
            if (!File.Exists(path)) return null;
            IntPtr handle = IntPtr.Zero;
            try {
                // LOWORD requests the large icon's physical pixel size; no small
                // icon is requested. This also avoids upscaling a fixed 32px icon.
                int result = SHDefExtractIcon(path, 0, 0, out handle, IntPtr.Zero, (uint)pixels);
                if (result != 0 || handle == IntPtr.Zero) return null;
                // FromHandle borrows the HICON. ToBitmap and the resized result own
                // their pixel storage independently; finally owns DestroyIcon.
                using (Icon icon = Icon.FromHandle(handle))
                using (Bitmap original = icon.ToBitmap()) {
                    return new Bitmap(original, new Size(pixels, pixels));
                }
            } finally {
                if (handle != IntPtr.Zero) DestroyIcon(handle);
            }
        }

        [DllImport("shell32.dll", EntryPoint = "SHDefExtractIconW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SHDefExtractIcon(string path, int index, uint flags,
            out IntPtr largeIcon, IntPtr smallIcon, uint sizes);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr icon);

        public void Dispose()
        {
            List<Image> images = new List<Image>();
            lock (gate) {
                if (disposed) return;
                disposed = true;
                changed = null;
                pending.Clear();
                foreach (Entry entry in entries.Values)
                    if (entry.Image != null) images.Add(entry.Image);
                entries.Clear();
            }
            // No join, wait handle, task cancellation wait or filesystem work.
            foreach (Image image in images) Release(image);
        }

        private static void Release(Image image)
        {
            if (image == null) return;
            try { image.Dispose(); }
            catch (Exception) { }
        }

        private sealed class Entry
        {
            internal readonly string Path;
            internal readonly int Pixels;
            internal Image Image;
            internal Entry(string path, int pixels) { Path = path; Pixels = pixels; }
        }
    }
}
