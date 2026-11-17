using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Forms;
using ScreenOffTray;
using ScreenOffTray.SettingsTransport;

namespace OledPrototype { internal static class Program { internal static void Log(string text) { throw new Exception("Use the test logger"); } } }

// One external backdrop and one real candidate UI process, without UI injection.
// No product Main,
// hooks, settings persistence or display operations are included in this host.
internal sealed class GlassBackdropTest : Form
{
    private readonly string binary, output, theme;
    private SettingsPipeHost host;
    private readonly Timer timer = new Timer { Interval = 1800 };
    private IntPtr window;
    private int phase, width, height, firstX, secondX, windowY, pid, focusRetries;
    private DateTime started;
    private bool ready, complete;
    private readonly Rectangle screen;

    [STAThread] private static int Main(string[] args)
    {
        if (args.Length != 2 && args.Length != 3) return 2;
        SetProcessDPIAware();
        Application.EnableVisualStyles();
        string checks=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        string candidate=Path.GetFullPath(args[0]);
        if(!candidate.StartsWith(checks,StringComparison.OrdinalIgnoreCase) || Path.GetFileName(candidate)!="ScreenOffTray.Settings.exe")return 3;
        if(Directory.Exists(args[1]) && Directory.GetFileSystemEntries(args[1]).Length!=0)return 4;
        Directory.CreateDirectory(args[1]);
        using (var stage = new GlassBackdropTest(args[0], args[1], args.Length==3?args[2]:"light")) Application.Run(stage);
        return File.Exists(Path.Combine(args[1], "complete.txt")) && !File.Exists(Path.Combine(args[1], "failure.txt")) ? 0 : 1;
    }
    private GlassBackdropTest(string executable, string directory, string mode)
    {
        binary = Path.GetFullPath(executable); output = Path.GetFullPath(directory); theme=mode=="dark"?"dark":"light";
        screen = Screen.PrimaryScreen.Bounds;
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
        Bounds = screen; ShowInTaskbar = false; DoubleBuffered = true; TopMost = true;
        Text = "External glass verification backdrop";
        Shown += delegate {
            host = new SettingsPipeHost(a => BeginInvoke(a), (p, result) => result(new Packet { Error = "Visual test only; operation disabled" }),
                delegate { ready = true; }, delegate { if (!complete) File.WriteAllText(Path.Combine(output, "failure.txt"), "Settings UI exited before both captures"); Close(); },
                message => File.AppendAllText(Path.Combine(output, "pipe.log"), message + Environment.NewLine));
            host.Start(binary, "zh-CN");
            host.Publish(new ViewState { Version = 1, Ready = true, CanClose = true, AudioKnown = true,
                StatusZh = "✓ 显示已就绪", StatusEn = "✓ Display ready", Whitelist = new string[0], Sources = new PlayingSource[0],
                Applied = new FormValues { Minutes = 3, Hotkey = "Ctrl+Alt+F10", Language = "zh-CN", AutomaticUpdates = true,
                    LimitEnabled = true, LimitMinutes = 60, InterfaceTheme = theme } });
            timer.Tick += Tick; timer.Start();
        };
        FormClosed += delegate { timer.Stop(); timer.Dispose(); if (host != null) { host.Close(); host.Dispose(); } };
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        int split = ClientSize.Width / 2;
        using (var blue = new SolidBrush(Color.FromArgb(22, 87, 178))) e.Graphics.FillRectangle(blue, 0, 0, split, ClientSize.Height);
        using (var orange = new SolidBrush(Color.FromArgb(231, 113, 37))) e.Graphics.FillRectangle(orange, split, 0, ClientSize.Width - split, ClientSize.Height);
        using (var font = new Font("Segoe UI", 21, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var title = new Font("Microsoft YaHei UI", 28, FontStyle.Bold, GraphicsUnit.Pixel))
        {
            for (int side = 0; side < 2; side++)
            {
                int x = side * split;
                e.Graphics.DrawString(side == 0 ? "外部背景 A · 蓝色" : "外部背景 B · 橙色", title, Brushes.White, x + 35, 26);
                for (int y = 100; y < ClientSize.Height; y += 94)
                {
                    e.Graphics.DrawString("BACKGROUND TEXT  0123456789  背后文字", font, Brushes.White, x + 35, y);
                    e.Graphics.DrawString("Sharp text / clear edges / blur test", font, Brushes.Black, x + 35, y + 30);
                    e.Graphics.FillRectangle(Brushes.White, x + 35, y + 68, 160, 5);
                    e.Graphics.FillRectangle(Brushes.Black, x + 195, y + 68, 160, 5);
                }
                // Large contrast shapes make blur distinguishable from merely
                // hiding small text under a uniform translucent tint.
                int shapeY=(ClientSize.Height-Math.Min(ClientSize.Height-190,(split-80)*5/7))/2+36;
                e.Graphics.FillRectangle(Brushes.White,x+540,shapeY,140,115);
                e.Graphics.FillRectangle(Brushes.Black,x+680,shapeY,140,115);
            }
        }
    }
    private void Tick(object sender, EventArgs args)
    {
        try
        {
            if (!ready) { if (++phase > 12) throw new Exception("UI did not report ready"); return; }
            if (window == IntPtr.Zero)
            {
                phase = 0;
                var child = (Process)typeof(SettingsPipeHost).GetField("child", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(host);
                child.Refresh(); window = child.MainWindowHandle; pid = child.Id; started = child.StartTime.ToUniversalTime();
                if (window == IntPtr.Zero) throw new Exception("Missing actual UI HWND");
                width = ClientSize.Width / 2 - 80; height = Math.Min(ClientSize.Height - 190, width * 5 / 7);
                firstX = screen.Left + 40; secondX = screen.Left + ClientSize.Width / 2 + 40;
                windowY = screen.Top + (ClientSize.Height - height) / 2;
                PositionWindow(firstX, false); return;
            }
            if (phase == 0) { if(!CaptureDesktop("蓝色背景实拍.png"))return; PositionWindow(secondX, true); phase = 1; return; }
            if (phase == 1)
            {
                if(!CaptureDesktop("橙色背景实拍.png"))return; complete = true;
                string hash;using(var sha=SHA256.Create())using(var file=File.OpenRead(binary))hash=BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");
                File.WriteAllText(Path.Combine(output, "complete.txt"),
                    "Binary=" + binary + "\r\nVersion=" + FileVersionInfo.GetVersionInfo(binary).FileVersion +
                    "\r\nSHA256="+hash+
                    "\r\nPID=" + pid + "\r\nStartUtc=" + started.ToString("O") + "\r\nSameHWND=" + window.ToInt64() +
                    "\r\nWindowSizePixels=" + width + "x" + height + "\r\nFirstXY=" + firstX + "," + windowY +
                    "\r\nSecondXY=" + secondX + "," + windowY + "\r\nCapture=Desktop CopyFromScreen; no PrintWindow or RenderTargetBitmap; images unedited" +
                    "\r\nTheme="+theme+"; same state and window throughout; external backdrop only; fake owner callbacks; no install or formal settings writes");
                phase = 2; return;
            }
            if (phase == 2) { host.Close(); phase = 3; return; }
            if (phase++ > 5) { host.Dispose(); Close(); }
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString()); if (host != null) host.Close(); timer.Stop(); Close(); }
    }
    private void PositionWindow(int x, bool moveOnly)
    {
        if (!SetWindowPos(window, new IntPtr(-1), x, windowY, width, height, moveOnly ? 0x0041u : 0x0040u)) throw new System.ComponentModel.Win32Exception();
        host.Activate(); SetForegroundWindow(window); DwmFlush();
        RECT r; if (!GetWindowRect(window, out r) || r.Left != x || r.Top != windowY || r.Right - r.Left != width || r.Bottom - r.Top != height)
            throw new Exception("Window identity/position check failed");
    }
    private bool CaptureDesktop(string name)
    {
        DwmFlush(); RECT r; GetWindowRect(window, out r);
        if(!IsWindowVisible(window) || IsIconic(window))throw new Exception("The test window is not visible; do not accept this capture");
        if(GetForegroundWindow()!=window){if(++focusRetries>3)throw new Exception("Foreground changed repeatedly; capture cancelled");host.Activate();SetForegroundWindow(window);return false;}
        focusRetries=0;
        int material;int result=DwmGetWindowAttribute(window,38,out material,4);
        if(result<0 || material!=3)throw new Exception("Desktop Acrylic is not attached; do not accept this as a glass capture");
        File.AppendAllText(Path.Combine(output, "positions.txt"), name + " HWND=" + window.ToInt64() + " rect=" + r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom + " visible=" + IsWindowVisible(window) + " minimized=" + IsIconic(window) + " foreground=" + GetForegroundWindow().ToInt64() + "\r\n");
        using (var bitmap = new Bitmap(screen.Width, screen.Height))
        {
            using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(screen.Location, Point.Empty, screen.Size);
            bitmap.Save(Path.Combine(output, name), ImageFormat.Png);
        }
        return true;
    }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int ht, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h,int attribute,out int value,int size);
}
