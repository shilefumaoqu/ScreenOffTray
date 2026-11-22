using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ScreenOffTray.SettingsUi
{
    // The DWM owns the desktop sampling and blur. WPF draws only the foreground;
    // neither screenshots nor a WPF BlurEffect can provide a desktop backdrop.
    internal static class NativeBackdrop
    {
        internal static bool Apply(IntPtr window,bool dark,bool highContrast)
        {
            int value=dark?1:0;
            DwmSetWindowAttribute(window,20,ref value,4);
            value=2;DwmSetWindowAttribute(window,33,ref value,4);
            value=-2;DwmSetWindowAttribute(window,34,ref value,4);
            bool enabled=!highContrast && TransparencyEnabled();
            value=enabled?3:1; // NONE or Desktop Acrylic; Windows 11 22621+.
            bool attached=DwmSetWindowAttribute(window,38,ref value,4)>=0 && enabled;
            int inset=attached?-1:0;
            var margins=new Margins{Left=inset,Right=inset,Top=inset,Bottom=inset};
            if(DwmExtendFrameIntoClientArea(window,ref margins)<0)attached=false;
            if(!attached){value=1;DwmSetWindowAttribute(window,38,ref value,4);}
            return attached;
        }
        private static bool TransparencyEnabled()
        {
            object value=Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","EnableTransparency",1);
            return !(value is int) || (int)value!=0;
        }
        internal static void Clip(IntPtr window,double radius)
        {
            Rect rect;if(!GetClientRect(window,out rect) || rect.Right<=0 || rect.Bottom<=0)return;
            int diameter=(int)Math.Round(radius*2*GetDpiForWindow(window)/96.0);
            IntPtr region=CreateRoundRectRgn(0,0,rect.Right+1,rect.Bottom+1,diameter,diameter);
            if(region!=IntPtr.Zero && SetWindowRgn(window,region,true)==0)DeleteObject(region);
            // Successful SetWindowRgn transfers ownership to Windows.
        }
        [StructLayout(LayoutKind.Sequential)] private struct Margins {internal int Left,Right,Top,Bottom;}
        [StructLayout(LayoutKind.Sequential)] private struct Rect {internal int Left,Top,Right,Bottom;}
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
        [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr window,ref Margins margins);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window,out Rect rect);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr window,IntPtr region,bool redraw);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left,int top,int right,int bottom,int ellipseWidth,int ellipseHeight);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    }
}
