using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using ScreenOffTray.Ui;
using ScreenOffTray.Playback;

namespace ScreenOffTray.Tests {
    internal static class SettingsHostUiHarness {
        [STAThread] private static int Main(string[] args) { return CSettingsUiTests.RunSettingsHost(args); }
    }
    internal static partial class CSettingsUiTests {
        internal static int RunSettingsHost(string[] args)
        {
            output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            if (!args.Contains("--private-child")) return LaunchPrivate(args);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s,System.Threading.ThreadExceptionEventArgs e) {
                File.WriteAllText(Path.Combine(output,"thread-error.txt"),e.Exception.ToString()); Environment.Exit(1);
            };
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try {
                using(SettingsDpiContext.EnterHost(new IntPtr(-1))) using(var host=new TextBox()) {
                    IntPtr h=host.Handle; TextRenderer.MeasureText("host",host.Font);
                    Check(GetDpiForWindow(h)==96,"host native caches initialized at 96 DPI");
                }
                if(args.Contains("--group=background-replay")) {
                    using(SettingsDpiContext.EnterUi())foreach(string lang in new[]{"zh-CN","en"})using(var f=new HostFixture(lang,"0.1.9.9")) {
                        Check(GetDpiForWindow(f.Form.Handle)==168,lang+": replay comparison requires actual 168 DPI after the 96 DPI host");
                        Check(new[]{"timeout","hotkey","language","whitelist-picker"}.All(name=>GetDpiForWindow(Find<Control>(f.Form,name).Handle)==168),lang+": native child replay HWNDs also use actual 168 DPI");
                        Size initialSize=f.Form.ClientSize;
                        CheckReplayStable(f.Form,lang+"-initial-host");
                        f.Form.ClientSize=new Size(1250,1050);Pump(100);
                        Check(Find<TableLayoutPanel>(f.Form,"configuration").ColumnCount==1 && !Find<Panel>(f.Form,"viewport").HorizontalScroll.Visible,lang+": resized replay uses the actual narrow layout without horizontal overflow");
                        var footer=Find<Control>(f.Form,"footer");Check(f.Form.ClientRectangle.Contains(f.Form.RectangleToClient(footer.RectangleToScreen(footer.ClientRectangle))),lang+": replay capture includes the complete fixed footer");
                        CheckReplayStable(f.Form,lang+"-resized-host");
                        f.State.Audio.Sources=new AudioSource[0];f.Form.RefreshPlayback();Pump(100);
                        CheckReplayStable(f.Form,lang+"-empty-playback");
                        f.Form.ClientSize=initialSize;Pump(100);
                        CheckReplayStable(f.Form,lang+"-wide-empty-playback");
                    }
                } else foreach(string lang in new[]{"zh-CN","en"}) HostSequence(lang);
                // These existing small transaction cases directly exercise the
                // altered acknowledgement path; not the old full UI matrices.
                if(!args.Contains("--group=background-replay")) { PreparationClose(); SynchronousAcknowledgement(); }
            } catch(Exception ex) { failures++; results.Add("FAIL "+ex); }
            results.Add("RESULT checks="+results.Count+" failures="+failures+"; private non-input desktop, actual 168 DPI, live GetDC/BitBlt with ink+paper; fake snapshots/callbacks, no product entry or settings/device calls.");
            File.WriteAllLines(Path.Combine(output,"results.txt"),results.ToArray(),Encoding.UTF8);
            File.WriteAllLines(Path.Combine(output,"geometry.txt"),geometry.ToArray(),Encoding.UTF8);
            Console.WriteLine(results.Last()); return failures==0 ? 0:1;
        }
        private sealed class HostFixture:IDisposable {
            internal readonly SettingsDialog Form;
            internal PlaybackPanelState State;
            internal SettingsValues Saved;
            internal int Saves,ListSaves,Prepares;
            internal string SaveError;
            internal HostFixture(string lang,string previewVersion="0.1.9.5") {
                IntPtr host=SettingsDpiContext.CaptureCurrent();
                var source=new AudioSource{ExePath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"notepad.exe"),DisplayName="Safe local source",ProcessIds=new[]{42},SessionCount=1};
                State=new PlaybackPanelState{Audio=new AudioSnapshot{Known=true,Sources=new[]{source}},AppliedOptions=new PlaybackOptions{WhitelistPaths=new[]{source.ExePath}},Timing=new AutoOffTimingResult(0,AutoOffPauseReason.None)};
                using(SettingsDpiContext.EnterUi()) {
                    Form=new SettingsDialog(new UiText(lang),new SettingsValues{TimeoutMinutes=3,Language=lang,Hotkey=HotkeyBinding.Default},
                        v=>{Saves++;Saved=v;return SaveError;},()=>{Prepares++;},()=>{},()=>{},()=>{},()=>{},previewVersion,
                        ()=>{using(SettingsDpiContext.EnterHost(host))return new PlaybackPanelState{Audio=State.Audio,AppliedOptions=State.AppliedOptions.Copy(),Timing=State.Timing,SettingsWarning=State.SettingsWarning};},
                        ()=>{},paths=>{ListSaves++;State.AppliedOptions.WhitelistPaths=paths;return null;});
                    Form.ConfigureWindow(()=>{});
                    Publish(false); Form.RefreshUpdate(false,"",false,false);
                    // Product Show performs the first playback read, not Fixture's
                    // older pre-read/CreateTree/PerformLayout preparation.
                    Form.Show(); Pump(120);
                    ((Timer)typeof(SettingsDialog).GetField("playbackTimer",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(Form)).Stop();
                }
            }
            internal void Publish(bool checking) { Form.RefreshSetup(new SetupPanelState{Ready=!checking,Checking=checking,CanPrepare=!checking,Visible=false,Detail=checking?"Checking safe snapshot":"Ready safe snapshot",Diagnostics="Safe snapshot"}); }
            internal void Combined() {Form.RefreshPlayback();Publish(false);Form.RefreshUpdate(false,"",false,false);}
            public void Dispose(){Form.Dispose();}
        }
        private sealed class HostCounts:IDisposable {
            internal int Layouts,ContentLayouts,Geometry,ZeroHeight;
            internal readonly Dictionary<string,int> Messages=new Dictionary<string,int>();
            internal readonly List<string> Sizes=new List<string>();
            readonly List<NativeCount> natives=new List<NativeCount>();
            readonly Control content;
            internal HostCounts(SettingsDialog form){content=Find<Control>(form,"content");Watch(form);}
            void Watch(Control c){
                c.Layout+=OnLayout;
                if(c==content)c.SizeChanged+=OnGeometry;
                if(c.IsHandleCreated)natives.Add(new NativeCount(c,this));
                foreach(Control child in c.Controls)Watch(child);
            }
            void OnLayout(object sender,LayoutEventArgs e){Layouts++;if(sender==content)ContentLayouts++;}
            void OnGeometry(object sender,EventArgs e){Geometry++;if(content.Height==0)ZeroHeight++;Sizes.Add(content.Size.ToString());}
            internal int Count(string key){int n;return Messages.TryGetValue(key,out n)?n:0;}
            internal void Clear(){Layouts=ContentLayouts=Geometry=ZeroHeight=0;Messages.Clear();Sizes.Clear();}
            internal void Record(string phase){geometry.Add(phase+" layouts="+Layouts+" contentLayouts="+ContentLayouts+" geometry="+Geometry+" zeroHeight="+ZeroHeight+" sizes="+String.Join(";",Sizes)+" messages="+String.Join(";",Messages.OrderBy(x=>x.Key).Select(x=>x.Key+":"+x.Value)));}
            public void Dispose(){foreach(var n in natives)n.ReleaseHandle();}
            sealed class NativeCount:NativeWindow {
                readonly Control control;readonly HostCounts owner;
                internal NativeCount(Control c,HostCounts o){control=c;owner=o;AssignHandle(c.Handle);}
                protected override void WndProc(ref Message m){
                    if(m.Msg==0xf || m.Msg==0x14 || m.Msg==0x47){string k=(m.Msg==0xf?"paint":m.Msg==0x14?"erase":"geometry")+"."+(control is SettingsDialog?"FORM":control.Name);owner.Messages[k]=owner.Count(k)+1;}
                    base.WndProc(ref m);
                }
            }
        }
        private static void HostSequence(string lang)
        {
            using(SettingsDpiContext.EnterUi()) using(var f=new HostFixture(lang)) {
                var form=f.Form;var viewport=Find<Panel>(form,"viewport");var content=Find<Control>(form,"content");var footer=Find<Control>(form,"footer");
                Check(GetDpiForWindow(form.Handle)==168,lang+": actual PMv2 HWND DPI=168");
                int initialDpi=(int)GetDpiForWindow(form.Handle);
                int initialLogicalHeight=lang=="en"?720:654;
                Check(form.ClientSize==new Size((int)Math.Round(984*initialDpi/96F),(int)Math.Round(initialLogicalHeight*initialDpi/96F)),lang+": accepted themed initial window footprint retained");
                Check(content.Height>0 && !viewport.HorizontalScroll.Visible,lang+": body remains nonzero and has no horizontal scroll");
                foreach(string name in new[]{"timeout","hotkey","language","whitelist-picker","playback-limit-minutes"}) {
                    Control c=Find<Control>(form,name);LogFont lf;
                    IntPtr font=UiSend(c.Handle,0x31,IntPtr.Zero,IntPtr.Zero);
                    int valid=GetObject(font,System.Runtime.InteropServices.Marshal.SizeOf(typeof(LogFont)),out lf);
                    Check(GetDpiForWindow(c.Handle)==168 && SettingsDpiContext.IsUiWindow(c.Handle) && valid>0 &&
                        lf.Height==-(int)Math.Round((name=="hotkey"?14:name=="language"||name=="whitelist-picker"?16:20)*168/72.0),lang+": "+name+" retains native PMv2 HWND and explicit pixel HFONT");
                }
                using(var b=DetailCapture(form,lang+"-initial.png")){}
                var number=Find<MintNumberBox>(form,"timeout");var limit=Find<MintNumberBox>(form,"playback-limit-minutes");
                number.Text="27";limit.Text="123";Pump(50);
                using(var stats=new HostCounts(form)) {
                    stats.Clear();for(int i=0;i<3;i++){f.Combined();Pump(25);}stats.Record(lang+" same combined x3");
                    Check(stats.ContentLayouts==0 && stats.Geometry==0 && stats.ZeroHeight==0,lang+": combined unchanged host Setup/Update/playback causes no content layout or geometry");
                    Check(stats.Count("paint.FORM")==0 && stats.Count("erase.configuration")==0 && stats.Count("paint.configuration")==0 && stats.Count("paint.footer")==0,lang+": unchanged combined callbacks do not repaint broad surfaces");
                    stats.Clear();f.Publish(true);Pump(30);f.Publish(false);Pump(30);stats.Record(lang+" Checking roundtrip");
                    Check(stats.Geometry==0 && stats.ZeroHeight==0 && stats.Count("paint.configuration")==0,lang+": Checking roundtrip updates header without body geometry or configuration paint");
                    Check(number.Text=="27" && limit.Text=="123",lang+": host refresh preserves numeric drafts");
                    form.RefreshUpdate(true,"Safe checking",false,false);Pump(30);
                    form.RefreshUpdate(false,"Safe update failure",false,true);Pump(30);
                    Check(Find<Label>(form,"update-detail").Text=="Safe update failure" && Find<LinkLabel>(form,"check-updates").Enabled,lang+": update failure text and retry gate remain live");
                    stats.Clear();for(int i=0;i<3;i++){form.RefreshPlayback();f.Publish(false);form.RefreshUpdate(false,"Safe update failure",false,true);Pump(20);}stats.Record(lang+" unchanged failure combined");
                    Check(stats.ContentLayouts==0 && stats.Geometry==0 && stats.ZeroHeight==0,lang+": unchanged failed-update host sequence also avoids body layout");
                    form.RefreshUpdate(false,"",false,false);Pump(30);
                    viewport.ScrollControlIntoView(Find<Control>(form,"playback-limit-hint"));Pump(40);
                    Point scroll=viewport.AutoScrollPosition;
                    stats.Clear();f.Combined();Pump(40);stats.Record(lang+" scrolled unchanged");
                    Check(viewport.AutoScrollPosition==scroll && stats.Geometry==0,lang+": unchanged host refresh preserves scrolling");
                    f.State.Audio.Sources=new AudioSource[0];form.RefreshPlayback();Pump(40);
                    Check(Find<Control>(form,"playing-empty").Visible,lang+": actual source removal updates the list");
                    f.State.Audio.Sources=new[]{new AudioSource{ExePath=f.State.AppliedOptions.WhitelistPaths[0],DisplayName="Returned safe source",ProcessIds=new[]{42},SessionCount=1}};
                    form.RefreshPlayback();viewport.AutoScrollPosition=Point.Empty;Pump(40);
                    var originalSource=f.State.Audio.Sources[0];int oldBody=content.Height;
                    f.State.Audio.Sources=new[]{originalSource,new AudioSource{ExePath=@"C:\SafeFixture\player-two.exe",DisplayName="Second safe source"},new AudioSource{ExePath=@"C:\SafeFixture\player-three.exe",DisplayName="Third safe source"}};
                    form.RefreshPlayback();Pump(40);
                    Check(content.Height>oldBody && Find<Control>(form,"playing-source-2").Visible,lang+": source growth updates real body height");
                    f.State.Audio.Sources=new[]{originalSource};form.RefreshPlayback();Pump(40);
                    Click(Find<Button>(form,"remove-whitelist"));Pump(40);
                    Check(f.ListSaves==1 && f.State.AppliedOptions.WhitelistPaths.Length==0 && f.Saves==0,lang+": whitelist removal remains an immediate independent save");
                    Click(Find<Button>(form,"add-whitelist-0"));Pump(40);
                    Check(f.ListSaves==2 && f.State.AppliedOptions.WhitelistPaths.Length==1,lang+": source add returns to applied list");
                    form.RefreshSetup(new SetupPanelState{Visible=true,CanPrepare=true,Detail="Safe failure detail",Diagnostics="Full safe diagnostic"});Pump(50);
                    Check(Find<Control>(form,"setup").Visible && Find<Label>(form,"setup-detail").Text=="Safe failure detail",lang+": changed failure detail remains visible");
                    Link(Find<LinkLabel>(form,"details"));Pump(40);
                    Check(Find<TextBox>(form,"diagnostics").Visible && Find<TextBox>(form,"diagnostics").Text.Contains("Full safe diagnostic"),lang+": expanded full diagnostic remains available");
                    f.Publish(false);Pump(40);
                    form.RefreshSetup(new SetupPanelState{Visible=true,CanPrepare=true,Detail="Prepare safe snapshot"});Pump(30);
                    Click(Find<Button>(form,"prepare"));Pump(30);
                    Check(f.Prepares==1 && !Find<Control>(form,"configuration").Enabled,lang+": pending prepare request gates drafts");
                    form.RefreshSetup(new SetupPanelState{Visible=true,CanPrepare=true,Detail="Prepare safe snapshot"});Pump(30);
                    Check(Find<Control>(form,"configuration").Enabled && Find<Button>(form,"prepare").Enabled,lang+": same incoming snapshot still acknowledges rejected prepare and clears pending gate");
                    f.Publish(false);Pump(30);
                    form.ClientSize=new Size((int)Math.Round(340*initialDpi/96F),(int)Math.Round((initialLogicalHeight+100)*initialDpi/96F));Pump(150);
                    Check(Find<TableLayoutPanel>(form,"configuration").ColumnCount==1,lang+": narrow actual-DPI window stacks; viewport="+viewport.ClientSize+" content="+Find<Control>(form,"content").Bounds+" config="+Find<Control>(form,"configuration").Bounds+" columns="+Find<TableLayoutPanel>(form,"configuration").ColumnCount+" pending="+Convert.ToInt32(typeof(SettingsDialog).GetField("pendingLayout",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form))+" queued="+typeof(SettingsDialog).GetField("layoutQueued",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form)+" presentation="+typeof(SettingsDialog).GetField("presentationDepth",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form)+" sizing="+typeof(SettingsDialog).GetField("sizing",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form));
                    Check(!viewport.HorizontalScroll.Visible,lang+": narrow actual-DPI window has no horizontal scrolling; viewport="+viewport.ClientSize+" content="+Find<Control>(form,"content").Bounds+" config="+Find<Control>(form,"configuration").Bounds);
                    using(var b=DetailCapture(form,lang+"-narrow.png")){}
                    form.ClientSize=new Size((int)Math.Round(984*initialDpi/96F),(int)Math.Round(initialLogicalHeight*initialDpi/96F));Pump(150);
                    Check(Find<TableLayoutPanel>(form,"configuration").ColumnCount==2,lang+": widening restores columns; viewport="+viewport.ClientSize+" content="+Find<Control>(form,"content").Bounds+" config="+Find<Control>(form,"configuration").Bounds);
                    Check(footer.Bottom<=form.ClientSize.Height,lang+": widening keeps fixed footer; viewport="+viewport.ClientSize+" content="+Find<Control>(form,"content").Bounds+" footer="+footer.Bounds);
                    form.ClientSize=new Size((int)Math.Round(984*initialDpi/96F),(int)Math.Round((initialLogicalHeight+100)*initialDpi/96F));Pump(150);
                    Check(!viewport.VerticalScroll.Visible && !viewport.HorizontalScroll.Visible,lang+": taller viewport releases obsolete scroll range; form="+form.ClientSize+" viewport="+viewport.ClientSize+" content="+Find<Control>(form,"content").Bounds+" config="+Find<Control>(form,"configuration").Bounds+" vscroll="+viewport.VerticalScroll.Visible+" hscroll="+viewport.HorizontalScroll.Visible+" pending="+Convert.ToInt32(typeof(SettingsDialog).GetField("pendingLayout",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form))+" queued="+typeof(SettingsDialog).GetField("layoutQueued",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form));
                    form.ClientSize=new Size((int)Math.Round(984*initialDpi/96F),(int)Math.Round(480*initialDpi/96F));Pump(150);
                    Check(viewport.VerticalScroll.Visible && content.Height>0,lang+": compact height restores needed vertical scroll without collapse");
                    viewport.ScrollControlIntoView(Find<Control>(form,"playback-limit-hint"));Pump(40);
                    Point end=form.PointToClient(Find<Control>(form,"playback-limit-hint").PointToScreen(new Point(0,Find<Control>(form,"playback-limit-hint").Height)));
                    Check(end.Y<=footer.Top,lang+": final body hint scrolls above fixed footer");
                    Check(stats.ZeroHeight==0,lang+": real list/state/width/height changes never collapse content to height zero");
                    viewport.AutoScrollPosition=Point.Empty;f.SaveError="Safe save rejection";Click(Find<Button>(form,"save"));Pump(40);
                    Check(f.Saves==1 && !form.IsDisposed && f.Saved.TimeoutMinutes==27 && f.Saved.PlaybackLimitMinutes==123,lang+": failed save receives and retains current drafts");
                    Check(Find<Label>(form,"error").Visible,lang+": failed save error stays visible");
                    f.SaveError=null;Click(Find<Button>(form,"save"));Pump(30);
                    Check(form.IsDisposed && f.Saves==2 && form.DialogResult==DialogResult.OK,lang+": successful save closes only this modeless settings view");
                }
            }
        }
    }
}
