using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using ScreenOffTray.Ui;
using ScreenOffTray.Playback;

namespace ScreenOffTray.Tests
{
    internal static partial class CSettingsUiTests
    {
        private static void UiInitialDetails()
        {
            Color green=Color.FromArgb(0,127,120),header=Color.FromArgb(236,250,246);
            Verify(Contrast(green,Color.White)>=4.5,"#007F78 on white contrast >= 4.5");
            Verify(Contrast(green,header)>=4.5,"#007F78 on #ECFAF6 header contrast >= 4.5");
            Verify(MintTheme.Green==(SystemInformation.HighContrast ? SystemColors.HotTrack : green),"theme retains native high-contrast branch");
            geometry.Add("Green=#007F78 white contrast="+Contrast(green,Color.White)+" Header contrast="+Contrast(green,header));
            using(var host=new TextBox()) { IntPtr h=host.Handle; TextRenderer.MeasureText("host",host.Font); }
            using(SettingsDpiContext.EnterUi()) foreach(string lang in new[]{"zh-CN","en"}) using(var f=new Fixture(lang)) {
                // Use the product's normal Show/work-area adaptation. No manual
                // ClientSize, Bounds, Scale, fake DPI or work-area override.
                f.Form.Show(); Pump(250);
                Size initial=f.Form.ClientSize;
                uint dpi=GetDpiForWindow(f.Form.Handle);
                var viewport=Find<Panel>(f.Form,"viewport");
                var footer=Find<Control>(f.Form,"footer");
                Rectangle footerBounds=f.Form.RectangleToClient(footer.RectangleToScreen(footer.ClientRectangle));
                geometry.Add(lang+" initial Show HWND DPI="+dpi+" ClientSize="+initial+" logicalClient="+(initial.Width*96.0/dpi)+"x"+(initial.Height*96.0/dpi)+" Bounds="+f.Form.Bounds+" viewport="+viewport.Bounds+" verticalScroll="+viewport.VerticalScroll.Visible+" footer="+footerBounds);
                Verify(!f.Form.HorizontalScroll.Visible && !viewport.HorizontalScroll.Visible,lang+": initial window/body need no horizontal scrolling");
                foreach(string name in new[]{"playback-hint","playback-limit-hint"}) {
                    var label=Find<Label>(f.Form,name);
                    Size textSize=TextRenderer.MeasureText(label.Text,label.Font,new Size(Math.Max(1,label.Width),0),TextFormatFlags.NoPadding|TextFormatFlags.WordBreak);
                    Verify(label.Left>=0 && label.Right<=label.Parent.ClientSize.Width && textSize.Width<=label.Width && textSize.Height<=label.Height,lang+": "+name+" wraps fully within its column");
                    geometry.Add(lang+" "+name+" bounds="+label.Bounds+" measuredText="+textSize);
                }
                var version=Find<Label>(f.Form,"version");
                Verify(version.Text=="v0.1.9.5" && version.Right<=version.Parent.ClientSize.Width,lang+": four-part version fits initial footer");
                var headerPanel=Find<MintGradientPanel>(f.Form,"header"); var scene=Find<Control>(f.Form,"header-scene");
                var state=Find<Control>(f.Form,"display-state-line"); var recheck=Find<LinkLabel>(f.Form,"recheck");
                float headerScale=dpi/96F;
                foreach(int logicalWidth in new[]{600,650,760}) {
                    f.Form.RefreshSetup(new SetupPanelState {Visible=true,CanPrepare=true,Detail="Fixture setup required"});
                    f.Form.ClientSize=new Size((int)Math.Round(logicalWidth*headerScale),initial.Height); Pump(80);
                    Verify(!scene.Visible || Rectangle.Intersect(scene.Bounds,state.Bounds).IsEmpty,
                        lang+": decorative header scene never overlaps display status at "+logicalWidth+" logical px");
                    Rectangle checkBounds=f.Form.RectangleToClient(recheck.RectangleToScreen(recheck.ClientRectangle));
                    Point headerOrigin=f.Form.PointToClient(headerPanel.PointToScreen(Point.Empty));
                    Verify(checkBounds.Left>=headerOrigin.X && checkBounds.Right<=headerOrigin.X+headerPanel.ClientSize.Width &&
                        state.Left>=0 && state.Right<=headerPanel.ClientSize.Width,
                        lang+": long unready status keeps its check action inside header at "+logicalWidth+" logical px");
                }
                f.Form.RefreshSetup(new SetupPanelState {Ready=true});
                f.Form.ClientSize=initial; Pump(80);
                footerBounds=f.Form.RectangleToClient(footer.RectangleToScreen(footer.ClientRectangle));
                var footerRow=Find<TableLayoutPanel>(f.Form,"footer-row");
                int[] footerColumns=footerRow.GetColumnWidths();
                Verify(footerRow.ColumnCount==3 && footerColumns.Length>=3 && footerColumns[1]>0,lang+": botanical footer keeps a dedicated middle column");
                using(var image=DetailCapture(footerRow,lang+"-footer-flowers.png")) {
                    int colored=0;
                    int middleLeft=footerRow.Padding.Left+footerColumns[0];
                    int middleRight=Math.Min(image.Width,middleLeft+footerColumns[1]);
                    for(int y=0;y<image.Height;y++) for(int x=middleLeft;x<middleRight;x++) {
                        Color pixel=image.GetPixel(x,y);
                        int high=Math.Max(pixel.R,Math.Max(pixel.G,pixel.B));
                        int low=Math.Min(pixel.R,Math.Min(pixel.G,pixel.B));
                        if(high-low>=18) colored++;
                    }
                    Verify(colored>=20,lang+": botanical footer illustration actually paints colored pixels");
                }
                Verify(f.Form.ClientRectangle.Contains(footerBounds),lang+": fixed footer is within initial client");
                foreach(string name in new[]{"save","cancel"}) {
                    var button=Find<Button>(f.Form,name);
                    Rectangle bounds=f.Form.RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
                    Verify(button.Visible && button.Enabled && footerBounds.Contains(bounds),lang+": "+name+" is enabled and fully inside fixed footer");
                }
                using(var image=DetailCapture(f.Form,lang+"-initial-live.png")) { }
                var lastHint=Find<Control>(f.Form,"playback-limit-hint");
                viewport.ScrollControlIntoView(lastHint); Pump(100);
                Rectangle hintBounds=f.Form.RectangleToClient(lastHint.RectangleToScreen(lastHint.ClientRectangle));
                Verify(hintBounds.Bottom<=footerBounds.Top && hintBounds.Top>=viewport.Top,lang+": ordinary vertical scroll reveals complete final hint above footer");
                Verify(!viewport.HorizontalScroll.Visible && f.Form.ClientSize==initial && f.Form.RectangleToClient(footer.RectangleToScreen(footer.ClientRectangle))==footerBounds,lang+": scrolling preserves initial size and fixed footer");
                Click(Find<Button>(f.Form,lang=="en" ? "save" : "cancel"));
                Verify(f.Form.IsDisposed && f.Saves==(lang=="en" ? 1 : 0),lang+": footer action completes through fake callback only");
            }
        }
        private static void UiDetails(bool baseline)
        {
            using(var host = new TextBox()) { IntPtr h = host.Handle; TextRenderer.MeasureText("host",host.Font); }
            using(SettingsDpiContext.EnterUi()) foreach(string lang in new[]{"zh-CN","en"}) using(var f = new Fixture(lang)) {
                f.Form.StartPosition = FormStartPosition.Manual; f.Form.Location = new Point(30,30);
                f.Form.Show(); Pump(250);
                float scale = GetDpiForWindow(f.Form.Handle)/96F;
                geometry.Add(lang+" live HWND DPI="+GetDpiForWindow(f.Form.Handle));
                // Freeze only this fake UI's cat for comparable ink coordinates.
                var cat = Find<Control>(f.Form,"mint-cat");
                var number = Find<MintNumberBox>(f.Form,"timeout");
                var cap = Find<MintNumberBox>(f.Form,"playback-limit-minutes");
                number.Text = "27"; cap.Text = "123";
                var hotkey = Find<HotkeyCaptureBox>(f.Form,"hotkey");
                hotkey.RecordPhysicalKey((int)Keys.N,2); hotkey.RecordKey(Keys.N,true); string draft = hotkey.Text;
                int oldHeaderHeight = 0;
                foreach(bool narrow in new[]{false,true,false}) {
                    string phase = narrow ? "narrow" : oldHeaderHeight == 0 ? "wide" : "wide-return";
                    f.Form.ClientSize = new Size((int)((narrow ? 340 : 734)*scale),(int)(700*scale)); Pump(160);
                    number.Focus(); Pump(60);
                    string prefix = lang+"-"+phase;
                    using(var image = DetailCapture(f.Form,prefix+".png")) { }
                    var header = Find<Control>(f.Form,"header");
                    using(var image = DetailCapture(header,prefix+"-header.png")) {
                        if(narrow) {
                            int stale=0;
                            int oldRuleY=oldHeaderHeight-1;
                            if(oldRuleY>=0 && oldRuleY<image.Height)
                                for(int x=0;x<image.Width;x++) if(image.GetPixel(x,oldRuleY).ToArgb()==MintTheme.Line.ToArgb()) stale++;
                            geometry.Add(prefix+" old separator y="+(oldHeaderHeight-1)+" linePixels="+stale+" currentHeight="+header.Height);
                            if(!baseline) Verify(stale==0,prefix+": old separator cleared after height change without a forced test repaint");
                        }
                        Rectangle inkRegion=cat.Bounds;
                        Rectangle scan=Rectangle.Intersect(header.ClientRectangle,inkRegion); int top=image.Height,bottom=-1,left=image.Width,right=-1;
                        for(int y=scan.Top;y<scan.Bottom;y++) for(int x=scan.Left;x<scan.Right;x++) {
                            Color p=image.GetPixel(x,y);
                            if(p.R<175 && p.G<170 && p.B<170) { top=Math.Min(top,y); bottom=Math.Max(bottom,y); left=Math.Min(left,x); right=Math.Max(right,x); }
                        }
                        geometry.Add(prefix+" visible cat dark ink="+Rectangle.FromLTRB(left,top,right+1,bottom+1)+" host="+cat.Bounds+" header="+header.ClientSize);
                        if(!baseline) Verify(top>=Math.Round(4*scale) && bottom<header.Height-Math.Round(5*scale),prefix+": actual cat ink has top and separator clearance");
                    }
                    oldHeaderHeight=header.Height;
                    var frame=number.Parent;
                    using(var image=DetailCapture(frame,prefix+"-focus.png")) {
                        int y=(int)Math.Round(scale),start=(int)Math.Round(10*scale),end=image.Width-start,green=0;
                        for(int x=start;x<end;x++) if(IsMintStroke(image.GetPixel(x,y))) green++;
                        geometry.Add(prefix+" focus top y="+y+" green="+green+" straightWidth="+(end-start));
                        if(!baseline) Verify(green>=end-start-2,prefix+": focus top stroke remains continuous across white edit and button children");
                    }
                    if(!baseline) {
                        Verify(Find<TableLayoutPanel>(f.Form,"configuration").ColumnCount==(narrow ? 1 : 2),prefix+": retained column structure");
                        var version=Find<Label>(f.Form,"version");
                        Verify(version.Text=="v0.1.9.5" && version.Right<=version.Parent.ClientSize.Width,prefix+": version fixture fits footer");
                        var footer=Find<Control>(f.Form,"footer");
                        Verify(footer.Bottom<=footer.Parent.ClientSize.Height,prefix+": footer remains within window");
                    }
                }
                if(baseline) continue;
                Verify(Contrast(MintTheme.Green,Color.White)>=4.5,"mint green small text / white button text contrast >= 4.5");
                geometry.Add("Green="+ColorTranslator.ToHtml(MintTheme.Green)+" white contrast="+Contrast(MintTheme.Green,Color.White));
                string hint=Find<Label>(f.Form,"playback-limit-hint").Text;
                Verify(lang!="en" || hint.Contains("without physical input") && hint.Contains("playback no longer blocks display-off"),lang+": cap copy explains playback stops blocking display off");
                int value=(int)number.Value; Click(Find<Button>(f.Form,"timeout-plus"));
                Verify(number.Value==value+1,lang+": native edit plus button still changes draft");
                var arrow=new KeyEventArgs(Keys.Down);
                typeof(MintNumberBox).GetMethod("OnKeyDown",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(number,new object[]{arrow});
                Verify(number.Value==value && arrow.SuppressKeyPress,lang+": native edit arrow stepping retained");
                Find<Button>(f.Form,"timeout-plus").Focus(); Pump(40);
                using(var image=DetailCapture(number.Parent,lang+"-button-focus.png")) { }
                Verify(number.Parent.ContainsFocus,lang+": button focus belongs to stepper");
                f.Form.SelectNextControl(number,true,true,true,true);
                Verify(f.Form.ActiveControl!=number,lang+": Tab traversal can leave numeric edit");
                var panel=Find<Control>(f.Form,"playback-settings");
                int before=panel.Height;
                Click(Find<Button>(f.Form,"remove-whitelist")); Pump(100);
                var status=Find<Label>(f.Form,"whitelist-save-status");
                var instant=Find<Label>(f.Form,"whitelist-instant");
                Verify(f.ListSaves==1 && f.Saves==0 && !status.Visible && instant.Text==(lang=="en" ? "Saved" : "已保存"),lang+": successful immediate save stays beside title");
                Verify(panel.Height>=before && panel.Height-before<=40,
                    lang+": success adds no extra action row; viewport-driven card height before="+before+" after="+panel.Height);
                string listHint=Find<Label>(f.Form,"playback-hint").Text;
                Verify(listHint.Contains(lang=="en" ? "defer auto off" : "暂缓自动关屏"),lang+": list hint explains playback protection");
                int layouts=0; LayoutEventHandler count=(s,e)=>layouts++;
                panel.Layout+=count; f.Form.RefreshPlayback(); Pump(100); panel.Layout-=count;
                Verify(layouts==0 && instant.Text==(lang=="en" ? "Saved" : "已保存"),lang+": unchanged refresh preserves feedback without relayout");
                f.ListError="fixture denied: full error must remain readable; original list is unchanged.";
                Click(Find<Button>(f.Form,"add-whitelist-0")); Pump(100);
                Verify(status.Visible && status.Text.Contains(f.ListError) && !status.AutoEllipsis && f.State.AppliedOptions.WhitelistPaths.Length==0,lang+": complete save failure retains applied list and explicit error");
                Verify(instant.Text==(lang=="en" ? "Save failed" : "保存失败"),lang+": title does not falsely claim success after failure");
                f.Form.ClientSize=new Size((int)(340*scale),(int)(700*scale)); Pump(100);
                var viewport=Find<Panel>(f.Form,"viewport"); viewport.ScrollControlIntoView(status); Pump(100);
                using(var image=DetailCapture(f.Form,lang+"-narrow-error-bottom.png")) { }
                Point errorEnd=f.Form.PointToClient(status.PointToScreen(new Point(0,status.Height)));
                Point footerTop=f.Form.PointToClient(Find<Control>(f.Form,"footer").PointToScreen(Point.Empty));
                Verify(errorEnd.Y<=footerTop.Y,lang+": scrolled complete error bottom is above fixed footer");
                f.ListError=null; Click(Find<Button>(f.Form,"add-whitelist-0")); Pump(100);
                Verify(!status.Visible && instant.Text==(lang=="en" ? "Saved" : "已保存"),lang+": later success clears old failure");
                Verify(number.Text=="27" && cap.Text=="123" && hotkey.Text==draft && f.Saves==0,lang+": all unsaved drafts survive refresh and list success/failure");
                Click(Find<Button>(f.Form,"cancel"));
                Verify(f.Saves==0 && f.ListSaves==3 && f.State.AppliedOptions.WhitelistPaths.Length==1,lang+": Cancel retains immediately saved list and never saves drafts");
            }
        }
        private static bool IsMintStroke(Color p) { return p.R<60 && p.G>75 && p.G<165 && p.B>70 && p.B<170; }
        private static double Luminance(Color c) { return .2126*Linear(c.R)+.7152*Linear(c.G)+.0722*Linear(c.B); }
        private static double Linear(byte c) { double v=c/255.0; return v<=.04045 ? v/12.92 : Math.Pow((v+.055)/1.055,2.4); }
        private static double Contrast(Color a,Color b) { return (Math.Max(Luminance(a),Luminance(b))+.05)/(Math.Min(Luminance(a),Luminance(b))+.05); }
        private static Bitmap DetailCapture(Control c,string name)
        {
            IntPtr dc=GetDC(c.Handle);
            if(dc==IntPtr.Zero) throw new InvalidOperationException("GetDC failed; stop pixel interpretation: "+name);
            var bitmap=new Bitmap(c.ClientSize.Width,c.ClientSize.Height);
            try {
                using(var g=Graphics.FromImage(bitmap)) {
                    IntPtr dest=g.GetHdc(); bool copied;
                    try { copied=BitBlt(dest,0,0,bitmap.Width,bitmap.Height,dc,0,0,0x00CC0020); }
                    finally { g.ReleaseHdc(dest); }
                    if(!copied) throw new InvalidOperationException("BitBlt failed; stop pixel interpretation: "+name);
                }
                int ink=0,paper=0;
                for(int y=0;y<bitmap.Height;y++) for(int x=0;x<bitmap.Width;x++) { Color p=bitmap.GetPixel(x,y); if(p.R<160 && p.G<175 && p.B<180) ink++; if(p.R>185 && p.G>185 && p.B>185) paper++; }
                Check(ink>5 && paper>50,"live DC contains ink and paper: "+name);
                bitmap.Save(Path.Combine(output,name),ImageFormat.Png); return bitmap;
            } catch { bitmap.Dispose(); throw; }
            finally { ReleaseDC(c.Handle,dc); }
        }
    }
}
