using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenOffTray.Ui;

namespace ScreenOffTray.SettingsUi
{
    // Extraction, path restrictions and native icon ownership remain in the
    // existing cache. This adapter owns only frozen WPF copies for one window.
    internal sealed class ApplicationIcons : IDisposable
    {
        private readonly ApplicationIconCache cache=new ApplicationIconCache();
        private readonly Dictionary<string,ImageSource> copies=new Dictionary<string,ImageSource>(StringComparer.OrdinalIgnoreCase);
        private readonly Dispatcher dispatcher;
        private bool disposed;
        private int queued;
        internal event Action Changed;
        internal ApplicationIcons(Dispatcher dispatcher)
        {this.dispatcher=dispatcher;cache.Changed+=OnChanged;}
        private void OnChanged(object sender,EventArgs e)
        {
            if(Interlocked.Exchange(ref queued,1)!=0)return;
            try {dispatcher.BeginInvoke(new Action(delegate {
                Interlocked.Exchange(ref queued,0);if(disposed)return;
                var changed=Changed;if(changed!=null)changed();
            }),DispatcherPriority.Background);}catch(InvalidOperationException){Interlocked.Exchange(ref queued,0);}
        }
        internal ImageSource Read(string path)
        {
            if(disposed || String.IsNullOrEmpty(path))return null;
            ImageSource found;if(copies.TryGetValue(path,out found))return found;
            var bitmap=cache.GetOrQueue(path,48) as System.Drawing.Bitmap;if(bitmap==null)return null;
            IntPtr handle=IntPtr.Zero;
            try {
                handle=bitmap.GetHbitmap();
                var source=Imaging.CreateBitmapSourceFromHBitmap(handle,IntPtr.Zero,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();copies.Add(path,source);return source;
            }finally{if(handle!=IntPtr.Zero)DeleteObject(handle);}
        }
        public void Dispose()
        {if(disposed)return;disposed=true;Changed=null;cache.Changed-=OnChanged;copies.Clear();cache.Dispose();}
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    }
}
