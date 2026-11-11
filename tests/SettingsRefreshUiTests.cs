using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ScreenOffTray.Ui;
using ScreenOffTray.Playback;
namespace ScreenOffTray.Tests
{
    internal static partial class CSettingsUiTests
    {
        private static void ComboConfirm()
        {
            using(var host=new TextBox()) { IntPtr handle=host.Handle; TextRenderer.MeasureText("host",host.Font); }
            using(SettingsDpiContext.EnterUi()) foreach(string lang in new[]{"zh-CN","en"}) using(var f=new Fixture(lang)) {
                f.State.AppliedOptions.WhitelistPaths=new string[0]; f.State.Timing=new AutoOffTimingResult(0,AutoOffPauseReason.None); f.Form.RefreshPlayback();
                f.Form.StartPosition=FormStartPosition.Manual; f.Form.Location=new Point(30,30); f.Form.Show(); Pump(500);
                foreach(string name in new[]{"language","whitelist-picker"}) {
                    var c=Find<ComboBox>(f.Form,name); ClientRect native; GetClientRect(c.Handle,out native);
                    Verify(GetDpiForWindow(c.Handle)==GetDpiForWindow(f.Form.Handle) && SettingsDpiContext.IsUiWindow(c.Handle),lang+": "+name+" HWND inherits attached PMv2 parent DPI");
                    Verify(native.Right==c.ClientSize.Width && native.Bottom==c.ClientSize.Height,lang+": "+name+" managed/native client bounds agree");
                }
                var hotkey=Find<HotkeyCaptureBox>(f.Form,"hotkey");
                Verify(Math.Abs(hotkey.Top-(hotkey.Parent.Height-hotkey.Height)/2)<=1,lang+": native shortcut is vertically centered");
                CaptureLive(f.Form,lang+"-live-before.png"); Pump(2300); CaptureLive(f.Form,lang+"-live-after.png");
                RefreshInteractions(f,lang,GetDpiForWindow(f.Form.Handle));
            }
        }
        private static void ComboDiagnostic()
        {
            using(var host=new TextBox()) { IntPtr handle=host.Handle; TextRenderer.MeasureText("host",host.Font); }
            using(SettingsDpiContext.EnterUi()) using(var f=new Fixture("zh-CN")) {
                f.Form.StartPosition=FormStartPosition.Manual; f.Form.Location=new Point(30,30); f.Form.Show(); Pump(150);
                foreach(string name in new[]{"settings-window","language","whitelist-picker","timeout"}) {
                    Control c=Find<Control>(f.Form,name); IntPtr context=GetWindowDpiAwarenessContext(c.Handle);
                    UiRect rect; GetWindowRect(c.Handle,out rect); ClientRect client; GetClientRect(c.Handle,out client);
                    IntPtr dc=GetDC(c.Handle); int dcError=Marshal.GetLastWin32Error();
                    geometry.Add(name+" hwnd="+c.Handle+" dpi="+GetDpiForWindow(c.Handle)+" awareness="+GetAwarenessFromDpiAwarenessContext(context)+" ctx="+context+" managed="+c.Bounds+" preferred="+c.PreferredSize+" nativeclient="+client.Right+"x"+client.Bottom+" screen="+rect.Left+","+rect.Top+","+rect.Right+","+rect.Bottom+" dc="+dc+" error="+dcError);
                    if(c is ComboBox) Verify(c.PreferredSize.Height==c.Height,"native combo preferred height matches actual row: "+name);
                    if(dc!=IntPtr.Zero) ReleaseDC(c.Handle,dc);
                    using(SettingsDpiContext.EnterHost(context)) {
                        dc=GetDC(c.Handle); GetClientRect(c.Handle,out client);
                        geometry.Add(name+" own context DC="+dc+" nativeclient="+client.Right+"x"+client.Bottom);
                        if(dc!=IntPtr.Zero) ReleaseDC(c.Handle,dc);
                    }
                }
                foreach(string name in new[]{"viewport","content","configuration","basic-settings","basic-fields","playback-settings","playing-viewport","whitelist-heading","whitelist-line","whitelist-selection","playback-hint","protection-limit-line","playback-limit-hint"}) {
                    Control c=Find<Control>(f.Form,name);
                    geometry.Add("LAYOUT "+name+" bounds="+c.Bounds+" preferred="+c.PreferredSize+" margin="+c.Margin+" padding="+c.Padding+" visible="+c.Visible);
                    var table=c as TableLayoutPanel;
                    if(table!=null) geometry.Add("ROWS "+name+" "+String.Join(",",table.GetRowHeights().Select(n=>n.ToString())));
                }
                Verify(!Find<Panel>(f.Form,"viewport").VerticalScroll.Visible,"wide current-DPI body fits without excess combo preferred-height gaps");
                CaptureLive(f.Form,"diagnostic-live-spacing.png");
            }
        }
        private static void SettingsRefreshChecks(bool baseline)
        {
            // Product initializes its native tray/menu controls before opening the
            // window-local PMv2 view. Prime the same WinForms HFONT cache at 96.
            using (SettingsDpiContext.EnterHost(new IntPtr(-1)))
            using (var hostFont = new Font(SystemFonts.MessageBoxFont,FontStyle.Regular))
            using (var host = new TextBox { Font = hostFont, Text = "host" }) {
                IntPtr h = host.Handle;
                TextRenderer.MeasureText("host",host.Font);
                geometry.Add("host initialized before UI: DPI="+GetDpiForWindow(h)+" hfont="+UiSend(h,0x31,IntPtr.Zero,IntPtr.Zero));
            }
            using (SettingsDpiContext.EnterUi()) {
                foreach (string lang in baseline ? new[] { "zh-CN" } : new[] { "zh-CN", "en" }) {
                    using (var f = new Fixture(lang)) {
                        f.State.AppliedOptions.WhitelistPaths = new string[0];
                        f.State.Timing = new AutoOffTimingResult(0, AutoOffPauseReason.None);
                        f.Form.RefreshPlayback();
                        f.Form.StartPosition = FormStartPosition.Manual; f.Form.Location = new Point(30,30);
                        f.Form.Show(); Pump(500);
                        uint dpi = GetDpiForWindow(f.Form.Handle);
                        geometry.Add(lang+" actual HWND DPI="+dpi+" system DPI="+GetDpiForSystem());
                        Verify(dpi >= 96 && dpi == GetDpiForSystem(), lang+": actual HWND uses the current primary monitor DPI");
                        foreach (string name in new[] { "timeout", "hotkey", "playback-limit-minutes", "language", "whitelist-picker" }) {
                            Control c = Find<Control>(f.Form,name);
                            IntPtr font = UiSend(c.Handle,0x31,IntPtr.Zero,IntPtr.Zero);
                            IntPtr dc = GetDC(c.Handle), old = SelectObject(dc,font);
                            TextMetric metrics; bool ok = GetTextMetrics(dc,out metrics);
                            LogFont lf; int valid = GetObject(font,Marshal.SizeOf(typeof(LogFont)),out lf);
                            ClientRect native; GetClientRect(c.Handle,out native);
                            geometry.Add(lang+" "+name+" hwnd="+c.Handle+" hfont="+font+" valid="+valid+" lfHeight="+lf.Height+" tmHeight="+metrics.Height+" font="+c.Font.Size+" expectedPixels="+c.Font.GetHeight(GetDpiForSystem())+" window="+c.Bounds+" nativeClient="+native.Right+"x"+native.Bottom+" visible="+c.Visible);
                            SelectObject(dc,old); ReleaseDC(c.Handle,dc);
                            int expectedHeight=-(int)Math.Round((name=="hotkey" ? 14 : name=="language"||name=="whitelist-picker" ? 16 : 20)*dpi/72.0);
                            if (!baseline) Verify(font!=IntPtr.Zero && valid>0 && ok && lf.Height == expectedHeight && metrics.Height >= c.Font.GetHeight(GetDpiForSystem())*.85,lang+": "+name+" native LOGFONT height matches actual HWND DPI");
                        }
                        CaptureLive(f.Form,lang+"-live-before.png");
                        int layouts=0; var configuration=Find<Control>(f.Form,"content");
                        LayoutEventHandler handler=(s,e)=>layouts++;
                        configuration.Layout+=handler;
                        for(int i=0;i<3;i++) { f.Form.RefreshPlayback(); Pump(60); }
                        int directLayouts=layouts; layouts=0; Pump(2200);
                        geometry.Add(lang+" same snapshot direct refresh content layouts="+directLayouts+" live timer layouts="+layouts);
                        if(!baseline) Verify(directLayouts==0 && layouts==0,lang+": identical explicit and real timer refreshes do not relayout configuration");
                        configuration.Layout-=handler;
                        CaptureLive(f.Form,lang+"-live-after.png");
                        if(!baseline) {
                            f.State.SettingsWarning="fixture warning"; f.Form.RefreshPlayback(); Pump(50);
                            Verify(Find<Control>(f.Form,"playback-warning").Visible,lang+": changed diagnostics become visible");
                            f.State.SettingsWarning=null; f.Form.RefreshPlayback(); Pump(50);
                            Verify(!Find<Control>(f.Form,"playback-warning").Visible,lang+": cleared diagnostics disappear");
                        }
                        if(!baseline) RefreshInteractions(f,lang,dpi);
                    }
                }
            }
        }
        private static void RefreshInteractions(Fixture f,string lang,uint dpi)
        {
            var timeout=Find<MintNumberBox>(f.Form,"timeout"); timeout.Text="27";
            var limit=Find<MintNumberBox>(f.Form,"playback-limit-minutes"); limit.Text="123";
            var hotkey=Find<HotkeyCaptureBox>(f.Form,"hotkey"); hotkey.RecordPhysicalKey((int)Keys.N,2); hotkey.RecordKey(Keys.N,true);
            string draft=hotkey.Text;
            foreach(string name in new[]{"language","whitelist-picker"}) {
                var combo=Find<ComboBox>(f.Form,name);
                Verify(combo.Visible && combo.Width>30 && combo.Height>20 && combo.Items.Count>0 && !String.IsNullOrWhiteSpace(combo.Text),lang+": "+name+" visible bounds and nonempty caption");
                combo.Focus(); UiSend(combo.Handle,0x100,new IntPtr((int)Keys.F4),IntPtr.Zero);
                Verify(combo.DroppedDown,lang+": native F4 opens "+name);
                UiSend(combo.Handle,0x100,new IntPtr((int)Keys.Down),IntPtr.Zero);
                UiSend(combo.Handle,0x100,new IntPtr((int)Keys.Return),IntPtr.Zero);
                Verify(!combo.DroppedDown,lang+": native Enter closes "+name);
                combo.DroppedDown=true;
                UiSend(combo.Handle,0x100,new IntPtr((int)Keys.Escape),IntPtr.Zero);
                Verify(!combo.DroppedDown && !f.Form.IsDisposed,lang+": native Escape closes only "+name);
                if(name=="language") combo.SelectedIndex=lang=="en" ? 1 : 0;
            }
            f.State.Audio.Sources=new[]{new AudioSource {ExePath=@"C:\CSettingsFixture\changed.exe",DisplayName="Changed source"},new AudioSource {ExePath=@"C:\CSettingsFixture\second.exe",DisplayName="Second source"}};
            f.Form.RefreshPlayback(); Pump(100);
            Verify(Find<Control>(f.Form,"playing-source-0").Text=="Changed source" && Find<Control>(f.Form,"playing-source-1").Visible,lang+": changed snapshot updates source list");
            Click(Find<Button>(f.Form,"add-whitelist-0")); Pump(60);
            Verify(f.ListSaves==1 && Find<ComboBox>(f.Form,"whitelist-picker").Text.Contains("Changed source"),lang+": add uses fake list callback and updates picker");
            Click(Find<Button>(f.Form,"remove-whitelist")); Pump(60);
            Verify(f.ListSaves==2 && Find<ComboBox>(f.Form,"whitelist-picker").Items.Count==1,lang+": remove restores nonempty placeholder");
            string[] duplicatePaths={@"C:\Audio\FirstPlayer\audio.exe",@"C:\Audio\SecondPlayer\audio.exe"};
            timeout.Focus(); Pump(30);
            f.State.Audio.Sources=duplicatePaths.Select(p=>new AudioSource { ExePath=p, DisplayName="Player with shared name" }).ToArray();
            f.State.AppliedOptions.WhitelistPaths=duplicatePaths;
            f.Form.RefreshPlayback(); Pump(80);
            var picker=Find<MintComboBox>(f.Form,"whitelist-picker");
            using(var canvas=new Bitmap(1600,Math.Max(1,picker.Font.Height+8)))
            using(var graphics=Graphics.FromImage(canvas)) {
                graphics.Clear(Color.White);
                var draw=new DrawItemEventArgs(graphics,picker.Font,new Rectangle(0,0,canvas.Width,canvas.Height),0,DrawItemState.None);
                typeof(MintComboBox).GetMethod("OnDrawItem",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(picker,new object[]{draw});
                int right=0;
                for(int x=0;x<canvas.Width;x++) for(int y=0;y<canvas.Height;y++) {
                    Color pixel=canvas.GetPixel(x,y);
                    if(pixel.R<110 && pixel.G<155 && pixel.B<165) right=Math.Max(right,x+1);
                }
                Verify(right>0 && picker.DropDownWidth>=Math.Min(900,right+(int)Math.Ceiling(13*dpi/96F)+SystemInformation.VerticalScrollBarWidth),lang+": popup width fits actually rendered duplicate-name caption and path");
                Verify(picker.Items[0].ToString()!=picker.Items[1].ToString(),lang+": duplicate app names retain distinguishing paths");
            }
            f.State.AppliedOptions.WhitelistPaths=new string[0];
            f.State.Audio.Sources=new AudioSource[0]; f.Form.RefreshPlayback(); Pump(100);
            Verify(Find<Control>(f.Form,"playing-empty").Visible,lang+": source removal updates empty state");
            Verify(timeout.Text=="27" && limit.Text=="123" && hotkey.Text==draft && f.Saves==0,lang+": unsaved numeric and shortcut drafts survive all refresh/list actions");
            float scale=dpi/96F;
            foreach(bool narrow in new[]{true,false}) {
                f.Form.ClientSize=new Size((int)((narrow ? 340 : 984)*scale),(int)(700*scale)); Pump(150);
                var columns=Find<TableLayoutPanel>(f.Form,"configuration");
                Verify(columns.ColumnCount==(narrow ? 1 : 2),lang+": "+(narrow ? "narrow single" : "wide double")+" column retained");
                var cat=Find<Control>(f.Form,"mint-cat"); var header=Find<Control>(f.Form,"header");
                Verify(header.Height>=cat.Bottom,lang+": cat has space above header separator");
                CaptureLive(f.Form,lang+(narrow ? "-narrow.png" : "-wide.png"));
            }
        }
        private static void CaptureLive(Control c,string name)
        {
            // Actual live window DC after message pumping: never WM_PRINT/PrintWindow/DrawToBitmap.
            var diagnosticForm=c as SettingsDialog;
            if(diagnosticForm!=null) {
                var vp=Find<Panel>(c,"viewport");var co=Find<Control>(c,"content");var cf=Find<TableLayoutPanel>(c,"configuration");
                geometry.Add(name+" LAYOUT form="+c.ClientSize+" viewport="+vp.ClientSize+" content="+co.Size+" columns="+cf.ColumnCount+" config="+cf.Size+" layoutScale="+typeof(SettingsDialog).GetField("layoutScale",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(c)+" measuredWidth="+typeof(SettingsDialog).GetField("measuredWidth",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(c));
            }
            IntPtr source=GetDC(c.Handle);
            using(var bitmap=new Bitmap(c.ClientSize.Width,c.ClientSize.Height)) {
                using(var g=Graphics.FromImage(bitmap)) { IntPtr dest=g.GetHdc(); try { Verify(BitBlt(dest,0,0,bitmap.Width,bitmap.Height,source,0,0,0x00CC0020),"live HWND DC capture "+name); } finally { g.ReleaseHdc(dest); ReleaseDC(c.Handle,source); } }
                bitmap.Save(Path.Combine(output,name),ImageFormat.Png);
                if(name.Contains("-live-")) foreach(string comboName in new[]{"language","whitelist-picker"}) CaptureComboFace(Find<ComboBox>(c,comboName),name);
            }
        }
        private static void CaptureComboFace(ComboBox combo,string name)
        {
            // A visible child may lie below the scroll viewport. Its clipped DC
            // is black on a private desktop, which says nothing about its paint.
            var form=(SettingsDialog)combo.FindForm();
            var viewport=Find<Panel>(form,"viewport");
            Point previousScroll=viewport.AutoScrollPosition;
            if(form.AutoScroll) form.ScrollControlIntoView(combo); else viewport.ScrollControlIntoView(combo);
            Pump(50);
            ClientRect client; GetClientRect(combo.Handle,out client);
            IntPtr dc=GetDC(combo.Handle);
            using(var image=new Bitmap(client.Right,client.Bottom)) {
                bool captured; int error;
                using(var g=Graphics.FromImage(image)) { IntPtr target=g.GetHdc(); try { captured=dc!=IntPtr.Zero && BitBlt(target,0,0,image.Width,image.Height,dc,0,0,0x00CC0020); error=Marshal.GetLastWin32Error(); } finally { g.ReleaseHdc(target); ReleaseDC(combo.Handle,dc); } }
                Verify(captured,name+": own HWND DC capture "+combo.Name);
                if(!captured) { geometry.Add(name+" "+combo.Name+" CAPTURE_UNAVAILABLE LastError="+error+"; no pixel interpretation"); return; }
                int ink=0,paper=0,area=Math.Max(1,(image.Width-6)*(image.Height-6));
                for(int y=3;y<image.Height-3;y++) for(int x=3;x<image.Width-3;x++) { Color p=image.GetPixel(x,y); if(p.R<110 && p.G<155 && p.B<165) ink++; if(p.R>180 && p.G>180 && p.B>180) paper++; }
                image.Save(Path.Combine(output,Path.GetFileNameWithoutExtension(name)+"-"+combo.Name+"-own-hwnd.png"),ImageFormat.Png);
                geometry.Add(name+" "+combo.Name+" own HWND live ink="+ink+" paper="+paper+" area="+area+" nativeClient="+client.Right+"x"+client.Bottom);
                if(!output.Contains("baseline")) Verify(ink>80 && ink<area*.55 && paper>area*.25,name+": "+combo.Name+" own live DC contains text on a light face");
            }
            viewport.AutoScrollPosition=new Point(-previousScroll.X,-previousScroll.Y);Pump(30);
        }
        [StructLayout(LayoutKind.Sequential,CharSet=System.Runtime.InteropServices.CharSet.Unicode)] private struct TextMetric { internal int Height,Ascent,Descent,InternalLeading,ExternalLeading,AveCharWidth,MaxCharWidth,Weight,Overhang,DigitizedAspectX,DigitizedAspectY; internal char FirstChar,LastChar,DefaultChar,BreakChar; internal byte Italic,Underlined,StruckOut,PitchAndFamily,CharSet; }
        [StructLayout(LayoutKind.Sequential,CharSet=System.Runtime.InteropServices.CharSet.Unicode)] private struct LogFont { internal int Height,Width,Escapement,Orientation,Weight; internal byte Italic,Underline,StrikeOut,CharSet,OutPrecision,ClipPrecision,Quality,PitchAndFamily; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] internal string Face; }
        [DllImport("user32.dll",EntryPoint="SendMessageW")] private static extern IntPtr UiSend(IntPtr h,int m,IntPtr w,IntPtr l);
        [StructLayout(LayoutKind.Sequential)] private struct UiRect { internal int Left,Top,Right,Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h,out UiRect r);
        [DllImport("user32.dll",SetLastError=true)] private static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h,IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
        [DllImport("gdi32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)] private static extern bool GetTextMetrics(IntPtr dc,out TextMetric metric);
        [DllImport("gdi32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetObject(IntPtr obj,int count,out LogFont font);
        [DllImport("gdi32.dll",SetLastError=true)] private static extern bool BitBlt(IntPtr dest,int x,int y,int w,int h,IntPtr source,int sx,int sy,uint rop);
    }
}

