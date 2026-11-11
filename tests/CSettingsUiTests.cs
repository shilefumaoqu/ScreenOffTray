using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using ScreenOffTray.Playback;
using ScreenOffTray.Ui;
namespace ScreenOffTray.Tests
{
    // Reuses the established hidden-window fixture and transaction tests. Only UI
    // and pure contracts are compiled; no settings store, coordinator or devices.
    internal static partial class CSettingsUiTests
    {
        private static readonly List<string> results = new List<string>();
        private static readonly List<string> geometry = new List<string>();
        private static string output;
        private static int failures;
        [STAThread] private static int Main(string[] args)
        {
            output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            File.AppendAllText(Path.Combine(output,"ui-bootstrap.txt"),String.Join("|",args)+Environment.NewLine);
            if(!args.Contains("--private-child")) return LaunchPrivate(args);
            // Never SwitchDesktop: all shown fake windows stay off the user's desktop.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender,System.Threading.ThreadExceptionEventArgs e) {
                File.AppendAllText(Path.Combine(output,"ui-native-exception.txt"),e.Exception+Environment.NewLine);
                Environment.Exit(1);
            };
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if(args.Contains("--probe")) return Probe();
            if(args.Contains("--rebuild-probe")) return ProbeRebuild();
            if(args.Contains("--timeout-probe")) { System.Threading.Thread.Sleep(30000); return 9; }
            string group=args.FirstOrDefault(a=>a.StartsWith("--group=")); group=group==null ? "all" : group.Substring(8);
            try {
                if(group=="background-replay") BackgroundReplayChecks();
                else if(group=="resize-paint") ResizePaintChecks();
                else if(group=="language-switch") LanguageSwitch();
                else if(group=="ui-details-initial") UiInitialDetails();
                else if(group.StartsWith("ui-details-")) UiDetails(group.EndsWith("baseline"));
                else if(group=="refresh-combo-diagnostic") ComboDiagnostic();
                else if(group=="refresh-combo-confirm") ComboConfirm();
                else if(group.StartsWith("refresh-")) SettingsRefreshChecks(group == "refresh-baseline");
                else if(group.StartsWith("preview-")) {
                    int split=group.LastIndexOf('-'); string lang=group.Substring(8,split-8); int dpi=Int32.Parse(group.Substring(split+1));
                    PreviewForm(lang,dpi);
                }
                else if(group=="contexts") DpiContexts();
                else if(group=="drafts") { NumericDrafts(); Transactions(); }
                else if(group=="lifetime") WindowLifetime();
                else if(group=="preparation") { PreparationClose(); SynchronousAcknowledgement(); }
                else if(group=="integration") Integration();
                else if(group=="dpi") DpiRebuild();
                else if(group=="modeless") ModelessHostLifetime();
                else if(group=="native-modeless") NativeModelessLifetime();
                else if(group=="low-error") LowError();
                else if(group.StartsWith("low-")) {
                    int split=group.LastIndexOf('-'); string lang=group.Substring(4,split-4); int dpi=Int32.Parse(group.Substring(split+1));
                    DirectForm(lang,dpi,true);
                }
                else if(group.StartsWith("direct-")) {
                    int split=group.LastIndexOf('-'); string lang=group.Substring(7,split-7); int dpi=Int32.Parse(group.Substring(split+1));
                    DirectForm(lang,dpi);
                }
                else if(group.StartsWith("render-")) {
                    int split=group.LastIndexOf('-'); string lang=group.Substring(7,split-7); int dpi=Int32.Parse(group.Substring(split+1));
                    DirectForm(lang,dpi);
                } else throw new ArgumentException("Use a bounded --group, --probe or --rebuild-probe entry.");
            } catch(Exception ex) { failures++; string failure="FAIL "+group+": "+ex; results.Add(failure); File.AppendAllText(Path.Combine(output,"ui-progress.txt"),failure+Environment.NewLine); }
            results.Add("RESULT checks="+results.Count+" failures="+failures+(group.StartsWith("ui-details-")
                ? "; actual HWND DPI logged; live GetDC/BitBlt with ink and paper validation on private non-input desktop; fake callbacks, no product entry, real settings, audio or display apply."
                : group.StartsWith("refresh-")
                    ? "; actual HWND DPI, WM_GETFONT/LOGFONT/GetTextMetrics and live window DC after timer refresh on private non-input desktop; fake callbacks, no product entry or real settings."
                    : "; DrawToBitmap on private non-input desktop, fake callbacks, simulated DPI/work areas; no product entry, actual audio or display apply. Desktop handle ends with this isolated process."));
            File.WriteAllLines(Path.Combine(output,"ui-results.txt"),results.ToArray(),Encoding.UTF8);
            File.WriteAllLines(Path.Combine(output,"ui-layout.txt"),geometry.ToArray(),Encoding.UTF8);
            Console.WriteLine(results.Last()); return failures==0 ? 0 : 1;
        }
        private static void Check(bool condition,string message) { File.AppendAllText(Path.Combine(output,"ui-progress.txt"),(condition ? "PASS " : "FAIL ")+message+Environment.NewLine); results.Add((condition ? "PASS " : "FAIL ")+message); if(!condition) throw new InvalidOperationException(message); }
        private static void Verify(bool condition,string message) { File.AppendAllText(Path.Combine(output,"ui-progress.txt"),(condition ? "PASS " : "FAIL ")+message+Environment.NewLine); results.Add((condition ? "PASS " : "FAIL ")+message); if(!condition) failures++; }
        private sealed class Fixture : IDisposable
        {
            internal readonly SettingsDialog Form;
            internal PlaybackPanelState State;
            internal SettingsValues Saved;
            internal string SaveError, ListError;
            internal int Saves, ListSaves, Rechecks, Prepares, Cancels, Refreshes;
            internal Action OnCancel;
            internal readonly IntPtr HostContext;
            internal bool CallbackContextValid = true;
            internal Fixture(string language)
            {
                HostContext = SettingsDpiContext.CaptureCurrent();
                using (SettingsDpiContext.EnterUi()) {
                string chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
                var source = new AudioSource { ExePath = File.Exists(chrome) ? chrome : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"notepad.exe"), DisplayName = File.Exists(chrome) ? "Chrome" : "Notepad (UI fixture)", ProcessIds = new[] { 42 }, SessionCount = 1 };
                State = new PlaybackPanelState { PhysicalIdleMilliseconds = 180000, SettingsWarning = null, Audio = new AudioSnapshot { Known = true, Sources = new[] { source } }, AppliedOptions = new PlaybackOptions { WhitelistPaths = new[] { source.ExePath } },
                    Timing = new AutoOffTimingResult(0,new PlaybackProtectionState(AutoOffPauseReason.Playback,false,source.DisplayName)) };
                File.AppendAllText(Path.Combine(output,"ui-fixture-phases.txt"),"construct dialog"+Environment.NewLine);
                Form = new SettingsDialog(new UiText(language),new SettingsValues { TimeoutMinutes = 3, Language = language, Hotkey = HotkeyBinding.Default, Startup = true },
                    value => { using (SettingsDpiContext.EnterHost(HostContext)) { ObserveCallbackContext(); Saves++; Saved = value; return SaveError; } },
                    () => { using (SettingsDpiContext.EnterHost(HostContext)) { ObserveCallbackContext(); Prepares++; } },
                    () => { using (SettingsDpiContext.EnterHost(HostContext)) { ObserveCallbackContext(); Cancels++; if (OnCancel != null) OnCancel(); } }, () => { }, () => { }, () => { }, "0.1.9.7",
                    () => { using (SettingsDpiContext.EnterHost(HostContext)) { ObserveCallbackContext(); return State; } },
                    () => { using (SettingsDpiContext.EnterHost(HostContext)) { ObserveCallbackContext(); Refreshes++; } },
                    paths => { using (SettingsDpiContext.EnterHost(HostContext)) { ObserveCallbackContext(); ListSaves++; if (ListError == null) State.AppliedOptions.WhitelistPaths = paths; return ListError; } });
                File.AppendAllText(Path.Combine(output,"ui-fixture-phases.txt"),"dialog constructed"+Environment.NewLine);
                Find<HotkeyCaptureBox>(Form,"hotkey").KeyIsDown = key => false;
                Form.ConfigureWindow(() => { using (SettingsDpiContext.EnterHost(HostContext)) { ObserveCallbackContext(); Rechecks++; } });
                File.AppendAllText(Path.Combine(output,"ui-fixture-phases.txt"),"refresh setup"+Environment.NewLine);
                Form.RefreshSetup(new SetupPanelState { Ready = true });
                File.AppendAllText(Path.Combine(output,"ui-fixture-phases.txt"),"refresh playback"+Environment.NewLine);
                Form.RefreshPlayback();
                File.AppendAllText(Path.Combine(output,"ui-fixture-phases.txt"),"create handles"+Environment.NewLine);
                IntPtr unused = Form.Handle; Layout();
                File.AppendAllText(Path.Combine(output,"ui-fixture-phases.txt"),"fixture ready"+Environment.NewLine);
                }
            }
            private void ObserveCallbackContext() { CallbackContextValid &= AreDpiAwarenessContextsEqual(SettingsDpiContext.CaptureCurrent(),HostContext); }
            internal void Layout() { CreateTree(Form); Form.PerformLayout(); foreach (Control c in Form.Controls) c.PerformLayout(); }
            public void Dispose() { Form.Dispose(); }
        }
        private static void LanguageSwitch()
        {
            using(SettingsDpiContext.EnterUi()) foreach(string original in new[]{"zh-CN","en"}) using(var f=new Fixture(original)) {
                f.Form.StartPosition=FormStartPosition.Manual;f.Form.Location=Point.Empty;
                f.Form.Show();Pump(100);
                int dpi=(int)GetDpiForWindow(f.Form.Handle);
                f.Form.ClientSize=new Size((int)Math.Round(984*dpi/96F),(int)Math.Round(760*dpi/96F));Pump(80);
                var language=Find<ComboBox>(f.Form,"language");
                var timeout=Find<MintNumberBox>(f.Form,"timeout");timeout.Text="27";
                var limit=Find<MintNumberBox>(f.Form,"playback-limit-minutes");limit.Text="123";
                var key=Find<HotkeyCaptureBox>(f.Form,"hotkey");key.RecordPhysicalKey((int)Keys.N,2);key.RecordKey(Keys.N,true);
                string keyDraft=key.Text;
                Find<CheckBox>(f.Form,"startup").Checked=false;
                Find<CheckBox>(f.Form,"automatic-updates").Checked=false;
                var shared=(UiText)typeof(SettingsDialog).GetField("text",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f.Form);
                bool hostEnglish=shared.English;
                IntPtr window=f.Form.Handle;Rectangle bounds=f.Form.Bounds;
                int notifications=0;f.Form.LanguagePreviewChanged+=delegate { notifications++; };
                // The view's last read predates the immediately saved removal.
                var old=f.State;
                f.State=new PlaybackPanelState { Audio=old.Audio,Timing=old.Timing,
                    AppliedOptions=old.AppliedOptions.Copy(),SettingsWarning=old.SettingsWarning };
                Click(Find<Button>(f.Form,"remove-whitelist"));Pump(40);
                for(int i=0;i<6;i++) {
                    bool english=i%2==0;
                    language.SelectedIndex=english ? 1 : 0;Pump(40);
                    Check(f.Form.English==english && Find<Label>(f.Form,"basic-title").Text==(english ? "Basic settings" : "基础设置"),original+": selection immediately changes visible language "+i);
                    Check(Find<Button>(f.Form,"save").Text==(english ? "Save" : "保存") && Find<Button>(f.Form,"cancel").Text==(english ? "Cancel" : "取消"),original+": footer follows selected language "+i);
                    Check(Find<Label>(f.Form,"timeout-unit").Text==(english ? "min" : "分钟") && Find<Label>(f.Form,"playback-limit-unit").Text==(english ? "min" : "分钟"),original+": both minute labels switch "+i);
                    Check(Find<Label>(f.Form,"whitelist-count").Text==(english ? "Allowlist (0)" : "白名单 (0)") && Find<Label>(f.Form,"whitelist-instant").Text==(english ? "Saved" : "已保存"),original+": stale cached options never resurrect a removed entry "+i);
                    Check(Find<Button>(f.Form,"add-whitelist-0").Text==(english ? "Add" : "加入") && Find<Label>(f.Form,"playing-caption-0").Text==(english ? "Playing" : "正在播放"),original+": dynamic source captions switch "+i);
                    Check(Find<Label>(f.Form,"playing-source-0").Font.Height>0 && key.Font.Unit==GraphicsUnit.Pixel && key.Font.FontFamily.Name==(english ? "Segoe UI" : "Microsoft YaHei UI"),original+": switched native and dynamic fonts remain live "+i);
                    Check(timeout.Text=="27" && limit.Text=="123" && key.Text==keyDraft && !Find<CheckBox>(f.Form,"startup").Checked && !Find<CheckBox>(f.Form,"automatic-updates").Checked,original+": all unrelated drafts survive "+i);
                }
                Check(shared.English==hostEnglish && f.Form.Handle==window && f.Form.Bounds==bounds && f.Saves==0 && f.ListSaves==1,original+": preview keeps host, native window, manual bounds and persistence unchanged");
                language.SelectedIndex=1;Pump(50);
                f.Form.RefreshSetup(new SetupPanelState { Ready=false,Visible=true,CanPrepare=true,Detail="准备说明 / Setup explanation" });
                f.Form.RefreshUpdate(false,"更新源未配置 / No update source",false,true);Pump(40);
                Check(Find<Label>(f.Form,"setup-detail").Text=="Setup explanation" && Find<Label>(f.Form,"update-detail").Text=="No update source",original+": localized cached setup and update details");
                language.SelectedIndex=0;Pump(40);
                Check(Find<Label>(f.Form,"setup-detail").Text=="准备说明" && Find<Label>(f.Form,"update-detail").Text=="更新源未配置",original+": cached details can return to Chinese");
                timeout.Text="0";Click(Find<Button>(f.Form,"save"));
                Check(f.Saves==0 && Find<Label>(f.Form,"error").Text.Contains("请输入"),original+": invalid draft is rejected without saving");
                language.SelectedIndex=1;Pump(50);
                Check(Find<Label>(f.Form,"error").Text=="Enter 1–1440 minutes.",original+": existing validation text changes language");
                timeout.Text="27";
                ApplyDpi(f,192);Pump(80);language.SelectedIndex=0;Pump(40);language.SelectedIndex=1;Pump(50);
                Check(Find<Label>(f.Form,"playing-source-0").Font.Height>0 && timeout.Text=="27" && limit.Text=="123",original+": switching after a DPI change keeps fonts and drafts");
                f.Form.RefreshSetup(new SetupPanelState { Preparing=true,Visible=true });
                language.SelectedIndex=0;Pump(30);
                Check(language.SelectedIndex==1 && !Find<Button>(f.Form,"save").Enabled && f.Cancels==0,original+": preparation gate rejects language changes without cancelling display work");
                f.Form.RefreshSetup(new SetupPanelState {Ready=true});Pump(30);
                Check(notifications>0 && f.Saves==0,original+": notifications request presentation only");
                Click(Find<Button>(f.Form,"cancel"));
                Check(f.Form.IsDisposed && f.Saves==0,original+": Cancel discards language preview and other form drafts");
            }
            using(var f=new Fixture("zh-CN")) {
                f.SaveError="快捷键已被占用 / That shortcut is in use.";
                Find<ComboBox>(f.Form,"language").SelectedIndex=1;Click(Find<Button>(f.Form,"save"));
                Check(!f.Form.IsDisposed && Find<Label>(f.Form,"error").Text=="That shortcut is in use.","save failure uses preview language and keeps the window open");
                Find<ComboBox>(f.Form,"language").SelectedIndex=0;
                Check(Find<Label>(f.Form,"error").Text=="快捷键已被占用","cached save failure switches back to Chinese");
                Find<ComboBox>(f.Form,"language").SelectedIndex=1;
                Check(Find<Label>(f.Form,"error").Text=="That shortcut is in use.","cached save failure retains both translations");
                f.ListError="名单未保存 / List was not saved\r\n诊断原文 / Raw diagnostic";
                Click(Find<Button>(f.Form,"remove-whitelist"));
                Check(Find<Label>(f.Form,"whitelist-save-status").Text.Contains("Raw diagnostic"),"list-save failure preserves both translated diagnostic lines");
                Find<ComboBox>(f.Form,"language").SelectedIndex=0;
                Check(Find<Label>(f.Form,"whitelist-save-status").Text.Contains("名单未保存") && Find<Label>(f.Form,"whitelist-save-status").Text.Contains("诊断原文"),"cached list-save failure can return to Chinese without dropping a line");
                Click(Find<Button>(f.Form,"cancel"));
                Check(f.Saves==1,"switching after a failed save never resubmits it");
            }
            using(var f=new Fixture("zh-CN")) {
                f.Form.LanguagePreviewChanged+=delegate { throw new InvalidOperationException("safe presentation callback failure"); };
                Find<ComboBox>(f.Form,"language").SelectedIndex=1;
                Check(f.Form.English && !f.Form.IsDisposed && Find<Label>(f.Form,"error").Text.Contains("safe presentation callback failure"),"presentation callback failure is contained within the language preview");
            }
            foreach(string target in new[]{"en","zh-CN"}) using(var f=new Fixture(target=="en" ? "zh-CN" : "en")) {
                Find<ComboBox>(f.Form,"language").SelectedIndex=target=="en" ? 1 : 0;
                Click(Find<Button>(f.Form,"save"));
                Check(f.Saves==1 && f.Saved.Language==target && f.Form.IsDisposed,"Save sends the chosen language through the existing settings transaction: "+target);
                using(var reopened=new Fixture(f.Saved.Language)) using(var menu=new TrayMenu(new UiText(f.Saved.Language),()=>{},()=>{},()=>{},()=>{},1F)) {
                    Check(reopened.Form.English==(target=="en") && menu.Settings.Text.Contains(target=="en" ? "Settings" : "设置"),"reopened settings and native tray consume the saved language: "+target);
                }
            }
            // Full-width English and Chinese evidence uses the same live window.
            using(var f=new Fixture("zh-CN")) using(SettingsDpiContext.EnterUi()) {
                f.Form.StartPosition=FormStartPosition.Manual;f.Form.Location=Point.Empty;ApplyDpi(f,144);
                f.Form.ClientSize=new Size(1476,1080);f.Form.Show();Pump(100);
                foreach(bool english in new[]{true,false}) {
                    Find<ComboBox>(f.Form,"language").SelectedIndex=english ? 1 : 0;Pump(100);
                    var viewport=Find<Panel>(f.Form,"viewport");
                    Check(!viewport.HorizontalScroll.Visible && !f.Form.HorizontalScroll.Visible,"language capture has no horizontal overflow: "+english);
                    var header=Find<Control>(f.Form,"header");
                    Check(header.ClientRectangle.Contains(Find<Control>(f.Form,"display-state-line").Bounds),"language header status stays inside header: "+english);
                    using(var image=new Bitmap(f.Form.ClientSize.Width,f.Form.ClientSize.Height)) {
                        using(var g=Graphics.FromImage(image)) { IntPtr dc=g.GetHdc();try { Check(PrintWindow(f.Form.Handle,dc,1),"native language-switch capture "+english); } finally {g.ReleaseHdc(dc);} }
                        image.Save(Path.Combine(output,english ? "language-en.png" : "language-zh-CN.png"),ImageFormat.Png);
                    }
                }
            }
        }
        [DllImport("user32.dll",EntryPoint="SendMessageW")] private static extern IntPtr ResizeSend(IntPtr hwnd,int msg,IntPtr w,IntPtr l);
        private static Bitmap BackgroundCanvas(Control card)
        { return (Bitmap)typeof(MintCardLayoutPanel).GetField("backgroundCanvas",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(card); }
        private static void PaintCard(Control card,Bitmap bitmap,Rectangle clip,bool uncached=false)
        {
            using(var g=Graphics.FromImage(bitmap)) {
                g.SetClip(clip);
                typeof(MintCardLayoutPanel).GetMethod(uncached ? "PaintBackgroundScene" : "OnPaintBackground",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(card,new object[]{new PaintEventArgs(g,clip)});
            }
        }
        private static bool BitmapDisposed(Bitmap image)
        { try { int width=image.Width;return false; } catch(ArgumentException){return true;} }
        private static Bitmap CaptureReplay(Control control)
        {
            var bitmap=new Bitmap(control.ClientSize.Width,control.ClientSize.Height);
            IntPtr dc=GetDC(control.Handle);
            if(dc==IntPtr.Zero){bitmap.Dispose();throw new InvalidOperationException("No live replay DC");}
            try {
                using(var graphics=Graphics.FromImage(bitmap)) {
                    IntPtr target=graphics.GetHdc();
                    try {if(!BitBlt(target,0,0,bitmap.Width,bitmap.Height,dc,0,0,0x00CC0020))throw new InvalidOperationException("Replay BitBlt failed");}
                    finally{graphics.ReleaseHdc(target);}
                }
                return bitmap;
            }catch{bitmap.Dispose();throw;}finally{ReleaseDC(control.Handle,dc);}
        }
        private static void BackgroundReplayChecks()
        {
            using(SettingsDpiContext.EnterUi())using(var form=new Form{AutoScaleMode=AutoScaleMode.None,ClientSize=new Size(700,480),StartPosition=FormStartPosition.Manual,Location=new Point(20,20)})
            using(var card=new MintCardLayoutPanel(1){Dock=DockStyle.Fill,Padding=new Padding(0),Margin=new Padding(0),AutoSize=false}) {
                card.ArtResource="ScreenOffTray.Ui.Resources.theme.card-left-scene.png";
                var layer=new Panel{Dock=DockStyle.Fill,Margin=new Padding(0),BackColor=Color.Transparent};
                var nested=new Panel{Location=new Point(150,270),Size=new Size(320,140),BackColor=Color.Transparent};
                var leaf=new Label{Location=new Point(38,42),Size=new Size(130,65),Text="",BackColor=Color.Transparent,AutoSize=false};
                nested.Controls.Add(leaf);layer.Controls.Add(nested);card.Controls.Add(layer,0,0);form.Controls.Add(card);
                form.Show();form.Invalidate(true);form.Update();Pump(100);
                foreach(bool bypass in new[]{false,true}) {
                    typeof(MintCardLayoutPanel).GetField("backgroundCanvasUnavailable",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(card,bypass);
                    card.Invalidate(true);form.Update();Pump(60);
                    using(var expected=new Bitmap(card.Width,card.Height)) {
                        PaintCard(card,expected,card.ClientRectangle,true);
                        foreach(var item in new Control[]{nested,leaf})using(var actual=CaptureReplay(item)) {
                            Point offset=card.PointToClient(item.PointToScreen(Point.Empty));int wrong=0,maximum=0;
                            for(int y=0;y<actual.Height;y++)for(int x=0;x<actual.Width;x++) {
                                var a=actual.GetPixel(x,y);var b=expected.GetPixel(x+offset.X,y+offset.Y);
                                int delta=Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B)));maximum=Math.Max(maximum,delta);if(delta>1)wrong++;
                            }
                            string name=(bypass ? "direct" : "cache")+(item==leaf ? "-leaf" : "-nested");actual.Save(Path.Combine(output,name+".png"),ImageFormat.Png);
                            geometry.Add(name+" offset="+offset+" wrong="+wrong+" max_delta="+maximum+" HWND_DPI="+GetDpiForWindow(item.Handle));
                            Verify(wrong==0,name+": actual transparent child matches the corresponding parent scene patch");
                        }
                    }
                }
                using(var capture=CaptureReplay(form))capture.Save(Path.Combine(output,"isolated-background.png"),ImageFormat.Png);
            }
        }
        private static IEnumerable<Control> ReplayTree(Control root)
        { yield return root;foreach(Control c in root.Controls)foreach(var child in ReplayTree(c))yield return child; }
        private static void CheckReplayStable(SettingsDialog form,string phase)
        {
            foreach(string cardName in new[]{"basic-settings","playback-settings"}) {
                var card=ReplayTree(form).OfType<MintCardLayoutPanel>().FirstOrDefault(c=>c.Name==cardName);
                if(card==null)continue;
                using(var expected=new Bitmap(card.Width,card.Height)) {
                    PaintCard(card,expected,card.ClientRectangle,true);
                    foreach(var container in ReplayTree(card).Where(c=>c!=card && c.BackColor.A==0 &&
                        (c.GetType()==typeof(Panel) || c.GetType()==typeof(TableLayoutPanel) || c.GetType()==typeof(FlowLayoutPanel)))) {
                        if(!container.Visible || container.Width<5 || container.Height<5)continue;
                        var viewport=Find<Control>(form,"viewport");
                        if(!viewport.RectangleToScreen(viewport.ClientRectangle).Contains(container.RectangleToScreen(container.ClientRectangle)))continue;
                        Point offset=card.PointToClient(container.PointToScreen(Point.Empty));
                        if(!card.ClientRectangle.Contains(new Rectangle(offset,container.ClientSize)))continue;
                        using(var actual=CaptureReplay(container)) {
                            var children=container.Controls.Cast<Control>().Where(c=>c.Visible).Select(c=>Rectangle.Inflate(c.Bounds,2,2)).ToArray();int samples=0,wrong=0;
                            for(int y=2;y<actual.Height-2;y++)for(int x=2;x<actual.Width-2;x++) {
                                if(children.Any(r=>r.Contains(x,y)))continue;
                                // The lower left card intentionally contains the
                                // book/cup raster art; compare its upper paper area.
                                if(cardName=="basic-settings" && y+offset.Y>=card.Height*.4F)continue;
                                Color a=actual.GetPixel(x,y),b=expected.GetPixel(x+offset.X,y+offset.Y);
                                // Compare smooth paper margins, not native text,
                                // custom child surfaces or raster-art edge filtering.
                                if(b.R<230 || b.G<230 || b.B<230 || Math.Max(b.R,Math.Max(b.G,b.B))-Math.Min(b.R,Math.Min(b.G,b.B))>8)continue;
                                bool smooth=true;
                                for(int dy=-2;dy<=2 && smooth;dy++)for(int dx=-2;dx<=2;dx++) {
                                    var neighbor=expected.GetPixel(x+offset.X+dx,y+offset.Y+dy);
                                    if(Math.Max(Math.Abs(b.R-neighbor.R),Math.Max(Math.Abs(b.G-neighbor.G),Math.Abs(b.B-neighbor.B)))>3){smooth=false;break;}
                                }
                                if(!smooth)continue;
                                samples++;if(Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B)))>3) {
                                    if(wrong==0)geometry.Add(phase+" "+container.Name+" first_wrong="+new Point(x,y)+" actual="+a+" expected="+b+" offset="+offset+" children="+String.Join(";",children.Select(r=>r.ToString())));
                                    wrong++;
                                }
                            }
                            if(samples>10) {
                                if(wrong>0) {
                                    actual.Save(Path.Combine(output,phase+"-"+container.Name+"-actual.png"),ImageFormat.Png);
                                    using(var patch=expected.Clone(new Rectangle(offset,container.ClientSize),expected.PixelFormat))patch.Save(Path.Combine(output,phase+"-"+container.Name+"-reference.png"),ImageFormat.Png);
                                }
                                geometry.Add(phase+" "+container.Name+" parent_patch_wrong="+wrong+" samples="+samples);
                                Verify(wrong==0,phase+" "+container.Name+": transparent margins align with parent scene pixels");
                            }
                        }
                    }
                }
            }
            using(var before=CaptureReplay(form)) {
                before.Save(Path.Combine(output,phase+"-before.png"),ImageFormat.Png);
                form.Invalidate(true);form.Update();Pump(60);
                using(var after=CaptureReplay(form)) {
                    after.Save(Path.Combine(output,phase+"-after.png"),ImageFormat.Png);
                    int wrong=0;
                    for(int y=0;y<before.Height;y++)for(int x=0;x<before.Width;x++) {
                        Color a=before.GetPixel(x,y),b=after.GetPixel(x,y);
                        if(a.R>180 && a.G>180 && a.B>180 && b.R>180 && b.G>180 && b.B>180 &&
                            Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B)))>3)wrong++;
                    }
                    geometry.Add(phase+" repaint_background_changed="+wrong);
                    Verify(wrong==0,phase+": settled live view has no stale background for a full repaint to repair");
                }
            }
        }
        private static void ResizePaintChecks()
        {
            using(var card=new MintCardLayoutPanel(1)) {
                card.Size=new Size(320,180);card.ArtResource="ScreenOffTray.Ui.Resources.theme.card-left-scene.png";
                using(var clipped=new Bitmap(320,180)) {
                    using(var g=Graphics.FromImage(clipped))g.Clear(Color.Magenta);
                    var region=new Rectangle(10,10,24,18);PaintCard(card,clipped,region);
                    int outside=0,inside=0;
                    for(int y=0;y<clipped.Height;y++)for(int x=0;x<clipped.Width;x++)if(clipped.GetPixel(x,y).ToArgb()!=Color.Magenta.ToArgb()){if(region.Contains(x,y))inside++;else outside++;}
                    Check(outside==0 && inside>0,"partial background replay changes pixels only within caller clip");
                    using(var direct=new Bitmap(320,180)) {
                        using(var graphics=Graphics.FromImage(direct))graphics.Clear(Color.Magenta);
                        PaintCard(card,direct,region,true);outside=0;
                        for(int y=0;y<direct.Height;y++)for(int x=0;x<direct.Width;x++)
                            if(!region.Contains(x,y) && direct.GetPixel(x,y).ToArgb()!=Color.Magenta.ToArgb())outside++;
                        Check(outside==0,"uncached scene also preserves the original caller clip");
                    }
                    if(!SystemInformation.HighContrast) {
                        var cached=BackgroundCanvas(card);Check(cached!=null,"visible small rounded card creates one static scene");
                        PaintCard(card,clipped,region);Check(Object.ReferenceEquals(cached,BackgroundCanvas(card)),"unchanged scene reuses the same bitmap");
                        typeof(MintCardLayoutPanel).GetField("backgroundCanvasUnavailable",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(card,true);
                        PaintCard(card,clipped,region);
                        Check(BackgroundCanvas(card)==null && BitmapDisposed(cached),"unavailable optional cache falls back and releases old ownership");
                        PaintCard(card,clipped,region);Check(BackgroundCanvas(card)==null,"unavailable cache does not retry on every paint");
                        card.Size=new Size(321,180);card.Size=new Size(320,180);PaintCard(card,clipped,region);cached=BackgroundCanvas(card);
                        Check(cached!=null,"a geometry change permits cache allocation again");
                        using(var raw=new Bitmap(320,180))using(var reused=new Bitmap(320,180)) {
                            PaintCard(card,raw,card.ClientRectangle,true);PaintCard(card,reused,card.ClientRectangle);
                            int maximum=0;
                            for(int y=0;y<180;y++)for(int x=0;x<320;x++) {
                                var a=raw.GetPixel(x,y);var b=reused.GetPixel(x,y);
                                maximum=Math.Max(maximum,Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B))));
                            }
                            Check(maximum<=1,"cached and direct scene preserve pixels within one channel rounding level");
                        }
                        card.Size=new Size(321,180);Check(BackgroundCanvas(card)==null && BitmapDisposed(cached),"size change immediately releases the old scene");
                        PaintCard(card,clipped,region);cached=BackgroundCanvas(card);
                        card.Visible=false;Check(BackgroundCanvas(card)==null && BitmapDisposed(cached),"hidden control releases its bitmap");card.Visible=true;
                        card.Size=new Size(1500,1000);PaintCard(card,clipped,region);Check(BackgroundCanvas(card)==null,"scene above 4 MiB renders without allocating the optional cache");
                        card.Size=new Size(320,180);PaintCard(card,clipped,region);cached=BackgroundCanvas(card);
                        card.Dispose();Check(BackgroundCanvas(card)==null && BitmapDisposed(cached),"Dispose releases owned cached bitmap");
                    }
                }
            }
            using(SettingsDpiContext.EnterUi())using(var f=new Fixture("zh-CN")) {
                f.Form.StartPosition=FormStartPosition.Manual;f.Form.Location=new Point(60,60);f.Form.Show();Pump(100);
                f.Form.ClientSize=new Size(1680,1180);Pump(120);
                var root=Find<Control>(f.Form,"settings-window");var basic=Find<Control>(f.Form,"basic-settings");var header=Find<Control>(f.Form,"header");var footer=Find<Control>(f.Form,"footer");
                var columns=Find<TableLayoutPanel>(f.Form,"configuration");
                Rectangle basicBefore=basic.Bounds,headerBefore=header.Bounds,footerBefore=footer.Bounds;
                var number=Find<MintNumberBox>(f.Form,"timeout");number.Text="27";
                ResizeSend(f.Form.Handle,0x231,IntPtr.Zero,IntPtr.Zero);
                for(int i=1;i<=8;i++){f.Form.ClientSize=new Size(1680-i*70,1180);Pump(5);}
                Check(basic.Bounds==basicBefore && header.Bounds==headerBefore && footer.Bounds==footerBefore,"interactive size keeps expensive child geometry stable until mouse release");
                Check(columns.ColumnCount==2 && (bool)typeof(SettingsDialog).GetField("interactiveRootSuspended",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(f.Form),"width breakpoint does not rebuild the view during interactive resize");
                ResizeSend(f.Form.Handle,0x232,IntPtr.Zero,IntPtr.Zero);Pump(160);
                Check(columns.ColumnCount==1 && !(bool)typeof(SettingsDialog).GetField("interactiveRootSuspended",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(f.Form),"mouse release commits final single-column geometry and releases layout suspension");
                Check(number.Text=="27" && footer.Bottom<=f.Form.ClientSize.Height && !Find<Panel>(f.Form,"viewport").HorizontalScroll.Visible,"resize preserves draft and reachable footer without horizontal overflow");
                Check(Convert.ToInt32(typeof(SettingsDialog).GetField("pendingLayout",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(f.Form))==0 && !(bool)typeof(SettingsDialog).GetField("layoutQueued",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(f.Form),"resize settles with no repeated posted layout");
                int bodyLayouts=0;LayoutEventHandler count=(s,e)=>bodyLayouts++;var body=Find<Control>(f.Form,"content");body.Layout+=count;
                ResizeSend(f.Form.Handle,0x231,IntPtr.Zero,IntPtr.Zero);f.Form.Left+=4;ResizeSend(f.Form.Handle,0x232,IntPtr.Zero,IntPtr.Zero);Pump(40);body.Layout-=count;
                Check(bodyLayouts==0,"same-monitor pure move never remeasures body");
                ResizeSend(f.Form.Handle,0x231,IntPtr.Zero,IntPtr.Zero);
                IntPtr suggested=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ClientRect)));
                try {
                    var bounds=f.Form.Bounds;Marshal.StructureToPtr(new ClientRect{Left=bounds.Left,Top=bounds.Top,Right=bounds.Right,Bottom=bounds.Bottom},suggested,false);
                    ResizeSend(f.Form.Handle,0x2e0,new IntPtr(192|(192<<16)),suggested);Pump(60);
                    Check((int)typeof(SettingsDialog).GetField("appliedDpi",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(f.Form)==192,"DPI message applies immediately inside an interactive resize");
                } finally {Marshal.FreeHGlobal(suggested);}
                ResizeSend(f.Form.Handle,0x232,IntPtr.Zero,IntPtr.Zero);Pump(80);
                Check(!(bool)typeof(SettingsDialog).GetField("interactiveRootSuspended",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(f.Form),"DPI transaction leaves no suspended root after resize");
                ResizeSend(f.Form.Handle,0x231,IntPtr.Zero,IntPtr.Zero);
                typeof(Control).GetMethod("RecreateHandle",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(f.Form,null);Pump(80);
                Check(!(bool)typeof(SettingsDialog).GetField("interactiveRootSuspended",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(f.Form),"handle recreation releases an active suspension");
                ResizeSend(f.Form.Handle,0x231,IntPtr.Zero,IntPtr.Zero);f.Form.Dispose();Pump(10);
                Check(!(bool)typeof(SettingsDialog).GetField("interactiveRootSuspended",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(f.Form) && BackgroundCanvas(basic)==null,"close during resize releases layout and scene ownership");
            }
        }
        private static void NumericDrafts()
        {
            using (var f = new Fixture("zh-CN")) {
                var number = Find<MintNumberBox>(f.Form,"timeout");
                number.Value = 1; Check(!Find<Button>(f.Form,"timeout-minus").Enabled,"lower bound disables minus");
                number.Value = 1440; Check(!Find<Button>(f.Form,"timeout-plus").Enabled,"upper bound disables plus");
                number.Value = 7; Click(Find<Button>(f.Form,"timeout-plus")); Check(number.Value == 8,"plus increments draft");
                var arrow = new KeyEventArgs(Keys.Down);
                typeof(MintNumberBox).GetMethod("OnKeyDown",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(number,new object[] { arrow });
                Check(number.Value == 7 && arrow.SuppressKeyPress,"keyboard arrows step within bounds without typing a character");
                number.Text = "oops"; Click(Find<Button>(f.Form,"save")); Check(f.Saves == 0 && number.Text == "oops","invalid draft remains visible and blocks save");
                var enabled = Find<CheckBox>(f.Form,"playback-limit-enabled"); enabled.Checked = false;
                Check(!Find<Control>(f.Form,"playback-limit-minutes-frame").Enabled && !Find<Button>(f.Form,"playback-limit-minutes-plus").Enabled,"unchecked cap disables entire stepper");
                Check(Find<Label>(f.Form,"playback-limit-hint").Text.Contains("直到播放停止"),"unlimited protection copy is explicit");
                enabled.Checked = true; var cap = Find<MintNumberBox>(f.Form,"playback-limit-minutes"); cap.Text = "0"; number.Value = 12;
                Click(Find<Button>(f.Form,"save")); Check(f.Saves == 0,"invalid protection limit blocks transaction");
                cap.Value = 77; f.Form.RefreshPlayback(); Check(cap.Value == 77,"live snapshot does not overwrite cap draft");
            }
        }
        private static void Transactions()
        {
            using (var f = new Fixture("en")) {
                var list = Find<ComboBox>(f.Form,"whitelist-picker"); f.ListError = "fixture denied";
                Click(Find<Button>(f.Form,"remove-whitelist")); Check(list.Items.Count == 1 && f.State.AppliedOptions.WhitelistPaths.Length == 1,"failed immediate removal retains effective list");
                Check(Find<Label>(f.Form,"whitelist-save-status").Text.Contains("fixture denied"),"list error remains visible");
                f.ListError = null; Click(Find<Button>(f.Form,"remove-whitelist")); Check(f.ListSaves == 2 && f.Saves == 0 && f.State.AppliedOptions.WhitelistPaths.Length == 0,"list mutation is independent and immediate");
                Click(Find<Button>(f.Form,"add-whitelist-0")); Check(f.State.AppliedOptions.WhitelistPaths.Length == 1,"source can be added by its exact path");
                var hotkey = Find<HotkeyCaptureBox>(f.Form,"hotkey"); hotkey.RecordPhysicalKey((int)Keys.F9,3); hotkey.RecordKey(Keys.F9,true);
                Find<MintNumberBox>(f.Form,"timeout").Value = 19; Find<CheckBox>(f.Form,"startup").Checked = false;
                f.SaveError = "fixture shortcut conflict"; Click(Find<Button>(f.Form,"save"));
                Check(f.Saves == 1 && f.Saved.TimeoutMinutes == 19 && f.Saved.Hotkey.Key == (int)Keys.F9 && !f.Saved.Startup && !f.Form.IsDisposed,"failed draft transaction preserves collected settings and open form");
                Click(Find<Button>(f.Form,"defaults")); Check(hotkey.Binding.SameAs(HotkeyBinding.Default) && Find<MintNumberBox>(f.Form,"timeout").Value == 19,"reset restores only shortcut");
                Link(Find<LinkLabel>(f.Form,"recheck")); Check(f.Rechecks == 1,"display check uses supplied read-only callback");
                Close(f.Form); Check(f.Saves == 1 && f.State.AppliedOptions.WhitelistPaths.Length == 1,"ordinary close discards drafts and retains immediate whitelist");
            }
        }
        private static void WindowLifetime()
        {
            foreach (string ending in new[] { "x", "cancel", "escape", "save" }) using (var f = new Fixture("zh-CN")) using (var other = new Form()) {
                IntPtr handle = other.Handle;
                Find<MintNumberBox>(f.Form,"timeout").Value = 23;
                typeof(Form).GetMethod("OnDeactivate",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(f.Form,new object[] { EventArgs.Empty });
                typeof(ComboBox).GetMethod("OnDropDownClosed",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(Find<ComboBox>(f.Form,"whitelist-picker"),new object[] { EventArgs.Empty });
                Application.DoEvents();
                Check(!f.Form.IsDisposed && f.Saves == 0,ending + ": deactivate/dropdown close leave form and draft intact");
                if (ending == "x") f.Form.Close();
                else if (ending == "cancel") Click(Find<Button>(f.Form,"cancel"));
                else if (ending == "save") Click(Find<Button>(f.Form,"save"));
                else Escape(f.Form);
                Check(f.Form.IsDisposed && !other.IsDisposed, ending + ": only settings form ends");
                Check(f.Form.DialogResult == (ending == "save" ? DialogResult.OK : DialogResult.Cancel) && f.Saves == (ending == "save" ? 1 : 0),ending + ": correct ordinary transaction result");
            }
            using (var f = new Fixture("en")) {
                f.Form.Location = new Point(54,63); f.Form.ClientSize = new Size(603,477);
                Rectangle bounds = f.Form.Bounds;
                f.State.Audio.Sources = new AudioSource[0];
                for (int i=0;i<3;i++) { f.Form.RefreshPlayback(); f.Form.RefreshSetup(new SetupPanelState { Ready = true }); }
                Check(f.Form.Bounds == bounds,"refresh preserves manual window size and position");
                f.Form.WindowState = FormWindowState.Minimized;
                for (int i=0;i<3;i++) f.Form.RefreshPlayback();
                Check(f.Form.WindowState == FormWindowState.Minimized,"one-second refresh never restores minimized window");
                var binding = Find<HotkeyCaptureBox>(f.Form,"hotkey").Binding;
                f.Form.ObservePhysicalKey((int)Keys.F8,3);
                Check(Find<HotkeyCaptureBox>(f.Form,"hotkey").Binding.SameAs(binding),"hidden or minimized editor never captures global input");
            }
        }
        private static void Escape(SettingsDialog form)
        {
            Message message = Message.Create(form.Handle,0x100,new IntPtr((int)Keys.Escape),IntPtr.Zero);
            typeof(SettingsDialog).GetMethod("ProcessCmdKey",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,new object[] { message,Keys.Escape });
        }
        private static void PreparationClose()
        {
            foreach (string ending in new[] { "x", "cancel", "escape" }) using (var f = new Fixture("zh-CN")) {
                f.Form.RefreshSetup(new SetupPanelState { Visible = true, CanPrepare = true, Detail = "仅假准备" });
                Click(Find<Button>(f.Form,"prepare"));
                Check(f.Prepares == 1 && !Find<Button>(f.Form,"save").Enabled && Find<Button>(f.Form,"cancel").Enabled,ending + ": preparation gates draft save and keeps cancel available");
                if (ending == "x") f.Form.Close(); else if (ending == "cancel") Click(Find<Button>(f.Form,"cancel")); else Escape(f.Form);
                Check(f.Cancels == 1 && !f.Form.IsDisposed,ending + ": close waits for acknowledged preparation cancellation");
                f.Form.Close(); Check(f.Cancels == 1 && !f.Form.IsDisposed,ending + ": repeat close cannot force restoration or duplicate cancel");
                f.Form.RefreshSetup(new SetupPanelState { Preparing = true, Visible = true, Detail = "fake restoring" });
                Check(!f.Form.IsDisposed,ending + ": restoration still owns completion");
                f.Form.RefreshSetup(new SetupPanelState { Ready = true });
                Check(f.Form.IsDisposed && f.Form.DialogResult == DialogResult.Cancel && f.Cancels == 1 && f.Saves == 0,ending + ": acknowledged safe result closes as cancellation exactly once");
            }
        }
        private static void SynchronousAcknowledgement()
        {
            using (var f = new Fixture("en")) {
                f.Form.RefreshSetup(new SetupPanelState { Visible = true, Preparing = true });
                f.OnCancel = () => f.Form.RefreshSetup(new SetupPanelState { Ready = true });
                f.Form.Close(); Application.DoEvents();
                Check(f.Cancels == 1 && f.Form.IsDisposed && f.Form.DialogResult == DialogResult.Cancel,"synchronous cancellation acknowledgement safely completes X close");
            }
        }
        private static void DpiContexts()
        {
            IntPtr host = SettingsDpiContext.CaptureCurrent(); int processBefore;
            Check(GetProcessDpiAwareness(IntPtr.Zero,out processBefore) == 0,"read isolated harness process awareness");
            using (SettingsDpiContext.EnterUi()) {
                Check(AreDpiAwarenessContextsEqual(SettingsDpiContext.CaptureCurrent(),new IntPtr(-4)),"UI scope enters PMv2");
                using (SettingsDpiContext.EnterHost(host)) {
                    Check(AreDpiAwarenessContextsEqual(SettingsDpiContext.CaptureCurrent(),host),"host callback scope restores captured host context");
                    using (SettingsDpiContext.EnterUi()) Check(AreDpiAwarenessContextsEqual(SettingsDpiContext.CaptureCurrent(),new IntPtr(-4)),"nested UI scope enters PMv2");
                    Check(AreDpiAwarenessContextsEqual(SettingsDpiContext.CaptureCurrent(),host),"nested UI exit restores immediate host caller");
                }
                Check(AreDpiAwarenessContextsEqual(SettingsDpiContext.CaptureCurrent(),new IntPtr(-4)),"host scope exit restores immediate UI caller");
                using (var f = new Fixture("zh-CN")) {
                    IntPtr reference = CreateWindowEx(0,"STATIC","Settings DPI reference",unchecked((int)0x80000000),0,0,1,1,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
                    Check(reference != IntPtr.Zero,"created private never-shown PMv2 reference HWND");
                    try {
                        uint expected = GetDpiForWindow(reference), measured = GetDpiForWindow(f.Form.Handle);
                        Check(GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(f.Form.Handle)) == 2,"new settings HWND has native per-monitor awareness 2");
                        Check(AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(f.Form.Handle),new IntPtr(-4)),"settings HWND is specifically PMv2");
                        Check(MonitorFromWindow(reference,2) == MonitorFromWindow(f.Form.Handle,2) && expected >= 96 && measured == expected,"settings DPI matches independent hidden reference on the same current desktop monitor");
                        Check(!IsWindowVisible(reference) && !IsWindowVisible(f.Form.Handle),"both DPI HWNDs remain hidden");
                        File.WriteAllText(Path.Combine(output,"原生DPI元数据.txt"),"process_awareness="+processBefore+"\r\nhost_awareness="+GetAwarenessFromDpiAwarenessContext(host)+"\r\nsettings_window_awareness=2\r\nsettings_window_dpi="+measured+"\r\nreference_window_dpi="+expected+"\r\nauto_scale_dimensions="+f.Form.AutoScaleDimensions+"\r\ncurrent_auto_scale_dimensions="+f.Form.CurrentAutoScaleDimensions+"\r\nclient_size="+f.Form.ClientSize+"\r\nfont_point_size="+f.Form.Font.Size+"\r\nfont_height="+f.Form.Font.Height+"\r\nstepper_size="+Find<Control>(f.Form,"timeout-frame").Size+"\r\nroot_padding="+Find<Control>(f.Form,"content").Padding+"\r\nlayout_scale="+typeof(SettingsDialog).GetField("layoutScale",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f.Form)+"\r\nNo display enumeration, APPLY, settings persistence or production adapters.\r\n",Encoding.UTF8);
                    } finally { DestroyWindow(reference); }
                }
            }
            Check(AreDpiAwarenessContextsEqual(SettingsDpiContext.CaptureCurrent(),host),"UI scope exit restores original host context");
            try { using (SettingsDpiContext.EnterUi()) throw new InvalidOperationException("intentional scope test"); } catch (InvalidOperationException) { }
            Check(AreDpiAwarenessContextsEqual(SettingsDpiContext.CaptureCurrent(),host),"exception restores original host context");
            int processAfter; Check(GetProcessDpiAwareness(IntPtr.Zero,out processAfter) == 0 && processAfter == processBefore,"process default DPI awareness unchanged");
        }
        [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr first,IntPtr second);
        [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
        [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr CreateWindowEx(int extendedStyle,string className,string caption,int style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
        [DllImport("shcore.dll")] private static extern int GetProcessDpiAwareness(IntPtr process,out int awareness);
        private static void ModelessHostLifetime()
        {
            // Actual WinForms loop, hidden Handle-created settings, no MainForm. This does
            // not prove visible Show/activation/titlebar behavior or production coordinator wiring.
            foreach (string ending in new[] { "x","cancel","save" }) using (var f=new Fixture("en")) using (var context=new ApplicationContext()) using (var pulse=new Timer { Interval=30 }) {
                bool closed=false; int afterClose=0, ticks=0;
                f.Form.FormClosed+=delegate { closed=true; };
                Check(!f.Form.Visible && !IsWindowVisible(f.Form.Handle),"hidden host "+ending+": created HWND remains invisible; no foreground interaction");
                pulse.Tick+=delegate {
                    if (++ticks>150) throw new TimeoutException("hidden modeless host "+ending);
                    Check(Application.MessageLoop && context.MainForm==null,"hidden host "+ending+": real message loop has no MainForm");
                    if(ticks==1) {
                        if (ending=="x") f.Form.Close(); else Click(Find<Button>(f.Form,ending));
                        Check(closed && f.Form.IsDisposed,"hidden host "+ending+": form is actually closed/disposed");
                        return;
                    }
                    if (!closed || ++afterClose<3) return;
                    Check(Application.MessageLoop,"hidden host "+ending+": independent timer still runs three ticks after settings closes");
                    Check(f.Saves==(ending=="save" ? 1 : 0) && f.Form.DialogResult==(ending=="save" ? DialogResult.OK : DialogResult.Cancel),"hidden host "+ending+": transaction closing result");
                    context.ExitThread();
                };
                pulse.Start(); Application.Run(context); pulse.Stop();
                Check(closed && afterClose>=3,"hidden host "+ending+": only test explicitly exits surviving host");
            }
        }
        private static void Integration()
        {
            using(var f=new Fixture("en")) {
                using(var status=new MintStatusLabel()) {
                    status.Font=new Font("Segoe UI",11.25F); status.Width=250;
                    status.Text="Whitelist unavailable. Auto off is paused";
                    Size required=TextRenderer.MeasureText(status.Text,status.Font,
                        new Size(status.Width-status.Padding.Horizontal,0),TextFormatFlags.NoPadding|TextFormatFlags.WordBreak);
                    Check(required.Height>36 && status.Height>=required.Height,"status banner grows to wrapped text height");
                }
                Check(f.Saves==0 && f.ListSaves==0,"opening and initial readback never persist settings or allowlist");
                var header=Find<MintGradientPanel>(f.Form,"header");
                Check(header.Title=="ScreenOffTray" && header.TitleBounds.Width>0 && header.TitleFont.GetHeight()>0,
                    "gradient header owns a visible, DPI-managed title");
                Check(f.Form.Controls.Find("other-settings-toggle",true).Length==0,"C layout has no More options disclosure");
                var cat=Find<StaticCatControl>(f.Form,"mint-cat");
                Check(cat.English && cat.AccessibleName.Contains("Cat") && cat.AccessibleRole==AccessibleRole.Graphic && !cat.TabStop && cat.Parent.Name=="header","static cat is a noninteractive header image with stable name");
                Check(f.Form.ShowInTaskbar && f.Form.MinimizeBox && f.Form.MaximizeBox && f.Form.FormBorderStyle==FormBorderStyle.Sizable,"standard independent native window is retained");
                Check(f.Form.Controls.Find("exit",true).Length==0,"settings never exposes or dispatches tray exit");
                f.Form.RefreshSetup(new SetupPanelState { Visible=true,Checking=true,Detail="Fake preparation failure",Diagnostics="Fake recovery diagnostics" });
                Link(Find<LinkLabel>(f.Form,"details"));
                Check(Find<TextBox>(f.Form,"diagnostics").Text.Contains("recovery"),"preparation diagnostics retain recovery detail");
                f.Form.RefreshUpdate(false,"Fixture update failed",false,true);
                Check(Find<Label>(f.Form,"update-detail").Text.Contains("failed"),"update error remains readable and retryable");
                f.State.Audio=AudioSnapshot.Unknown("fixture unavailable"); f.Form.RefreshPlayback();
                Check(Find<TextBox>(f.Form,"playback-warning-details").Text.Contains("unavailable"),"unknown audio retains full diagnostics");
                f.State.AppliedOptions.WhitelistKnown=false; f.State.AppliedOptions.WhitelistPaths=new[]{f.State.Audio.Sources[0].ExePath}.Concat(Enumerable.Range(1,9).Select(i=>@"C:\UI-Fixture\App"+i+".exe")).ToArray();
                f.State.Timing=new AutoOffTimingResult(0,AutoOffPauseReason.PlaybackSettingsUnknown); f.Form.RefreshPlayback();
                Check(Find<ComboBox>(f.Form,"whitelist-picker").Text.Contains("unavailable") && !Find<Button>(f.Form,"remove-whitelist").Enabled,"unknown allowlist stays distinct from empty");
                f.Form.Dispose(); Check(cat.IsDisposed,"settings disposal releases native cat child");
            }
            using(var f=new Fixture("zh-CN")) {
                var hotkey=Find<HotkeyCaptureBox>(f.Form,"hotkey"); hotkey.RecordPhysicalKey((int)Keys.L,0); hotkey.RecordKey(Keys.L,true);
                Click(Find<Button>(f.Form,"save")); Check(f.Saves==0,"invalid unmodified shortcut blocks save");
                Click(Find<Button>(f.Form,"defaults"));
                Find<MintNumberBox>(f.Form,"timeout").Value=17; Find<CheckBox>(f.Form,"automatic-updates").Checked=false;
                Find<CheckBox>(f.Form,"playback-limit-enabled").Checked=false;
                Click(Find<Button>(f.Form,"save"));
                Check(f.Saves==1 && f.Saved.TimeoutMinutes==17 && !f.Saved.AutomaticUpdateChecks && !f.Saved.PlaybackLimitEnabled,"Save collects original SettingsValues fields exactly once");
            }
        }
        private static void ApplyDpi(Fixture f,int dpi) { typeof(SettingsDialog).GetMethod("ApplyWindowDpi",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(f.Form,new object[]{dpi}); f.Layout(); }
        private static void DpiRebuild()
        {
            foreach(string lang in new[] {"zh-CN","en"}) foreach(int dpi in new[] {96,120,144,192}) using(var f=new Fixture(lang)) {
                ApplyDpi(f,dpi); var old=Find<PictureBox>(f.Form,"playing-icon-0"); Size size=old.Size; Padding margin=old.Margin;
                var add=Find<Button>(f.Form,"add-whitelist-0"); Size button=add.MinimumSize; Font font=add.Font;
                f.State.Audio.Sources=new[] {new AudioSource {ExePath=@"C:\CSettingsFixture\replacement.exe",DisplayName="Replacement"}}; f.Form.RefreshPlayback(); f.Layout();
                var next=Find<PictureBox>(f.Form,"playing-icon-0");
                geometry.Add(lang+"/"+dpi+" rebuild old_icon="+size+" new_icon="+next.Size+" old_margin="+margin+" new_margin="+next.Margin+" old_button="+button+" new_button="+Find<Button>(f.Form,"add-whitelist-0").MinimumSize+" old_font="+font.Size+" new_font="+Find<Button>(f.Form,"add-whitelist-0").Font.Size);
                Check(old.IsDisposed && next.Size==size && next.Margin==margin && Find<Button>(f.Form,"add-whitelist-0").MinimumSize==button && Find<Button>(f.Form,"add-whitelist-0").Font==font,lang+"/"+dpi+": DPI source rebuild retains icon geometry, action and current font");
                Click(Find<Button>(f.Form,"add-whitelist-0"));
                Check(next.IsDisposed && !Find<Button>(f.Form,"add-whitelist-0").Enabled && f.ListSaves==1,lang+"/"+dpi+": immediate allowlist rebuild keeps Added state");
                int height=Find<Panel>(f.Form,"playing-viewport").Height;
                f.State.Audio.Sources=Enumerable.Range(0,4).Select(i=>new AudioSource {ExePath=@"C:\CSettingsFixture\p"+i+".exe",DisplayName="Player "+i}).ToArray(); f.Form.RefreshPlayback(); f.Layout();
                Check(Find<Panel>(f.Form,"playing-viewport").Height==3*height && Find<PictureBox>(f.Form,"playing-icon-3").Size==size,lang+"/"+dpi+": multiple players remain bounded at three full rows");
            }
        }
        private static Rectangle Relative(Control child,Control parent) { var point=Point.Empty; for(Control c=child;c!=parent;c=c.Parent) point.Offset(c.Location); return new Rectangle(point,child.Size); }
        private static void LowError()
        {
            using(var f=new Fixture("en")) using(SettingsDpiContext.EnterUi()) {
                f.Form.StartPosition=FormStartPosition.Manual; f.Form.Location=Point.Empty; ApplyDpi(f,192);
                var work=new Rectangle(0,0,960,540); Size frame=new Size(f.Form.Width-f.Form.ClientSize.Width,f.Form.Height-f.Form.ClientSize.Height);
                f.Form.MinimumSize=new Size(600,432);
                typeof(SettingsDialog).GetField("lastWorkArea",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(f.Form,typeof(SettingsDialog).GetMethod("WindowWorkArea",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(f.Form,null));
                f.Form.Bounds=SettingsDialog.FitToWorkArea(f.Form.Bounds,work,192,frame,true); f.Form.Show(); Pump(250);
                Find<MintNumberBox>(f.Form,"timeout").Text="0";
                f.Form.ScrollControlIntoView(Find<Control>(f.Form,"save")); Pump(100);
                Click(Find<Button>(f.Form,"save")); Pump(150);
                var error=Find<Label>(f.Form,"error"); Rectangle bounds=Relative(error,f.Form);
                Verify(f.Saves==0 && error.Visible && error.Text.Contains("1–1440"),"low validation: invalid number blocks save and shows real error caption");
                Verify(bounds.Top>=0 && bounds.Bottom<=f.Form.ClientSize.Height && bounds.Left>=0 && bounds.Right<=f.Form.ClientSize.Width,"low validation: error is actually visible without test scrolling after save");
                geometry.Add("outer="+f.Form.Bounds+" client="+f.Form.ClientSize+" error_client="+bounds+" caption="+error.Text+" native_dpi="+GetDpiForWindow(f.Form.Handle)+" simulated_dpi=192");
                using(var bitmap=new Bitmap(f.Form.ClientSize.Width,f.Form.ClientSize.Height)) {
                    using(var graphics=Graphics.FromImage(bitmap)) { IntPtr dc=graphics.GetHdc(); try { PrintWindow(f.Form.Handle,dc,1); } finally { graphics.ReleaseHdc(dc); } }
                    bitmap.Save(Path.Combine(output,"low-error-native-client.png"),ImageFormat.Png);
                }
                results.Add("RESULT checks="+results.Count+" failures="+failures+"; only previously unfinished low-height validation visibility; original fake SettingsDialog/private desktop/simulated DPI; child process owns renderer cleanup.");
                File.WriteAllLines(Path.Combine(output,"ui-results.txt"),results.ToArray(),Encoding.UTF8); File.WriteAllLines(Path.Combine(output,"ui-layout.txt"),geometry.ToArray(),Encoding.UTF8);
                Environment.Exit(failures==0 ? 0 : 1);
            }
        }
        private static void NativeModelessLifetime()
        {
            using(var f=new Fixture("en")) using(var context=new ApplicationContext()) using(var timer=new Timer {Interval=200}) using(SettingsDpiContext.EnterUi()) {
                f.Form.StartPosition=FormStartPosition.Manual; f.Form.Location=Point.Empty;
                bool closed=false; int ticks=0;
                f.Form.FormClosing+=delegate { File.AppendAllText(Path.Combine(output,"ui-close-phases.txt"),"FormClosing event\n"); };
                f.Form.FormClosed+=delegate { closed=true; File.AppendAllText(Path.Combine(output,"ui-close-phases.txt"),"FormClosed event\n"); };
                f.Form.Disposed+=delegate { File.AppendAllText(Path.Combine(output,"ui-close-phases.txt"),"Disposed event\n"); };
                timer.Tick+=delegate {
                    ticks++;
                    if(ticks==1) {
                        File.AppendAllText(Path.Combine(output,"ui-close-phases.txt"),"native message loop closing shown fixture\n");
                        Click(Find<Button>(f.Form,"cancel"));
                        File.AppendAllText(Path.Combine(output,"ui-close-phases.txt"),"close callback returned\n");
                    }
                    if(ticks>=3) {
                        Check(closed && f.Form.IsDisposed && f.Saves==0 && context.MainForm==null && Application.MessageLoop,"shown settings closes while its real tray-like host message loop survives");
                        timer.Stop(); context.ExitThread();
                    }
                };
                f.Form.Show(); timer.Start(); Application.Run(context);
            }
        }
        private static void Pump(int milliseconds)
        {
            var until=DateTime.UtcNow.AddMilliseconds(milliseconds);
            do { Application.DoEvents(); System.Threading.Thread.Sleep(10); } while(DateTime.UtcNow<until);
        }
        private static int Ink(Bitmap bitmap,Rectangle region,out Rectangle bounds)
        {
            region=Rectangle.Intersect(region,new Rectangle(Point.Empty,bitmap.Size));
            int count=0,left=region.Right,top=region.Bottom,right=region.Left,bottom=region.Top;
            for(int y=region.Top;y<region.Bottom;y++) for(int x=region.Left;x<region.Right;x++) {
                Color c=bitmap.GetPixel(x,y);
                if(c.A>128 && c.R<190 && c.G<190 && c.B<190) { count++; left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x);bottom=Math.Max(bottom,y); }
            }
            bounds=count==0 ? Rectangle.Empty : Rectangle.FromLTRB(left,top,right+1,bottom+1); return count;
        }
        // One bounded visual batch, normalized to the HTML inner surface:
        // remove its fake title bar and 1px card border; native chrome is excluded.
        private static void PreviewForm(string lang,int dpi)
        {
            Action<string> phase=value=>File.AppendAllText(Path.Combine(output,"ui-preview-phases.txt"),value+Environment.NewLine);
            phase("construct fixture");
            using(var f=new Fixture(lang)) using(SettingsDpiContext.EnterUi()) {
                phase("fixture constructed");
                bool actualDpi = dpi==0;
                if(actualDpi) dpi=(int)GetDpiForWindow(f.Form.Handle);
                bool narrow=lang=="en";
                float scale=dpi/96F;
                Func<int,int> units=value=>(int)Math.Round(value*scale);
                f.Form.StartPosition=FormStartPosition.Manual; f.Form.Location=Point.Empty;
                phase("apply dpi"); ApplyDpi(f,dpi); phase("dpi applied");
                f.State.AppliedOptions.WhitelistPaths=new[]{f.State.Audio.Sources[0].ExePath}.Concat(Enumerable.Range(1,9).Select(i=>@"C:\UI-Fixture\App"+i+".exe")).ToArray();
                f.State.Timing=new AutoOffTimingResult(0,new PlaybackProtectionState(AutoOffPauseReason.Playback,false,f.State.Audio.Sources[0].DisplayName)); phase("refresh playback"); f.Form.RefreshPlayback(); phase("playback refreshed");
                phase("resize"); f.Form.ClientSize=new Size(units(narrow ? 410 : 984),units(narrow ? 850 : 654));
                phase("show original"); f.Form.Show(); phase("pump original"); Pump(500); phase("shown");
                var root=Find<TableLayoutPanel>(f.Form,"settings-window");
                var viewport=Find<Panel>(root,"viewport");
                var refresh=Find<LinkLabel>(root,"refresh-audio"); refresh.Focus(); Pump(150);
                var cat=Find<StaticCatControl>(root,"mint-cat");
                Verify(cat.Size==new Size(units(narrow ? 92 : 134),units(narrow ? 88 : 128)),"preview: cat uses the reference avatar size");
                var header=Find<Control>(root,"header");
                Verify(cat.Left==units(narrow ? 16 : 30) && cat.Top==(narrow ? units(14) : 1),"preview: cat avatar stays in the themed header");
                Verify(root.Parent==f.Form && IsWindowVisible(f.Form.Handle),"preview: original form stays on private desktop without reparenting");
                Verify(!viewport.HorizontalScroll.Visible && !f.Form.HorizontalScroll.Visible,"preview: no horizontal overflow");
                if(!narrow) Verify(!viewport.VerticalScroll.Visible,"preview: wide body fits without scrolling");
                var startup=Find<CheckBox>(root,"startup");
                var updates=Find<CheckBox>(root,"automatic-updates");
                Verify(startup.Bottom<=updates.Top && updates.Parent.ClientRectangle.Contains(updates.Bounds),"preview: preference checkbox rows reserve full minimum height without overlap or clipping");
                foreach(string name in new[]{"language","whitelist-picker"}) {
                    var combo=Find<ComboBox>(root,name); int count=combo.Items.Count;
                    combo.Focus(); combo.DroppedDown=true;
                    Verify(combo.DroppedDown,"preview: native dropdown opens "+name);
                    Pump(80);
                    geometry.Add("dropdown "+name+" after-private-desktop-pump-open="+combo.DroppedDown+" focused="+combo.Focused);
                    combo.DroppedDown=false;
                    if(name=="language") { combo.SelectedIndex=1-combo.SelectedIndex; combo.SelectedIndex=lang=="en" ? 1 : 0; }
                    Verify(combo.Items.Count==count && !combo.DroppedDown,"preview: native dropdown closes and preserves items "+name);
                }
                refresh.Focus(); viewport.AutoScrollPosition=Point.Empty; Pump(100);
                Action<string> capture=suffix=> {
                    ClientRect native; GetClientRect(f.Form.Handle,out native);
                    using(var bitmap=new Bitmap(native.Right,native.Bottom)) {
                        using(var graphics=Graphics.FromImage(bitmap)) {
                            IntPtr dc=graphics.GetHdc(); bool ok;
                            try { ok=PrintWindow(f.Form.Handle,dc,1); } finally { graphics.ReleaseHdc(dc); }
                            Verify(ok,"preview: native client capture "+suffix);
                        }
                        bitmap.Save(Path.Combine(output,"preview-"+lang+"-"+dpi+"-"+suffix+".png"),ImageFormat.Png);
                    }
                };
                capture("native-client");
                if(narrow) { viewport.AutoScrollPosition=new Point(0,10000); Pump(100); capture("bottom"); viewport.AutoScrollPosition=Point.Empty; }
                Action<Control> log=null;
                log=c=> { geometry.Add(c.Name+" relative="+Relative(c,f.Form)+" font="+c.Font.Name+"/"+c.Font.Size+" visible="+c.Visible); foreach(Control child in c.Controls) log(child); };
                geometry.Add("actual_hwnd_dpi="+GetDpiForWindow(f.Form.Handle)+" requested_layout_dpi="+dpi+" system_font_dpi="+GetDpiForSystem()+" capture=PrintWindow original private desktop; client excludes native title bar");
                log(root);
                Link(refresh); Verify(f.Refreshes==1,"preview: refresh icon link retains callback");
                int initialHeight=Find<Control>(root,"playing-copy-0").Height;
                f.State.Audio.Sources=new[]{new AudioSource {ExePath=@"C:\CSettingsFixture\replacement.exe",DisplayName="Replacement"}};
                f.Form.RefreshPlayback(); f.Layout(); Pump(80);
                Verify(Find<Control>(root,"playing-copy-0").Height==initialHeight,"preview: source rebuild retains scaled row height");
                Verify(f.Saves==0 && f.ListSaves==0,"preview: visual and dropdown checks never save settings");
                File.WriteAllLines(Path.Combine(output,"ui-results.txt"),results.Concat(new[]{"RESULT checks="+results.Count+" failures="+failures+"; original private desktop form, fixture data, "+(actualDpi ? "actual HWND " : "simulated ")+dpi+" DPI; no hardware, audio or settings store."}).ToArray(),Encoding.UTF8);
                File.WriteAllLines(Path.Combine(output,"ui-layout.txt"),geometry.ToArray(),Encoding.UTF8);
                Console.WriteLine("preview "+lang+"/"+dpi+" failures="+failures);
                // Existing renderer convention: process owns private-window cleanup.
                Environment.Exit(failures==0 ? 0 : 1);
            }
        }
        private static void DirectForm(string lang,int dpi,bool low=false)
        {
            var clock=System.Diagnostics.Stopwatch.StartNew();
            Action<string> stamp=phase=>File.AppendAllText(Path.Combine(output,"ui-direct-phases.txt"),clock.ElapsedMilliseconds+"ms "+phase+Environment.NewLine);
            stamp("construct original fake SettingsDialog, no reparent");
            using(var f=new Fixture(lang)) using(SettingsDpiContext.EnterUi()) {
                f.Form.StartPosition=FormStartPosition.Manual; f.Form.Location=Point.Empty;
                ApplyDpi(f,dpi); f.State.AppliedOptions.WhitelistPaths=new string[0]; f.State.Timing=new AutoOffTimingResult(0,AutoOffPauseReason.None); f.Form.RefreshPlayback();
                var root=Find<TableLayoutPanel>(f.Form,"settings-window"); var viewport=Find<Panel>(root,"viewport");
                var footer=Find<Control>(root,"footer"); var cat=Find<StaticCatControl>(root,"mint-cat");
                foreach(bool narrow in low ? new[]{true} : new[]{false,true}) {
                    string name=(low ? "low-" : "direct-")+lang+"-"+dpi+"-"+(narrow ? "narrow" : "wide");
                    f.Form.ClientSize=new Size((int)Math.Round((narrow ? 340 : 760)*dpi/96F),(int)Math.Round((narrow ? 700 : (lang=="en" ? 620 : 590))*dpi/96F));
                    if(low) {
                        var work=new Rectangle(0,0,960,540); Size frame=new Size(f.Form.Width-f.Form.ClientSize.Width,f.Form.Height-f.Form.ClientSize.Height);
                        f.Form.MinimumSize=new Size(Math.Min((int)(300*dpi/96F),920),432);
                        // Model the same minimum-size cap as AdaptToCurrentWorkArea,
                        // while keeping the actual non-input desktop unchanged.
                        typeof(SettingsDialog).GetField("lastWorkArea",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(f.Form,typeof(SettingsDialog).GetMethod("WindowWorkArea",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(f.Form,null));
                        f.Form.Bounds=SettingsDialog.FitToWorkArea(f.Form.Bounds,work,dpi,frame,true);
                        Verify(work.Contains(f.Form.Bounds) && f.Form.Height<=432,name+": actual HWND fits simulated 960x540 work area at 200 percent");
                    }
                    stamp(name+" Show original"); f.Form.Show(); Pump(1200); stamp(name+" native pump complete");
                    if(low) { f.Form.AutoScrollPosition=Point.Empty; Pump(100); }

                    Verify(Object.ReferenceEquals(cat.FindForm(),f.Form),name+": static cat remains in the original shown SettingsDialog");
                    Verify(f.Form.Visible && IsWindowVisible(f.Form.Handle) && root.Parent==f.Form,name+": original native form shown only on private desktop");
                    ClientRect nativeClient; GetClientRect(f.Form.Handle,out nativeClient);
                    Size actualClient = new Size(nativeClient.Right,nativeClient.Bottom);
                    Verify(low ? root.Width<=actualClient.Width && root.Height>=actualClient.Height : root.Size==actualClient,name+": root fills or vertically scrolls within actual native client");
                    Verify(footer.Bottom<=root.Height && Math.Abs(root.Height-footer.Bottom-footer.Margin.Bottom)<=1,name+": themed footer is bottom aligned with its card margin");
                    Verify(!viewport.HorizontalScroll.Visible,name+": no horizontal scrolling");
                    int minimumViewport=(low ? 96 : narrow ? 240 : 160)*dpi/96;
                    Verify(!viewport.HorizontalScroll.Visible && viewport.Height>=minimumViewport,name+": usable native viewport; size="+viewport.ClientSize+" hscroll="+viewport.HorizontalScroll.Visible+" vscroll="+viewport.VerticalScroll.Visible);
                    Verify(!f.Form.HorizontalScroll.Visible,name+": no outer horizontal scrolling");
                    foreach(string numberName in new[]{"timeout","playback-limit-minutes"}) {
                        var number=Find<MintNumberBox>(root,numberName); var minus=Find<Button>(root,numberName+"-minus"); var plus=Find<Button>(root,numberName+"-plus");
                        Verify(number.Left>=minus.Right && number.Right<=plus.Left && number.Font.Height/(dpi/96F)>=14,name+": full readable stepper "+numberName);
                    }
                    Verify(Find<ComboBox>(root,"whitelist-picker").Text==(lang=="en" ? "No apps added" : "尚未添加程序"),name+": native empty allowlist caption");
                    string state="cat_visible="+cat.Visible+" cat_bounds="+cat.Bounds;
                    geometry.Add(name+" native_dpi="+GetDpiForWindow(f.Form.Handle)+" requested_simulated_dpi="+dpi+" client="+f.Form.ClientSize+" root="+root.Bounds+" rowheights="+String.Join(",",root.GetRowHeights())+" rowstyles="+String.Join(";",root.RowStyles.Cast<RowStyle>().Select(x=>x.SizeType+":"+x.Height))+" viewport="+viewport.Bounds+" footer="+footer.Bounds+" footer_preferred="+footer.GetPreferredSize(new Size(root.Width,0))+" "+state);
                    var measuredContent=Find<Control>(root,"content");
                    geometry.Add(name+" CLIENT_DIAG managed="+f.Form.ClientRectangle+" native="+nativeClient.Right+"x"+nativeClient.Bottom+" display="+f.Form.DisplayRectangle+" auto="+f.Form.AutoScroll+" hscroll="+f.Form.HorizontalScroll.Visible+" vscroll="+f.Form.VerticalScroll.Visible+" root_dock="+root.Dock+" content="+measuredContent.Bounds+" preferred="+measuredContent.GetPreferredSize(new Size(viewport.Width,0))+" content_padding="+measuredContent.Padding+" playback="+Find<Control>(root,"playback-settings").Bounds);
                    Rectangle catBox=Relative(cat,root),inkBounds;
                    using(var child=new Bitmap(cat.Width,cat.Height)) {
                        cat.DrawToBitmap(child,new Rectangle(Point.Empty,cat.Size));
                        int ink=Ink(child,new Rectangle(Point.Empty,child.Size),out inkBounds);
                        child.Save(Path.Combine(output,name+"-cat-control.png"),ImageFormat.Png);
                        geometry.Add(name+" CAT child_pixels="+ink+" ink_bounds="+inkBounds);
                        Verify(ink>=30*dpi/96F && inkBounds.Height>=24*dpi/96F,name+": cat control actually paints outline pixels "+ink+" "+inkBounds);
                    }
                    using(var bitmap=new Bitmap(root.Width,root.Height)) {
                        root.DrawToBitmap(bitmap,new Rectangle(Point.Empty,root.Size)); bitmap.Save(Path.Combine(output,name+"-root.png"),ImageFormat.Png);
                        int ink=Ink(bitmap,catBox,out inkBounds); geometry.Add(name+" CAT root_pixels="+ink+" ink_bounds="+inkBounds);
                        Verify(ink>=30*dpi/96F && inkBounds.Height>=24*dpi/96F,name+": complete root capture contains actual cat pixels "+ink+" "+inkBounds);
                    }
                    using(var bitmap=new Bitmap(actualClient.Width,actualClient.Height)) {
                        using(var graphics=Graphics.FromImage(bitmap)) {
                            IntPtr dc=graphics.GetHdc(); bool captured;
                            try { captured=PrintWindow(f.Form.Handle,dc,1); } finally { graphics.ReleaseHdc(dc); }
                            Verify(captured,name+": native PrintWindow client capture succeeds");
                        }
                        bitmap.Save(Path.Combine(output,name+"-native-client.png"),ImageFormat.Png);
                        int ink=Ink(bitmap,Relative(cat,f.Form),out inkBounds); geometry.Add(name+" CAT native_pixels="+ink+" ink_bounds="+inkBounds);
                        Verify(ink>=30*dpi/96F && inkBounds.Height>=24*dpi/96F,name+": native client capture contains cat pixels "+ink+" "+inkBounds);
                    }
                    if(narrow) { viewport.AutoScrollPosition=new Point(0,10000); Pump(100); CaptureSurface(root,name+"-bottom.png"); viewport.AutoScrollPosition=Point.Empty; }
                    if(low) {
                        var save=Find<Control>(root,"save"); f.Form.ScrollControlIntoView(save); Pump(100);
                        Rectangle saveInClient=Relative(save,f.Form);
                        Verify(saveInClient.Top>=0 && saveInClient.Bottom<=f.Form.ClientSize.Height,name+": Save reachable inside client after scrolling");
                        using(var bitmap=new Bitmap(f.Form.ClientSize.Width,f.Form.ClientSize.Height)) {
                            using(var graphics=Graphics.FromImage(bitmap)) { IntPtr dc=graphics.GetHdc(); try { PrintWindow(f.Form.Handle,dc,1); } finally { graphics.ReleaseHdc(dc); } }
                            bitmap.Save(Path.Combine(output,name+"-footer-client.png"),ImageFormat.Png);
                        }
                        f.Form.ScrollControlIntoView(Find<Control>(root,"timeout")); Pump(100);
                        Rectangle fieldInClient=Relative(Find<Control>(root,"timeout"),f.Form);
                        Verify(fieldInClient.Top>=0 && fieldInClient.Bottom<=f.Form.ClientSize.Height,name+": body field reachable inside client after scrolling");
                        geometry.Add(name+" save_client="+saveInClient+" field_client="+fieldInClient+" form_scroll="+f.Form.AutoScrollMinSize+" outer="+f.Form.Bounds);
                        Action<string> captureFeedback=suffix=> {
                            using(var image=new Bitmap(f.Form.ClientSize.Width,f.Form.ClientSize.Height)) {
                                using(var graphics=Graphics.FromImage(image)) { IntPtr dc=graphics.GetHdc(); try { PrintWindow(f.Form.Handle,dc,1); } finally { graphics.ReleaseHdc(dc); } }
                                image.Save(Path.Combine(output,name+"-"+suffix+"-client.png"),ImageFormat.Png);
                            }
                        };
                        var idle=Find<MintNumberBox>(root,"timeout"); var feedback=Find<Label>(root,"error");
                        idle.Text="0"; f.Form.ScrollControlIntoView(save); Pump(100); Click((Button)save); Pump(100);
                        Rectangle numericError=Relative(feedback,f.Form);
                        Verify(f.Form.Visible && IsWindowVisible(f.Form.Handle) && f.Saves==0 && feedback.Visible && feedback.Text.Contains("1–1440"),name+": visible original Form rejects invalid minutes without save callback");
                        Verify(numericError.Top>=0 && numericError.Bottom<=f.Form.ClientSize.Height && numericError.Left>=0 && numericError.Right<=f.Form.ClientSize.Width,name+": SetError automatically scrolls numeric validation into client; no test scroll after Save");
                        geometry.Add(name+" NUMERIC_ERROR client="+numericError+" caption="+feedback.Text+" saves="+f.Saves); captureFeedback("invalid-minutes");
                        idle.Value=3; f.Form.AutoScrollPosition=Point.Empty; Pump(50);
                        var shortcut=Find<HotkeyCaptureBox>(root,"hotkey"); shortcut.RecordPhysicalKey((int)Keys.L,0); shortcut.RecordKey(Keys.L,true); Pump(100);
                        Rectangle shortcutError=Relative(feedback,f.Form);
                        Verify(!String.IsNullOrEmpty(shortcut.Error) && feedback.Visible && f.Saves==0,name+": invalid unmodified shortcut publishes SetError without saving");
                        Verify(shortcutError.Top>=0 && shortcutError.Bottom<=f.Form.ClientSize.Height && shortcutError.Left>=0 && shortcutError.Right<=f.Form.ClientSize.Width,name+": shortcut SetError automatically scrolls into client; no test scroll after key capture");
                        geometry.Add(name+" SHORTCUT_ERROR client="+shortcutError+" caption="+feedback.Text+" saves="+f.Saves); captureFeedback("invalid-shortcut");
                        Click(Find<Button>(root,"defaults"));
                        f.Form.RefreshSetup(new SetupPanelState {Visible=true,CanPrepare=true,Detail="Fixture only: preparation available"}); Pump(100);
                        var prepare=Find<Button>(root,"prepare"); f.Form.ScrollControlIntoView(prepare); Pump(50); Rectangle prepareInClient=Relative(prepare,f.Form);
                        Verify(prepare.Visible && prepare.Enabled && prepareInClient.Top>=0 && prepareInClient.Bottom<=f.Form.ClientSize.Height && prepareInClient.Left>=0 && prepareInClient.Right<=f.Form.ClientSize.Width,name+": preparation button is reachable by whole-page scrolling");
                        Click(prepare); Pump(100); Click(prepare);
                        Verify(f.Prepares==1 && !prepare.Enabled && !Find<Button>(root,"save").Enabled,name+": preparation invokes fake callback exactly once and keeps Save gated");
                        var cancelPrepare=Find<Button>(root,"cancel-prepare"); f.Form.ScrollControlIntoView(cancelPrepare); Pump(50); Rectangle cancelInClient=Relative(cancelPrepare,f.Form);
                        Verify(cancelPrepare.Visible && cancelPrepare.Enabled && cancelInClient.Top>=0 && cancelInClient.Bottom<=f.Form.ClientSize.Height && cancelInClient.Left>=0 && cancelInClient.Right<=f.Form.ClientSize.Width,name+": cancel preparation button is reachable by whole-page scrolling");
                        captureFeedback("preparing"); Click(cancelPrepare); Pump(100); Click(cancelPrepare);
                        Verify(f.Cancels==1 && !cancelPrepare.Enabled && !f.Form.IsDisposed,name+": cancellation invokes fake callback exactly once and waits for acknowledgement");
                        f.Form.RefreshSetup(new SetupPanelState {Ready=true}); Pump(100);
                        Verify(!f.Form.IsDisposed && Find<Button>(root,"save").Enabled && f.Prepares==1 && f.Cancels==1 && f.Saves==0 && f.ListSaves==0,name+": fake safe acknowledgement restores editing without settings or list persistence");
                        geometry.Add(name+" PREPARATION prepare_client="+prepareInClient+" cancel_client="+cancelInClient+" prepares="+f.Prepares+" cancels="+f.Cancels+" saves="+f.Saves+" list_saves="+f.ListSaves);
                        f.Form.ClientSize=new Size((int)(718*dpi/96F),(int)(480*dpi/96F)); Pump(150);
                        Verify(!f.Form.AutoScroll && root.Dock==DockStyle.Fill && root.Size==f.Form.ClientSize,name+": returning to large client exits whole-page scrolling and fills client");
                        Verify(!viewport.HorizontalScroll.Visible && viewport.Height>=120*dpi/96,name+": restored normal C body has a usable viewport; vertical scrolling is allowed here");
                        geometry.Add(name+" RESTORE client="+f.Form.ClientSize+" root="+root.Bounds+" dock="+root.Dock+" form_scroll="+f.Form.AutoScroll+" viewport="+viewport.Bounds);
                        CaptureSurface(root,name+"-restored-large.png");
                    }
                    File.WriteAllLines(Path.Combine(output,"ui-layout.txt"),geometry.ToArray(),Encoding.UTF8);
                    stamp(name+" captures complete");
                }
                Verify(f.Saves==0 && f.ListSaves==0,"direct native Show/resize/captures never persist values or whitelist");
                // This renderer owns one process/private desktop. Terminate its UI
                // with that process after durable evidence; lifecycle behavior has
                // a separate real-message-loop modeless group, not this print probe.
                results.Add("RESULT checks="+results.Count+" failures="+failures+"; original SettingsDialog, fake callbacks, private desktop, simulated DPI; renderer cleanup is owned process exit, not Close/Dispose acceptance.");
                File.WriteAllLines(Path.Combine(output,"ui-results.txt"),results.ToArray(),Encoding.UTF8);
                File.WriteAllLines(Path.Combine(output,"ui-layout.txt"),geometry.ToArray(),Encoding.UTF8);
                stamp("render evidence flushed; owned child process exits; parent closes private desktop");
                Environment.Exit(failures==0 ? 0 : 1);
            }
        }
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window,IntPtr dc,uint flags);
        [StructLayout(LayoutKind.Sequential)] private struct ClientRect { internal int Left,Top,Right,Bottom; }
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window,out ClientRect rect);
        private static void CaptureSurface(Control surface,string name)
        {
            using(var bitmap=new Bitmap(surface.Width,surface.Height)) {
                surface.DrawToBitmap(bitmap,new Rectangle(Point.Empty,surface.Size));
                bitmap.Save(Path.Combine(output,name),ImageFormat.Png);
            }
        }
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
        private static int Probe()
        {
            var clock=System.Diagnostics.Stopwatch.StartNew();
            Action<string> stamp=phase=>File.AppendAllText(Path.Combine(output,"ui-probe.txt"),clock.ElapsedMilliseconds+"ms "+phase+Environment.NewLine);
            try {
                stamp("start fixture");
                using(var f=new Fixture("zh-CN")) using(SettingsDpiContext.EnterUi()) {
                    stamp("fixture ready; apply96 begin"); ApplyDpi(f,96); stamp("apply96 done");
                    f.State.AppliedOptions.WhitelistPaths=new string[0]; f.State.Timing=new AutoOffTimingResult(0,AutoOffPauseReason.None); f.Form.RefreshPlayback(); stamp("readPlayback done");
                    Control surface=Find<Control>(f.Form,"settings-window");
                    using(var host=new Form { AutoScaleMode=AutoScaleMode.None,FormBorderStyle=FormBorderStyle.None,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=Point.Empty,Font=f.Form.Font,BackColor=f.Form.BackColor,ClientSize=new Size(718,480) }) {
                        f.Form.Controls.Remove(surface); host.Controls.Add(surface); stamp("root reparented; Show begin");
                        try {
                            host.Show(); Application.DoEvents(); stamp("Show + native layout + DoEvents done");
                            var content=Find<Control>(surface,"content"); var viewport=Find<Panel>(surface,"viewport");
                            stamp("native geometry content="+content.Bounds+" scroll="+viewport.AutoScrollMinSize+" viewport="+viewport.ClientSize+" config="+Find<Control>(surface,"configuration").Size);
                            using(var g=Graphics.FromHwnd(host.Handle)) stamp("font points="+f.Form.Font.SizeInPoints+" Font.Height="+f.Form.Font.Height+" GraphicsDpi="+g.DpiY+" actualHWNDDpi="+GetDpiForWindow(host.Handle)+" GetHeight96="+f.Form.Font.GetHeight(96F)+" GetHeight120="+f.Form.Font.GetHeight(120F));
                            stamp("DrawToBitmap begin without recursive LayoutTree"); CaptureSurface(surface,"ui-probe-native.png"); stamp("DrawToBitmap done");
                            stamp("normal body complete="+(!viewport.VerticalScroll.Visible && !viewport.HorizontalScroll.Visible));
                            if(viewport.VerticalScroll.Visible || viewport.HorizontalScroll.Visible) throw new InvalidOperationException("Normal C body still scrolls");
                        } finally { stamp("host detach begin"); host.Controls.Remove(surface); f.Form.Controls.Add(surface); stamp("host detached"); }
                    }
                    stamp("fixture disposal begin");
                }
                stamp("PASS probe complete; no default desktop activation"); return 0;
            } catch(Exception ex) { stamp("FAIL "+ex.GetType().Name+" "+ex.Message); return 1; }
        }
        private static int ProbeRebuild()
        {
            var clock=System.Diagnostics.Stopwatch.StartNew();
            Action<string> stamp=phase=>File.AppendAllText(Path.Combine(output,"ui-rebuild-probe.txt"),clock.ElapsedMilliseconds+"ms "+phase+Environment.NewLine);
            try {
                stamp("fixture144 begin");
                using(var f=new Fixture("zh-CN")) {
                    ApplyDpi(f,144); stamp("DPI144 applied");
                    var old=Find<PictureBox>(f.Form,"playing-icon-0"); var button=Find<Button>(f.Form,"add-whitelist-0");
                    Size size=old.Size, minimum=button.MinimumSize; Padding margin=old.Margin; Font font=button.Font;
                    stamp("old icon="+size+" margin="+margin+" button="+minimum+" font="+font.Name+"/"+font.SizeInPoints+"/"+font.Style);
                    f.State.Audio.Sources=new[]{new AudioSource {ExePath=@"C:\CSettingsFixture\replacement.exe",DisplayName="Replacement"}};
                    stamp("source RefreshPlayback begin"); f.Form.RefreshPlayback(); stamp("source RefreshPlayback done"); f.Layout(); stamp("fixture.Layout done");
                    var next=Find<PictureBox>(f.Form,"playing-icon-0"); var action=Find<Button>(f.Form,"add-whitelist-0");
                    stamp("new icon="+next.Size+" margin="+next.Margin+" button="+action.MinimumSize+" font="+action.Font.Name+"/"+action.Font.SizeInPoints+"/"+action.Font.Style+" same_font_ref="+Object.ReferenceEquals(font,action.Font));
                    stamp("original geometry assertion="+(old.IsDisposed && next.Size==size && next.Margin==margin && action.MinimumSize==minimum && action.Font==font));
                    int layouts=0, maxFrames=0; var observed=Find<Control>(f.Form,"content");
                    LayoutEventHandler count=delegate {
                        layouts++; if(layouts<=3 || layouts%100==0) {
                            int frames=new System.Diagnostics.StackTrace(false).FrameCount; maxFrames=Math.Max(maxFrames,frames);
                            stamp("ADD layout="+layouts+" frames="+frames+" content="+observed.Bounds+" cap="+Find<Control>(f.Form,"protection-limit-line").Bounds+" stepper="+Find<Control>(f.Form,"playback-limit-minutes-frame").Size);
                        }
                        if(layouts>1000) throw new InvalidOperationException("Bounded layout probe exceeded 1000 layout events");
                    };
                    observed.Layout+=count;
                    stamp("Click Add begin"); Click(action); stamp("Click Add returned; listSaves="+f.ListSaves+" layouts="+layouts+" sampled_max_frames="+maxFrames);
                    observed.Layout-=count;
                    stamp("immediate rebuild assertion="+(next.IsDisposed && !Find<Button>(f.Form,"add-whitelist-0").Enabled && f.ListSaves==1));
                }
                stamp("PASS fixture disposed"); return 0;
            } catch(Exception ex) { stamp("FAIL "+ex.GetType().Name+" "+ex.Message); return 1; }
        }
        private static int LaunchPrivate(string[] arguments)
        {
            string directory=arguments[0];
            string name="CSettings-"+Guid.NewGuid().ToString("N");
            IntPtr desktop=CreateDesktop(name,IntPtr.Zero,IntPtr.Zero,0,0x10000000,IntPtr.Zero);
            if(desktop==IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var start=new StartupInfo { Size=Marshal.SizeOf(typeof(StartupInfo)),Desktop=name }; ProcessInfo process;
            string exe=Assembly.GetExecutingAssembly().Location;
            try {
                if(!CreateProcess(exe,new StringBuilder("\""+exe+"\" \""+Path.GetFullPath(directory)+"\" --private-child"+(arguments.Contains("--probe") ? " --probe" : arguments.Contains("--rebuild-probe") ? " --rebuild-probe" : arguments.Contains("--timeout-probe") ? " --timeout-probe" : " "+arguments.First(a=>a.StartsWith("--group=")))),IntPtr.Zero,IntPtr.Zero,false,0x08000000,IntPtr.Zero,null,ref start,out process)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                try {
                    uint wait=WaitForSingleObject(process.Process,arguments.Contains("--timeout-probe") ? 1000U : 120000U);
                    if(wait==258) {
                        File.AppendAllText(Path.Combine(directory,"ui-timeout.txt"),"TIMEOUT: held child PID="+process.ProcessId+" exe="+exe+" limit_ms="+(arguments.Contains("--timeout-probe") ? 1000 : 120000)+Environment.NewLine);
                        if(!TerminateProcess(process.Process,3)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                        if(WaitForSingleObject(process.Process,5000)!=0) throw new TimeoutException("Held child termination was not acknowledged");
                        File.AppendAllText(Path.Combine(directory,"ui-timeout.txt"),"PASS exact held child terminated and acknowledged; no process-name search or desktop switch."+Environment.NewLine);
                        return arguments.Contains("--timeout-probe") ? 0 : 3;
                    }
                    uint code; GetExitCodeProcess(process.Process,out code); return (int)code;
                }
                finally { CloseHandle(process.Thread); CloseHandle(process.Process); }
            } finally { CloseDesktop(desktop); }
        }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct StartupInfo {
            internal int Size; internal string Reserved,Desktop,Title; internal int X,Y,Width,Height,XChars,YChars,Fill,Flags; internal short Show,ReservedSize; internal IntPtr ReservedData,Input,Output,Error;
        }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { internal IntPtr Process,Thread; internal uint ProcessId,ThreadId; }
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr CreateDesktop(string name,IntPtr device,IntPtr mode,uint flags,uint access,IntPtr security);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool CreateProcess(string app,StringBuilder command,IntPtr processSecurity,IntPtr threadSecurity,bool inherit,uint flags,IntPtr environment,string cwd,ref StartupInfo start,out ProcessInfo process);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle,uint time);
        [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr handle,out uint code);
        [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process,uint code);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        private static void CreateTree(Control root) { IntPtr unused = root.Handle; foreach (Control child in root.Controls) CreateTree(child); }
        private static void Click(Button button) { if (button.Enabled) typeof(Button).GetMethod("OnClick",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(button,new object[] { EventArgs.Empty }); }
        private static void Link(LinkLabel link) { if (link.Enabled) typeof(LinkLabel).GetMethod("OnLinkClicked",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(link,new object[] { new LinkLabelLinkClickedEventArgs(link.Links[0]) }); }
        private static void Close(SettingsDialog form) { typeof(SettingsDialog).GetMethod("RequestClose",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,null); }
        private static T Find<T>(Control root,string name) where T:Control {
            if (root.Name == name) return (T)root;
            var found = root.Controls.Find(name,true); if (found.Length == 0) throw new InvalidOperationException("Missing " + name); return (T)found[0];
        }
    }
}
