using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ScreenOffTray.Playback;

namespace ScreenOffTray.Ui
{
    internal sealed class SettingsValues
    {
        internal int TimeoutMinutes;
        internal HotkeyBinding Hotkey;
        internal string Language;
        internal bool Startup;
        internal bool AutomaticUpdateChecks = true;
        internal bool PlaybackLimitEnabled = true;
        internal int PlaybackLimitMinutes = 60;
    }

    internal sealed class SetupPanelState
    {
        internal bool Visible, Checking, Preparing, CanPrepare, Ready;
        internal string Detail, Diagnostics;
    }

    internal sealed class SettingsDialog : Form
    {
        private readonly TableLayoutPanel root, footer;
        private readonly Panel header;
        private readonly Label heading;
        private readonly TableLayoutPanel stateLine, basics;
        private readonly Label headerStatus;
        private readonly LinkLabel recheckLink;
        private readonly AnimatedCatControl cat;
        private Icon ownedApplicationIcon;
        private Action recheck;
        private readonly MintStatusMark displayMark;
        private readonly TableLayoutPanel footerRow;
        private readonly FlowLayoutPanel versionRow, buttons;
        // Only fonts constructed by this view belong here; inherited/system fonts are borrowed.
        private readonly List<Font> ownedFonts = new List<Font>();
        internal static readonly IEqualityComparer<Font> FontIdentity = new FontReferenceComparer();
        private sealed class FontReferenceComparer : IEqualityComparer<Font>
        {
            public bool Equals(Font first,Font second) { return Object.ReferenceEquals(first,second); }
            public int GetHashCode(Font font) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(font); }
        }
        private readonly Dictionary<Font,float> fontPoints = new Dictionary<Font,float>(FontIdentity);
        private readonly float fontRasterDpi;
        private readonly UiText text;
        private readonly Func<SettingsValues, string> save;
        private readonly Action prepare, cancelPrepare, openDisplaySettings;
        private readonly Action checkUpdates, openRelease;
        private readonly TableLayoutPanel content, configuration, setupSection;
        private readonly PlaybackSettingsPanel playbackPanel;
        private readonly Func<PlaybackPanelState> readPlayback;
        private readonly Timer playbackTimer;
        private readonly Panel viewport;
        private readonly MintNumberBox timeout;
        private readonly HotkeyCaptureBox hotkey;
        private readonly ComboBox language;
        private readonly CheckBox startup;
        private readonly CheckBox automaticUpdates;
        private readonly Label updateHint, updateDetail;
        private readonly LinkLabel updateCheck, releasePage;
        private readonly TableLayoutPanel updateSection;
        private bool checkingUpdates;
        private readonly Label error, setupDetail;
        private readonly Button defaults, saveButton, cancelButton, prepareButton, cancelSetup;
        private readonly LinkLabel details, displaySettings;
        private readonly TextBox diagnostics;
        private SetupPanelState setup = new SetupPanelState();
        private bool expanded, prepareRequested, cancelRequested, closeWhenSafe, saving, sizing, handlingClose, active;
        private float layoutScale = 1F;
        private int appliedDpi = 96;
        private bool layoutReady, adapting, adaptationQueued, wholeWindowScroll;
        private Rectangle lastWorkArea;

        internal SettingsDialog(UiText text, SettingsValues current, Func<SettingsValues, string> save,
            Action prepare, Action cancelPrepare, Action openDisplaySettings,
            Action checkUpdates = null, Action openRelease = null, string version = null,
            Func<PlaybackPanelState> readPlayback = null, Action refreshAudio = null,
            Func<string[], string> saveWhitelist = null)
        {
            SuspendLayout();
            // Framework 4.8 can consume AutoScaleDimensions before the tree exists.
            // This view owns one complete-tree geometry pass, including DPI messages.
            AutoScaleMode = AutoScaleMode.None;
            fontRasterDpi = GetDpiForSystem();
            if (fontRasterDpi <= 0) fontRasterDpi = 96;
            this.text = text; this.save = save; this.prepare = prepare;
            this.cancelPrepare = cancelPrepare; this.openDisplaySettings = openDisplaySettings;
            this.checkUpdates = checkUpdates; this.openRelease = openRelease;
            this.readPlayback = readPlayback;
            Text = text.Pick("ScreenOffTray · 设置", "ScreenOffTray · Settings");
            Font = OwnFont(new Font(text.English ? "Segoe UI" : "Microsoft YaHei UI", 11F));
            BackColor = MintTheme.Paper; ForeColor = MintTheme.Ink;
            AutoScaleDimensions = new SizeF(96, 96);
            FormBorderStyle = FormBorderStyle.Sizable; ShowInTaskbar = true;
            MinimizeBox = MaximizeBox = true;
            StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(718, 480);
            MinimumSize = new Size(300, 300);
            AccessibleDescription = text.Pick("编辑期间暂缓自动关屏；关闭设置后继续在托盘运行。", "Auto off is on hold while editing. Closing settings keeps the app in the tray.");
            root = Table("settings-window", 1); root.AutoSize = false; root.Dock = DockStyle.Fill;
            root.RowCount = 3; root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);
            header = new Panel { Name = "header", Dock = DockStyle.Top, Height = 64, Margin = new Padding(0), BackColor = MintTheme.Header };
            heading = Paragraph("heading", text.Pick("设置", "Settings")); heading.Dock = DockStyle.None;
            heading.Font = OwnFont(new Font(Font.FontFamily, 21F, FontStyle.Bold)); heading.Margin = new Padding(0);
            header.Controls.Add(heading);
            stateLine = Table("display-state-line", 3); stateLine.Dock = DockStyle.None;
            stateLine.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 26)); stateLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); stateLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            displayMark = new MintStatusMark { Name = "display-mark", Size = new Size(24,24), Margin = new Padding(0), TabStop = false };
            headerStatus = Paragraph("display-status", ""); headerStatus.ForeColor = MintTheme.Ink; headerStatus.Margin = new Padding(0,2,0,0);
            recheckLink = new LinkLabel { Name = "recheck", AutoSize = true, Text = text.Pick("检查", "Check"), Margin = new Padding(8,2,0,0) }; StyleLink(recheckLink);
            stateLine.Controls.Add(displayMark,0,0); stateLine.Controls.Add(headerStatus,1,0); stateLine.Controls.Add(recheckLink,2,0); header.Controls.Add(stateLine);
            cat = new AnimatedCatControl { English = text.English, Name = "mint-cat", Size = new Size(162,60), Margin = new Padding(0) };
            cat.Visible = !SystemInformation.HighContrast; header.Controls.Add(cat);
            header.Paint += delegate(object sender,PaintEventArgs e) { DrawRule(e.Graphics,header,false); };
            root.Controls.Add(header,0,0);
            viewport = new Panel { Name = "viewport", Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0), TabStop = false };
            content = Table("content", 1); content.Dock = DockStyle.Top; content.Padding = new Padding(18,16,18,16);
            viewport.Controls.Add(content); root.Controls.Add(viewport, 0, 1);
            configuration = Table("configuration",2); configuration.Dock = DockStyle.Top; configuration.TabIndex = 1;
            configuration.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,49.14F)); configuration.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50.86F));
            basics = Table("basic-settings",1); basics.Dock = DockStyle.Top; basics.Margin = new Padding(0,0,18,0);
            var basicTitle = Paragraph("basic-title",text.Pick("基础设置", "Basic settings"));
            basicTitle.Font = OwnFont(new Font(Font.FontFamily,14.25F,FontStyle.Bold)); basicTitle.Margin = new Padding(0,0,0,16); basics.Controls.Add(basicTitle,0,0);
            var timeoutLine = Table("timeout-line",2); timeoutLine.Dock = DockStyle.Top; timeoutLine.Padding = new Padding(0,0,0,16); timeoutLine.Margin = new Padding(0,0,0,15);
            timeoutLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); timeoutLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var idleLabel = Paragraph("timeout-label",text.Pick("空闲关屏", "Idle timeout")); idleLabel.Margin = new Padding(0,9,0,0);
            timeoutLine.Controls.Add(idleLabel,0,0);
            timeout = new MintNumberBox { Name = "timeout", Value = Math.Max(1,Math.Min(1440,current.TimeoutMinutes)), AccessibleName = text.Pick("空闲关屏分钟数，1至1440", "Idle timeout in minutes, 1 to 1440") };
            var timeoutRow = new FlowLayoutPanel { Name = "timeout-row", AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            timeoutRow.Controls.Add(new MintStepper(timeout)); timeoutRow.Controls.Add(new Label { Text = text.Pick("分钟", "min"), AutoSize = true, Margin = new Padding(12,9,0,0) });
            timeoutLine.Controls.Add(timeoutRow,1,0); basics.Controls.Add(timeoutLine,0,1);
            timeoutLine.Paint += delegate(object sender,PaintEventArgs e) { DrawRule(e.Graphics,timeoutLine,false); };
            var fields = Table("basic-fields",3); fields.Dock = DockStyle.Top;
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,text.English ? 82 : 80)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            hotkey = new HotkeyCaptureBox { Name = "hotkey", Dock = DockStyle.Fill, AccessibleName = text.Pick("关屏快捷键，按组合键录入", "Display-off shortcut, press a key combination") };
            hotkey.SetBinding(current.Hotkey ?? HotkeyBinding.Default);
            AddField(fields,0,text.Pick("快捷键", "Shortcut"), new InputFrame(hotkey) { Dock = DockStyle.Fill });
            defaults = Button("defaults",text.Pick("恢复默认", "Reset"),1); defaults.MinimumSize = new Size(95,39); defaults.Padding = new Padding(4,2,4,2); defaults.Margin = new Padding(10,0,0,16); fields.Controls.Add(defaults,2,0);
            language = new MintComboBox { Name = "language", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Standard, TabIndex = 2, AccessibleName = text.Pick("界面语言", "Language") };
            language.Items.AddRange(new object[] { "简体中文", "English" }); language.SelectedIndex = current.Language == "en" ? 1 : 0;
            AddField(fields,1,text.Pick("语言", "Language"),language); fields.SetColumnSpan(language,2); basics.Controls.Add(fields,0,2);
            startup = new MintCheckBox { Name = "startup", Text = text.Pick("开机启动", "Start with Windows"), Checked = current.Startup, AutoSize = true, TabIndex = 3, Margin = new Padding(0,4,0,4) };
            automaticUpdates = new MintCheckBox { Name = "automatic-updates", Text = text.Pick("自动检查更新", "Check for updates automatically"), Checked = current.AutomaticUpdateChecks, AutoSize = true, TabIndex = 4, Margin = new Padding(0,4,0,4) };
            basics.Controls.Add(startup,0,3); basics.Controls.Add(automaticUpdates,0,4);
            updateHint = Paragraph("update-hint", ""); updateHint.Visible = false; basics.Controls.Add(updateHint,0,5);
            playbackPanel = new PlaybackSettingsPanel(text,current.PlaybackLimitEnabled,current.PlaybackLimitMinutes,refreshAudio,saveWhitelist) { Dock = DockStyle.Top, TabIndex = 1, Margin = new Padding(18,0,0,0) };
            configuration.Controls.Add(basics,0,0); configuration.Controls.Add(playbackPanel,1,0); content.Controls.Add(configuration,0,1);
            configuration.Paint += delegate(object sender,PaintEventArgs e) {
                using (var pen = new Pen(MintTheme.Line)) {
                    if (configuration.ColumnCount == 2) { int x = playbackPanel.Left-(int)Math.Round(18*layoutScale); e.Graphics.DrawLine(pen,x,0,x,configuration.Height-1); }
                    else { int y = playbackPanel.Top-(int)Math.Round(17*layoutScale); e.Graphics.DrawLine(pen,0,y,configuration.Width-1,y); }
                }
            };
            error = Paragraph("error", ""); error.Visible = false; error.ForeColor = SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(162,59,53); error.AccessibleRole = AccessibleRole.Alert;
            content.Controls.Add(error,0,2);

            setupSection = Table("setup",1); setupSection.Dock = DockStyle.Top; setupSection.Visible = false; setupSection.Margin = new Padding(0,3,0,8);
            setupDetail = Paragraph("setup-detail", ""); setupDetail.AutoSize = false; setupDetail.Height = 42; setupDetail.AutoEllipsis = true; setupDetail.AccessibleRole = AccessibleRole.Alert; setupSection.Controls.Add(setupDetail,0,0);
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = new Padding(0,6,0,6) };
            prepareButton = Button("prepare",text.Pick("一键准备", "Prepare"),0); prepareButton.Margin = new Padding(0,0,8,0);
            cancelSetup = Button("cancel-prepare",text.Pick("取消准备", "Cancel setup"),1); actions.Controls.AddRange(new Control[] { prepareButton,cancelSetup }); setupSection.Controls.Add(actions,0,1);
            details = new LinkLabel { Name = "details", AutoSize = true }; StyleLink(details); setupSection.Controls.Add(details,0,2);
            diagnostics = new TextBox { Name = "diagnostics", AutoSize = false, ReadOnly = true, Multiline = true, WordWrap = false, ScrollBars = ScrollBars.Both, Dock = DockStyle.Top, Height = 100, Visible = false };
            setupSection.Controls.Add(diagnostics,0,3);
            displaySettings = new LinkLabel { Name = "display-settings", Text = text.Pick("高级：Windows 显示设置", "Advanced: Windows display settings"), AutoSize = true, Visible = false }; StyleLink(displaySettings); setupSection.Controls.Add(displaySettings,0,4); content.Controls.Add(setupSection,0,0);
            updateSection = Table("update-result",1); updateSection.Dock = DockStyle.Top; updateSection.Visible = false;
            updateDetail = Paragraph("update-detail", ""); updateDetail.Font = OwnFont(new Font(Font.FontFamily,10F)); updateDetail.ForeColor = SecondaryText;
            releasePage = new LinkLabel { Name = "release-page", Text = text.Pick("前往 GitHub 下载", "Download on GitHub"), AutoSize = true, Visible = false }; StyleLink(releasePage);
            updateSection.Controls.Add(updateDetail,0,0); updateSection.Controls.Add(releasePage,0,1); content.Controls.Add(updateSection,0,3);

            footer = Table("footer",1); footer.Dock = DockStyle.Top; footer.Padding = new Padding(18,14,18,16);
            footer.Paint += delegate(object sender,PaintEventArgs e) {
                using (var pen = new Pen(MintTheme.Line)) e.Graphics.DrawLine(pen,footer.Padding.Left,0,footer.Width-footer.Padding.Right,0);
            };
            footerRow = Table("footer-row",2); footerRow.Dock = DockStyle.Top; footerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); footerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var footerCopy = Table("footer-copy",1); footerCopy.Dock = DockStyle.Fill; footerCopy.Margin = new Padding(0,0,12,0);
            var behavior = Paragraph("behavior",text.Pick("关屏后 3 秒防误触，首次实体输入只亮屏。", "3-second guard; first physical key or click only wakes the display."));
            behavior.Font = OwnFont(new Font(Font.FontFamily,9.75F)); behavior.ForeColor = SecondaryText; behavior.Margin = new Padding(0,0,0,6); footerCopy.Controls.Add(behavior,0,0);
            versionRow = new FlowLayoutPanel { Name = "version-row", AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = new Padding(0) };
            var versionLabel = Paragraph("version", "v" + (version ?? Application.ProductVersion)); versionLabel.ForeColor = SecondaryText; versionLabel.Font = OwnFont(new Font(Font.FontFamily,9.75F)); versionLabel.Margin = new Padding(0,0,18,0);
            updateCheck = new LinkLabel { Name = "check-updates", AutoSize = true, Font = OwnFont(new Font(Font.FontFamily,9.75F)), Text = text.Pick("检查更新", "Check updates"), Margin = new Padding(0) }; StyleLink(updateCheck);
            versionRow.Controls.AddRange(new Control[] { versionLabel,updateCheck }); footerCopy.Controls.Add(versionRow,0,1); footerRow.Controls.Add(footerCopy,0,0);
            buttons = new FlowLayoutPanel { Name = "buttons", FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0), TabIndex = 1, Anchor = AnchorStyles.Right };
            cancelButton = Button("cancel",text.Pick("取消", "Cancel"),0); saveButton = Button("save",text.Pick("保存", "Save"),1); StylePrimaryButton(saveButton);
            cancelButton.MinimumSize = new Size(100,39); saveButton.MinimumSize = new Size(106,39); cancelButton.Margin = new Padding(0,0,11,0); saveButton.Margin = new Padding(0);
            buttons.Controls.AddRange(new Control[] { cancelButton,saveButton }); footerRow.Controls.Add(buttons,1,0); footer.Controls.Add(footerRow,0,0); root.Controls.Add(footer,0,2);
            AcceptButton = saveButton; CancelButton = cancelButton; cancelButton.DialogResult = DialogResult.None;
            defaults.Click += delegate { hotkey.SetBinding(HotkeyBinding.Default); SetError(""); };
            hotkey.BindingChanged += delegate { SetError(Localize(hotkey.Error)); };
            hotkey.GestureReleased += delegate { if (hotkey.Focused && hotkey.Error == null) defaults.Focus(); };
            saveButton.Click += delegate { SaveValues(); }; cancelButton.Click += delegate { RequestClose(); };
            recheckLink.LinkClicked += delegate { if (!setup.Checking && !Running) InvokeAction(recheck); };
            prepareButton.Click += delegate { StartPrepare(); }; cancelSetup.Click += delegate { RequestCancel(false); };
            details.LinkClicked += delegate { expanded = !expanded; ApplySetup(); };
            displaySettings.LinkClicked += delegate { InvokeAction(this.openDisplaySettings); };
            updateCheck.LinkClicked += delegate { if (this.checkUpdates != null && !checkingUpdates && !Running) InvokeAction(this.checkUpdates); };
            releasePage.LinkClicked += delegate { if (this.openRelease != null && !Running) InvokeAction(this.openRelease); };
            automaticUpdates.CheckedChanged += delegate { UpdatePreferenceHint(); };
            playbackTimer = new Timer { Interval = 1000 }; playbackTimer.Tick += delegate { RefreshPlayback(); };
            VisibleChanged += delegate { playbackTimer.Enabled = Visible && this.readPlayback != null; if (playbackTimer.Enabled) RefreshPlayback(); };
            FormClosing += ClosingDialog; Activated += delegate { active = true; };
            Deactivate += delegate { active = false; };
            content.Layout += delegate { FitHeight(); }; viewport.SizeChanged += delegate { FitHeight(); };
            ClientSizeChanged += delegate { if(layoutReady) FitHeight(); };
            Load += delegate {
                // Manual positioning belongs to the caller (including isolated test desktops).
                if (StartPosition == FormStartPosition.CenterScreen) {
                    AdaptToCurrentWorkArea(true);
                }
            };
            Shown += delegate { timeout.Focus(); FitHeight(); };
            UpdatePreferenceHint(); ApplySetup();
            layoutReady = true;
            ApplyWindowDpi((int)GetDpiForWindow(Handle));
            ResumeLayout(true);
        }

        internal void RefreshPlayback()
        {
            using (SettingsDpiContext.EnterUi()) {
            if (IsDisposed || readPlayback == null) return;
            try { playbackPanel.RefreshState(readPlayback()); }
            catch (Exception ex) { playbackPanel.ShowReadError(ex.Message); }
            FitHeight();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && playbackTimer != null) { playbackTimer.Stop(); playbackTimer.Dispose(); }
            base.Dispose(disposing);
            if (disposing) {
                if (ownedApplicationIcon != null) { ownedApplicationIcon.Dispose(); ownedApplicationIcon = null; }
                foreach (Font font in ownedFonts) font.Dispose();
                ownedFonts.Clear();
                fontPoints.Clear();
            }
        }

        private static TableLayoutPanel Table(string name, int columns)
        {
            var table = new TableLayoutPanel { Name = name, ColumnCount = columns, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0), Padding = new Padding(0), TabStop = false };
            if (columns == 1) table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return table;
        }
        private static Label Paragraph(string name, string value)
        {
            return new Label { Name = name, Text = value, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 3, 0, 3) };
        }
        private static Button Button(string name, string value, int tab)
        {
            Button button = SystemInformation.HighContrast ? new Button() : new SurfaceButton();
            button.Name = name; button.Text = value; button.AutoSize = true;
            button.MinimumSize = new Size(96, 39); button.Padding = new Padding(10, 2, 10, 2);
            button.TabIndex = tab; button.Margin = new Padding(8, 0, 0, 0); button.DialogResult = DialogResult.None;
            return button;
        }
        private static Color SecondaryText { get { return SystemInformation.HighContrast ? SystemColors.ControlText : MintTheme.Muted; } }
        private static void StyleLink(LinkLabel link)
        {
            if (SystemInformation.HighContrast) return;
            link.LinkColor = MintTheme.Green;
            link.ActiveLinkColor = Color.FromArgb(0,124,119);
            link.VisitedLinkColor = link.LinkColor;
            link.LinkBehavior = LinkBehavior.HoverUnderline;
        }
        private static void StylePrimaryButton(Button button)
        {
            var surface = button as SurfaceButton;
            if (surface != null) surface.Primary = true;
        }
        private static void AddField(TableLayoutPanel table, int row, string label, Control control)
        {
            table.Controls.Add(new Label { Name = control.Name+"-label", Text = label, AutoSize = true, Margin = new Padding(0, 9, 8, 0), TabIndex = control.TabIndex }, 0, row);
            control.Margin = new Padding(0, 0, 0, 16); table.Controls.Add(control, 1, row);
        }
        internal void ConfigureWindow(Action recheck, Icon applicationIcon = null)
        {
            this.recheck = recheck;
            if (applicationIcon != null) {
                Icon next = (Icon)applicationIcon.Clone();
                Icon = next;
                if (ownedApplicationIcon != null) ownedApplicationIcon.Dispose();
                ownedApplicationIcon = next;
            }
            UpdateActions();
        }
        private bool HasNativeDropDown(Control parent)
        {
            foreach (Control child in parent.Controls) {
                var combo = child as ComboBox; if (combo != null && combo.DroppedDown) return true;
                if (HasNativeDropDown(child)) return true;
            } return false;
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape && !hotkey.Focused && !HasNativeDropDown(this)) { RequestClose(); return true; }
            return base.ProcessCmdKey(ref msg,keyData);
        }
        private Font OwnFont(Font font) { ownedFonts.Add(font); fontPoints.Add(font,font.Size); return font; }
        internal static void RescaleOwnedFonts(Control view, List<Font> owned, Dictionary<Font,float> points, float factor)
        {
            var replacements = new Dictionary<Font,Font>(FontIdentity);
            foreach (Font font in owned) {
                float size = points[font] * factor;
                if (Math.Abs(font.Size-size) > .01F) replacements.Add(font,new Font(font.FontFamily,size,font.Style,font.Unit));
            }
            if (replacements.Count == 0) return;
            var assignments = new Dictionary<Control,Font>();
            CollectFontAssignments(view,replacements,assignments);
            foreach (var pair in assignments) pair.Key.Font = pair.Value;
            for (int i=0;i<owned.Count;i++) {
                Font replacement;
                if (!replacements.TryGetValue(owned[i],out replacement)) continue;
                Font old = owned[i]; float logical = points[old];
                owned[i] = replacement; points.Remove(old); points.Add(replacement,logical); old.Dispose();
            }
        }
        private static void CollectFontAssignments(Control control, Dictionary<Font,Font> replacements, Dictionary<Control,Font> assignments)
        {
            Font next; if (replacements.TryGetValue(control.Font,out next)) assignments.Add(control,next);
            foreach (Control child in control.Controls) CollectFontAssignments(child,replacements,assignments);
        }
        private void ApplyWindowDpi(int dpi)
        {
            if (!layoutReady || IsDisposed || dpi <= 0) return;
            using (SettingsDpiContext.EnterUi()) {
                SuspendLayout();
                bool previousSizing = sizing; sizing = true;
                try {
                    float target = dpi / 96F, ratio = target / layoutScale;
                    if (Math.Abs(ratio-1F) > .001F) Scale(new SizeF(ratio,ratio));
                    // Point fonts are rasterized against the session's system font DPI in
                    // Framework 4.8. Recreate only view-owned fonts for the window DPI.
                    RescaleOwnedFonts(this,ownedFonts,fontPoints,dpi/fontRasterDpi);
                    playbackPanel.ApplyFontDpi(dpi,fontRasterDpi);
                    appliedDpi = dpi; AutoScaleDimensions = new SizeF(dpi,dpi);
                } finally { ResumeLayout(true); sizing = previousSizing; }
                FitHeight();
            }
        }
        // Pure geometry seam: fake work areas do not enumerate or alter displays.
        internal static Rectangle FitToWorkArea(Rectangle desired, Rectangle work, int dpi, Size frame, bool initial)
        {
            float scale = Math.Max(1,dpi)/96F;
            int gap = Math.Min((int)Math.Round(10*scale),Math.Max(0,Math.Min(work.Width,work.Height)/8));
            Rectangle available = Rectangle.Inflate(work,-gap,-gap);
            int maxWidth = Math.Max(1,initial ? Math.Min(available.Width,(int)(work.Width*.9)) : available.Width);
            int maxHeight = Math.Max(1,initial ? Math.Min(available.Height,(int)(work.Height*.8)) : available.Height);
            int width = Math.Min(maxWidth,Math.Max(1,initial ? (int)Math.Round(718*scale)+frame.Width : desired.Width));
            int height = Math.Min(maxHeight,Math.Max(1,initial ? (int)Math.Round(480*scale)+frame.Height : desired.Height));
            int left = initial ? work.Left+(work.Width-width)/2 : Math.Max(available.Left,Math.Min(desired.Left,available.Right-width));
            int top = initial ? work.Top+(work.Height-height)/2 : Math.Max(available.Top,Math.Min(desired.Top,available.Bottom-height));
            return new Rectangle(left,top,width,height);
        }
        private Rectangle WindowWorkArea()
        {
            using (SettingsDpiContext.EnterUi()) {
            var monitor = new MonitorWorkArea { Size = Marshal.SizeOf(typeof(MonitorWorkArea)) };
            if (!GetMonitorInfo(MonitorFromWindow(Handle,2),ref monitor)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return Rectangle.FromLTRB(monitor.Work.Left,monitor.Work.Top,monitor.Work.Right,monitor.Work.Bottom);
            }
        }
        private void AdaptToCurrentWorkArea(bool initial)
        {
            if (!layoutReady || adapting || IsDisposed || Disposing || WindowState != FormWindowState.Normal) return;
            using (SettingsDpiContext.EnterUi()) {
                adapting = true;
                try {
                    Rectangle work = WindowWorkArea();
                    int gap = Math.Max(1,(int)Math.Round(10*appliedDpi/96F));
                    MinimumSize = new Size(Math.Min((int)Math.Round(300*appliedDpi/96F),Math.Max(1,work.Width-gap*2)),
                        Math.Min((int)Math.Round(300*appliedDpi/96F),Math.Max(1,(int)(work.Height*.8))));
                    Rectangle fitted = FitToWorkArea(Bounds,work,appliedDpi,new Size(Width-ClientSize.Width,Height-ClientSize.Height),initial);
                    if (Bounds != fitted) Bounds = fitted;
                    lastWorkArea = work;
                } finally { adapting = false; }
            }
        }
        private void QueueWorkAreaAdaptation()
        {
            if (adaptationQueued || adapting || !Visible || IsDisposed || Disposing || !IsHandleCreated) return;
            adaptationQueued = true;
            try { BeginInvoke(new Action(delegate {
                adaptationQueued = false;
                if (!IsDisposed && !Disposing) AdaptToCurrentWorkArea(false);
            })); } catch (InvalidOperationException) { adaptationQueued = false; }
        }
        protected override void WndProc(ref Message message)
        {
            using (SettingsDpiContext.EnterUi()) {
                if (message.Msg == 0x02E0 && layoutReady) { // WM_DPICHANGED: this view owns scaling, not process-wide WinForms opt-ins.
                    int dpi = (int)(message.WParam.ToInt64() & 0xffff);
                    Rectangle? suggested = null;
                    if (message.LParam != IntPtr.Zero) {
                        var rect = (NativeRect)Marshal.PtrToStructure(message.LParam,typeof(NativeRect));
                        suggested = Rectangle.FromLTRB(rect.Left,rect.Top,rect.Right,rect.Bottom);
                    }
                    ApplyWindowDpi(dpi);
                    if (WindowState == FormWindowState.Normal && suggested.HasValue) Bounds = suggested.Value;
                    AdaptToCurrentWorkArea(false); message.Result = IntPtr.Zero; return;
                }
                base.WndProc(ref message);
                if (!layoutReady) return;
                if (message.Msg == 0x007E || (message.Msg == 0x001A && message.WParam.ToInt64() == 0x002F) || message.Msg == 0x0232)
                    QueueWorkAreaAdaptation(); // display/work-area change, or end of user move/resize.
                else if (message.Msg == 0x0047 && Visible && !adapting && WindowWorkArea() != lastWorkArea)
                    QueueWorkAreaAdaptation(); // WM_WINDOWPOSCHANGED on a different monitor/work area.
            }
        }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left,Top,Right,Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MonitorWorkArea { internal int Size; internal NativeRect Monitor,Work; internal uint Flags; }
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorWorkArea info);
        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        {
            layoutScale *= factor.Width;
            base.ScaleControl(factor,specified);
        }
        private static void DrawRule(Graphics graphics, Control control, bool top)
        {
            using (var pen = new Pen(MintTheme.Line)) graphics.DrawLine(pen,0,top ? 0 : control.Height-1,control.Width-1,top ? 0 : control.Height-1);
        }
        // Kept as a layout entry point for callers; it never changes the window footprint.
        private void FitHeight()
        {
            using (SettingsDpiContext.EnterUi()) {
            if (sizing || IsDisposed || Disposing || content == null || viewport == null || footerRow == null || root.Disposing || root.IsDisposed || viewport.Disposing || viewport.IsDisposed) return;
            sizing = true;
            try {
                // Test the full viewport first. Measuring only the already-scrolled
                // client width can keep a scrollbar alive after a wider row would fit.
                int width = Math.Max(1,wholeWindowScroll ? ClientSize.Width : viewport.Width);
                // Release an obsolete scroll range only when the complete body
                // already fits at full width. Needed ranges keep their position.
                if (!wholeWindowScroll && viewport.VerticalScroll.Visible &&
                    content.GetPreferredSize(new Size(width,0)).Height <= viewport.Height) {
                    viewport.AutoScrollMinSize = Size.Empty;
                    viewport.AutoScroll = false;
                    viewport.AutoScroll = true;
                }
                bool narrow = width < Units(600);
                if ((configuration.ColumnCount == 1) != narrow) {
                    configuration.SuspendLayout(); configuration.Controls.Remove(basics); configuration.Controls.Remove(playbackPanel);
                    configuration.ColumnCount = narrow ? 1 : 2; configuration.RowCount = narrow ? 2 : 1;
                    configuration.ColumnStyles.Clear(); configuration.RowStyles.Clear();
                    configuration.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,narrow ? 100 : 49.14F));
                    if (!narrow) configuration.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50.86F));
                    basics.Margin = new Padding(0,0,narrow ? 0 : Units(18),narrow ? Units(34) : 0);
                    playbackPanel.Margin = new Padding(narrow ? 0 : Units(18),0,0,0);
                    configuration.Controls.Add(basics,0,0); configuration.Controls.Add(playbackPanel,narrow ? 0 : 1,narrow ? 1 : 0);
                    configuration.ResumeLayout(true);
                }
                content.Padding = new Padding(Units(narrow ? 14 : 18),Units(16),Units(narrow ? 14 : 18),Units(16));
                viewport.SuspendLayout();
                content.MaximumSize = new Size(width,0); content.MinimumSize = new Size(width,0); content.Width = width;
                content.PerformLayout();
                int basicWidth = Math.Max(1,basics.Width);
                var fields = (TableLayoutPanel)basics.Controls["basic-fields"];
                bool stackedFields = narrow && text.English;
                var shortcutLabel = fields.Controls["hotkey-frame-label"]; var languageLabel = fields.Controls["language-label"];
                var shortcutFrame = hotkey.Parent;
                if ((fields.ColumnCount == 2) != stackedFields) {
                    fields.SuspendLayout(); fields.Controls.Clear(); fields.ColumnStyles.Clear(); fields.RowStyles.Clear();
                    fields.ColumnCount = stackedFields ? 2 : 3; fields.RowCount = stackedFields ? 4 : 2;
                    fields.ColumnStyles.Add(new ColumnStyle(stackedFields ? SizeType.Percent : SizeType.Absolute,stackedFields ? 100 : Units(text.English ? 82 : 80)));
                    fields.ColumnStyles.Add(new ColumnStyle(stackedFields ? SizeType.AutoSize : SizeType.Percent,stackedFields ? 0 : 100));
                    if(!stackedFields) fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                    fields.Controls.Add(shortcutLabel,0,0); fields.Controls.Add(shortcutFrame,stackedFields ? 0 : 1,stackedFields ? 1 : 0); fields.Controls.Add(defaults,stackedFields ? 1 : 2,stackedFields ? 1 : 0);
                    fields.Controls.Add(languageLabel,0,stackedFields ? 2 : 1); fields.Controls.Add(language,stackedFields ? 0 : 1,stackedFields ? 3 : 1);
                    fields.SetColumnSpan(shortcutLabel,stackedFields ? 2 : 1); fields.SetColumnSpan(languageLabel,stackedFields ? 2 : 1); fields.SetColumnSpan(language,2);
                    shortcutLabel.Margin = languageLabel.Margin = stackedFields ? new Padding(0,0,0,Units(6)) : new Padding(0,Units(9),Units(8),0);
                    fields.ResumeLayout(true);
                }
                if(!stackedFields) fields.ColumnStyles[0].Width = Units(narrow && basicWidth < Units(310) ? (text.English ? 82 : 66) : (text.English ? 82 : 80));
                var idleLine = (TableLayoutPanel)basics.Controls["timeout-line"]; var idleLabel = idleLine.Controls["timeout-label"]; var idleRow = timeout.Parent.Parent;
                if ((idleLine.ColumnCount == 1) != stackedFields) {
                    idleLine.SuspendLayout(); idleLine.Controls.Remove(idleLabel); idleLine.Controls.Remove(idleRow); idleLine.ColumnStyles.Clear(); idleLine.RowStyles.Clear();
                    idleLine.ColumnCount = stackedFields ? 1 : 2; idleLine.RowCount = stackedFields ? 2 : 1; idleLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
                    if(!stackedFields) idleLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                    idleLine.Controls.Add(idleLabel,0,0); idleLine.Controls.Add(idleRow,stackedFields ? 0 : 1,stackedFields ? 1 : 0);
                    idleLabel.Margin = stackedFields ? new Padding(0,0,0,Units(8)) : new Padding(0,Units(9),0,0); idleRow.Anchor = AnchorStyles.Right; idleLine.ResumeLayout(true);
                }
                defaults.MinimumSize = new Size(Units(narrow && basicWidth < Units(310) ? (text.English ? 60 : 83) : 95),Units(39));
                defaults.Margin = new Padding(Units(narrow && basicWidth < Units(310) ? 7 : 10),0,0,Units(16));
                timeout.Parent.Width = Units(narrow && basicWidth < Units(310) ? 132 : 158);
                playbackPanel.AdaptWidth();
                automaticUpdates.MaximumSize = startup.MaximumSize = new Size(basicWidth,0);
                try {
                    int body = content.GetPreferredSize(new Size(width,0)).Height;
                    if(body > viewport.Height) {
                        width = Math.Max(1,width-SystemInformation.VerticalScrollBarWidth);
                        content.MinimumSize = Size.Empty; content.MaximumSize = new Size(width,0); content.Width = width; content.PerformLayout();
                        playbackPanel.AdaptWidth(); body = content.GetPreferredSize(new Size(width,0)).Height;
                    }
                    content.MinimumSize = Size.Empty;
                    Size scroll = wholeWindowScroll ? Size.Empty : new Size(0,body);
                    if(viewport.AutoScrollMinSize != scroll) viewport.AutoScrollMinSize = scroll;
                } finally { viewport.ResumeLayout(true); }
                header.Height = Units(narrow ? 90 : 64);
                int titleWidth = heading.GetPreferredSize(Size.Empty).Width;
                heading.SetBounds(Units(narrow ? 14 : 18),Units(narrow ? 16 : 12),titleWidth,Units(40));
                int stateLeft = heading.Right+Units(narrow ? 10 : 24);
                int catWidth = Units(narrow ? 100 : 162), catHeight = Units(narrow ? 52 : 60);
                cat.SetBounds(header.Width-catWidth-Units(narrow ? 3 : 18),header.Height-catHeight,catWidth,catHeight);
                int stateWidth = Math.Max(1,header.Width-stateLeft-Units(narrow ? 14 : 188));
                stateLine.SetBounds(stateLeft,Units(narrow ? 21 : 20),stateWidth,Units(28));
                int copyWidth = text.English ? Units(350) : Units(360);
                bool narrowFooter = footer.ClientSize.Width-footer.Padding.Horizontal < copyWidth+buttons.PreferredSize.Width+Units(12);
                if ((footerRow.ColumnCount == 1) != narrowFooter) {
                    var copy = footerRow.Controls["footer-copy"];
                    footerRow.SuspendLayout(); footerRow.Controls.Remove(copy); footerRow.Controls.Remove(buttons);
                    footerRow.ColumnCount = narrowFooter ? 1 : 2; footerRow.RowCount = narrowFooter ? 2 : 1;
                    footerRow.ColumnStyles.Clear(); footerRow.RowStyles.Clear(); footerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
                    if (!narrowFooter) footerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                    copy.Margin = new Padding(0,0,narrowFooter ? 0 : Units(12),narrowFooter ? Units(14) : 0);
                    footerRow.Controls.Add(copy,0,0); footerRow.Controls.Add(buttons,narrowFooter ? 0 : 1,narrowFooter ? 1 : 0);
                    buttons.Anchor = AnchorStyles.Right; footerRow.ResumeLayout(true);
                }
                // AutoSize can retain the footer's former multi-line measurement
                // after columns stack. Reserve its current preferred height explicitly.
                int footerHeight = footer.GetPreferredSize(new Size(root.ClientSize.Width,0)).Height;
                if(root.RowStyles[2].SizeType != SizeType.Absolute) root.RowStyles[2].SizeType = SizeType.Absolute;
                if(Math.Abs(root.RowStyles[2].Height-footerHeight) > .1F) root.RowStyles[2].Height = footerHeight;
                // In a very short work area, scroll the complete page instead of
                // letting fixed chrome consume the body. Normal C stays unchanged.
                bool scrollWindow = ClientSize.Height < header.Height+footerHeight+Units(120);
                if(scrollWindow != wholeWindowScroll) {
                    wholeWindowScroll = scrollWindow;
                    AutoScrollPosition = Point.Empty; AutoScrollMinSize = Size.Empty;
                    viewport.AutoScrollMinSize = Size.Empty; viewport.AutoScroll = !scrollWindow;
                    AutoScroll = scrollWindow; root.Dock = scrollWindow ? DockStyle.Top : DockStyle.Fill;
                }
                if(wholeWindowScroll) {
                    if(root.Width != ClientSize.Width) root.Width = ClientSize.Width;
                    int fullHeight = header.Height+content.GetPreferredSize(new Size(Math.Max(1,viewport.Width),0)).Height+footerHeight;
                    if(root.Height != fullHeight) root.Height = fullHeight;
                    Size page = new Size(0,fullHeight);
                    if(AutoScrollMinSize != page) AutoScrollMinSize = page;
                }
            } finally { sizing = false; }
            }
        }
        private int Units(int logical) { return Math.Max(1,(int)Math.Round(logical*layoutScale)); }
        private string Localize(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            string[] parts = value.Split(new[] { " / " }, StringSplitOptions.None);
            return parts.Length > 1 ? (text.English ? parts[parts.Length - 1] : parts[0]) : value;
        }
        private void SetError(string value)
        {
            error.Text = Localize(value); error.Visible = !String.IsNullOrEmpty(error.Text); FitHeight();
            if (Visible && error.Visible) {
                if (wholeWindowScroll) ScrollControlIntoView(error);
                else viewport.ScrollControlIntoView(error);
            }
        }
        private bool Running { get { return prepareRequested || setup.Preparing; } }

        private void SaveValues()
        {
            if (Running || saving || closeWhenSafe) return;
            if (hotkey.HasHeldKeys) { SetError(text.Pick("请先松开组合键，再保存。", "Release the shortcut keys before saving.")); return; }
            if (!String.IsNullOrEmpty(hotkey.Error)) { SetError(hotkey.Error); return; }
            int minutes;
            if (!Int32.TryParse(timeout.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out minutes) || minutes < 1 || minutes > 1440) {
                SetError(text.Pick("请输入 1–1440 分钟。", "Enter 1–1440 minutes.")); return;
            }
            int playbackMinutes; string playbackError;
            if (!playbackPanel.TryReadLimit(out playbackMinutes, out playbackError)) { SetError(playbackError); return; }
            var value = new SettingsValues { TimeoutMinutes = minutes, Hotkey = hotkey.Binding,
                Language = language.SelectedIndex == 1 ? "en" : "zh-CN", Startup = startup.Checked,
                AutomaticUpdateChecks = automaticUpdates.Checked,
                PlaybackLimitEnabled = playbackPanel.LimitEnabled, PlaybackLimitMinutes = playbackMinutes };
            saving = true; saveButton.Enabled = false;
            try {
                string reason = save(value);
                if (!String.IsNullOrEmpty(reason)) { SetError(reason); return; }
                DialogResult = DialogResult.OK; Close();
            } catch (Exception ex) { SetError(ex.Message); }
            finally { saving = false; if (!IsDisposed) saveButton.Enabled = !Running && !closeWhenSafe; }
        }

        private void StartPrepare()
        {
            if (Running || setup.Checking || !setup.CanPrepare || closeWhenSafe) return;
            prepareRequested = true; cancelRequested = false; SetError(""); ApplySetup(); InvokeAction(prepare);
            // Only RefreshSetup can acknowledge completion, even after exceptions.
        }
        private void InvokeAction(Action action)
        {
            try { if (action != null) action(); } catch (Exception ex) { SetError(ex.Message); }
        }
        private void RequestCancel(bool close)
        {
            closeWhenSafe |= close;
            if (!Running) { if (closeWhenSafe) CloseCancelled(); return; }
            if (cancelRequested) return;
            cancelRequested = true; ApplySetup(); InvokeAction(cancelPrepare);
        }
        private void RequestClose()
        {
            if (Running) RequestCancel(true); else CloseCancelled();
        }
        private void CloseCancelled()
        {
            if (handlingClose || Running || IsDisposed) return;
            DialogResult = DialogResult.Cancel; Close();
        }
        private void ClosingDialog(object sender, FormClosingEventArgs e)
        {
            if (!Running) { if (DialogResult != DialogResult.OK) DialogResult = DialogResult.Cancel; return; }
            e.Cancel = true; DialogResult = DialogResult.None; handlingClose = true;
            try { RequestCancel(true); } finally { handlingClose = false; }
            // The callback may synchronously publish its final state.
            if (!Running && closeWhenSafe && IsHandleCreated) BeginInvoke(new Action(CloseCancelled));
        }

        internal void RefreshSetup(SetupPanelState state)
        {
            using (SettingsDpiContext.EnterUi()) {
            if (state == null) throw new ArgumentNullException("state");
            if (IsDisposed) return;
            setup = new SetupPanelState { Visible = state.Visible, Checking = state.Checking, Preparing = state.Preparing,
                CanPrepare = state.CanPrepare, Ready = state.Ready, Detail = state.Detail, Diagnostics = state.Diagnostics };
            prepareRequested = false;
            if (!Running) cancelRequested = false;
            ApplySetup();
            if (closeWhenSafe && !Running) CloseCancelled();
            }
        }

        private void UpdatePreferenceHint()
        {
            updateHint.Text = automaticUpdates.Checked
                ? text.Pick("启动时检查；安装前由你确认。", "Check on launch. You choose when to install.")
                : text.Pick("关闭后仍可手动检查更新。", "You can still check manually when this is off.");
            FitHeight();
        }
        internal void RefreshUpdate(bool checking, string detail, bool available, bool failed)
        {
            using (SettingsDpiContext.EnterUi()) {
            if (IsDisposed) return;
            checkingUpdates = checking;
            updateCheck.Text = checking ? text.Pick("正在检查…", "Checking…") :
                failed ? text.Pick("重新检查", "Check again") : text.Pick("检查更新", "Check for updates");
            updateDetail.Text = detail ?? "";
            updateDetail.ForeColor = failed && !SystemInformation.HighContrast ? Color.FromArgb(166, 38, 24) : SecondaryText;
            updateSection.Visible = !String.IsNullOrEmpty(updateDetail.Text);
            releasePage.Visible = available;
            UpdateActions(); FitHeight();
            }
        }
        private void UpdateActions()
        {
            bool busy = Running || closeWhenSafe || saving;
            recheckLink.Enabled = recheck != null && !setup.Checking && !busy;
            updateCheck.Enabled = checkUpdates != null && !checkingUpdates && !Running && !closeWhenSafe;
            releasePage.Enabled = openRelease != null && !Running && !closeWhenSafe;
        }

        // UI-thread delivery only. The coordinator queues non-injected KeyDown
        // snapshots from its existing listener, subscribed only while this is open.
        internal void ObservePhysicalKey(int key, uint modifiers)
        { ObservePhysicalKey(key, modifiers, false); }

        internal void ObservePhysicalKey(int key, uint modifiers, bool up)
        {
            if (IsDisposed || !Visible || !active || Form.ActiveForm != this || !hotkey.Focused ||
                !hotkey.Enabled || Running || closeWhenSafe) return;
            if (up) hotkey.RecordKey((Keys)key, true);
            else hotkey.RecordPhysicalKey(key, modifiers);
        }
        private void ApplySetup()
        {
            bool running = Running;
            headerStatus.Text = running ? text.Pick("正在准备…", "Preparing…") : setup.Checking ? text.Pick("正在检查…", "Checking…") : setup.Ready ? text.Pick("显示已就绪", "Display ready") : text.Pick("关屏尚未启用", "Display off not enabled");
            displayMark.Ready = setup.Ready && !running && !setup.Checking; displayMark.Invalidate();
            recheckLink.Visible = !setup.Ready || running || setup.Checking;
            recheckLink.Text = setup.Ready ? text.Pick("检查", "Check") : text.Pick("重新检查", "Recheck");
            configuration.Enabled = !running && !closeWhenSafe;
            saveButton.Enabled = !running && !closeWhenSafe && !saving;
            cancelButton.Enabled = !closeWhenSafe;
            cancelButton.Text = closeWhenSafe ? text.Pick("等待取消…", "Waiting…") : text.Pick("取消", "Cancel");
            setupSection.Visible = setup.Visible && !(setup.Ready && !running);
            setupDetail.Text = setup.Detail ?? "";
            if (prepareRequested) setupDetail.Text = text.Pick("正在准备，请稍候…", "Preparing. Please wait…");
            if (cancelRequested) setupDetail.Text += "\r\n" + text.Pick("已请求取消，等待处理结果…", "Cancellation requested; waiting for the result…");
            prepareButton.Text = running ? text.Pick("正在准备…", "Preparing…") : setup.Checking
                ? text.Pick("正在检查…", "Checking…") : text.Pick("一键准备", "Prepare");
            prepareButton.Enabled = !running && !setup.Checking && setup.CanPrepare && !closeWhenSafe;
            cancelSetup.Visible = running; cancelSetup.Enabled = running && !cancelRequested;
            details.Text = expanded ? text.Pick("收起诊断", "Hide diagnostics") : text.Pick("诊断详情", "Diagnostic details");
            setupDetail.AccessibleDescription = setupDetail.Text;
            diagnostics.Text = setupDetail.Text + (String.IsNullOrEmpty(setup.Diagnostics) ? "" : "\r\n\r\n" + setup.Diagnostics); diagnostics.Visible = displaySettings.Visible = expanded;
            displaySettings.Enabled = !running && !setup.Checking && !closeWhenSafe;
            UpdateActions();
            FitHeight();
        }
    }

    // Presentation-only wrappers retain the native controls and their keyboard semantics.
    internal sealed class InputFrame : Panel
    {
        private readonly Control input;
        internal InputFrame(Control input)
        {
            this.input = input; Name = input.Name + "-frame"; TabIndex = input.TabIndex;
            TabStop = false; Height = 39; Width = input.Width + 20; Padding = new Padding(10, 4, 10, 4);
            BackColor = SystemInformation.HighContrast ? SystemColors.Window : Color.White;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            var box = input as TextBox; if (box != null) box.BorderStyle = BorderStyle.None;
            var number = input as NumericUpDown; if (number != null) number.BorderStyle = BorderStyle.None;
            input.Dock = DockStyle.None; input.TabIndex = 0; input.Margin = new Padding(0);
            input.Enter += delegate { Invalidate(); }; input.Leave += delegate { Invalidate(); };
            Controls.Add(input);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && Enabled && input.Enabled) input.Focus();
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (input == null) return;
            int h = input.PreferredSize.Height;
            input.SetBounds(Padding.Left, Math.Max(1, (ClientSize.Height - h) / 2),
                Math.Max(1, ClientSize.Width - Padding.Horizontal), h);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Color border = SystemInformation.HighContrast ? SystemColors.WindowText : ContainsFocus
                ? MintTheme.Green : MintTheme.Field;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = SurfaceButton.Outline(ClientRectangle, Math.Max(4, Font.Height / 3)))
            using (var pen = new Pen(border, ContainsFocus ? 2F : 1F)) e.Graphics.DrawPath(pen, path);
        }
    }

    internal sealed class SurfaceButton : Button
    {
        internal bool Primary;
        internal int Symbol; // 1 power, 2 pause, 3 resume; presentation only.
        private bool hovered, pressed;
        internal SurfaceButton()
        {
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; base.OnMouseLeave(e); Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = true; base.OnMouseDown(e); Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; base.OnMouseUp(e); Invalidate(); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) pressed = true; base.OnKeyDown(e); Invalidate(); }
        protected override void OnKeyUp(KeyEventArgs e) { pressed = false; base.OnKeyUp(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { pressed = false; base.OnLostFocus(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { pressed = false; base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (SystemInformation.HighContrast) { base.OnPaint(e); return; }
            e.Graphics.Clear(Parent == null ? Color.White : Parent.BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color background = !Enabled ? Color.FromArgb(227,235,237) : Primary
                ? (pressed ? Color.FromArgb(0,124,119) : hovered ? Color.FromArgb(0,124,119) : MintTheme.Green)
                : (pressed ? Color.FromArgb(232,244,239) : hovered ? Color.FromArgb(232,243,242) : MintTheme.Soft);
            Color foreground = !Enabled ? Color.FromArgb(109,133,144) : Primary ? Color.White : Color.FromArgb(51,88,105);
            using (var path = Outline(ClientRectangle, Math.Max(5, Font.Height / 3))) {
                using (var brush = new SolidBrush(background)) e.Graphics.FillPath(brush, path);
                if (!Primary) using (var pen = new Pen(MintTheme.Field)) e.Graphics.DrawPath(pen, path);
            }
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
            Rectangle textBounds = ClientRectangle;
            if (Symbol != 0) {
                int size = Math.Max(12,Font.Height-2), gap = Math.Max(6,Font.Height/3);
                int group = TextRenderer.MeasureText(Text,Font).Width + size + gap;
                int left = Math.Max(6,(Width-group)/2), top = (Height-size)/2;
                using (var pen = new Pen(foreground,Math.Max(1,Font.Height/13F))) {
                    if (Symbol == 1) { e.Graphics.DrawArc(pen,left+1,top+1,size-2,size-2,-55,290); e.Graphics.DrawLine(pen,left+size/2,top,left+size/2,top+size/2); }
                    else if (Symbol == 2) { e.Graphics.DrawRectangle(pen,left+2,top+1,size/4,size-2); e.Graphics.DrawRectangle(pen,left+size*2/3,top+1,size/4,size-2); }
                    else { e.Graphics.DrawPolygon(pen,new[] { new Point(left+2,top+1),new Point(left+size-1,top+size/2),new Point(left+2,top+size-1) }); }
                }
                textBounds = new Rectangle(left+size+gap,0,Width-left-size-gap,Height); flags &= ~TextFormatFlags.HorizontalCenter; flags |= TextFormatFlags.Left;
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, foreground, flags);
            if (Focused && ShowFocusCues) {
                Rectangle focus = Rectangle.Inflate(ClientRectangle, -5, -5);
                ControlPaint.DrawFocusRectangle(e.Graphics, focus, foreground, background);
            }
        }
        internal static GraphicsPath Outline(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            Rectangle bounds = Rectangle.Inflate(rect, -1, -1);
            int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            if (diameter < 2) { path.AddRectangle(bounds); return path; }
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure(); return path;
        }
    }

    // Window-local messages only: no hooks, registration, parsing, or replay.
    // On-demand physical key-state checks verify release even if the existing
    // application listener consumed a KeyDown before it reached this UI queue.
    // No timer, global hook, hotkey registration, or display/device access.
    internal sealed class HotkeyCaptureBox : TextBox
    {
        private readonly HashSet<Keys> held = new HashSet<Keys>();
        private bool captured;
        internal HotkeyBinding Binding { get; private set; }
        internal string Error { get; private set; }
        internal event EventHandler BindingChanged, GestureReleased;
        internal Func<Keys, bool> KeyIsDown = QueryKey;
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        private static bool QueryKey(Keys key) { return (GetAsyncKeyState((int)key) & 0x8000) != 0; }
        internal HotkeyCaptureBox()
        {
            ReadOnly = true; ShortcutsEnabled = false; ImeMode = ImeMode.Disable; BackColor = SystemColors.Window;
        }
        internal void SetBinding(HotkeyBinding binding)
        {
            Binding = binding; Error = null; Text = binding.DisplayText; captured = false;
        }
        internal bool HasHeldKeys
        {
            get {
                foreach (Keys key in held) if (KeyIsDown(key)) return true;
                foreach (Keys key in new[] { Keys.ControlKey, Keys.Menu, Keys.ShiftKey, Keys.LWin, Keys.RWin }) if (KeyIsDown(key)) return true;
                return false;
            }
        }
        private static bool Modifier(Keys key)
        {
            return key == Keys.ControlKey || key == Keys.LControlKey || key == Keys.RControlKey || key == Keys.Menu ||
                key == Keys.LMenu || key == Keys.RMenu || key == Keys.ShiftKey || key == Keys.LShiftKey || key == Keys.RShiftKey ||
                key == Keys.LWin || key == Keys.RWin;
        }
        private uint ModifiersFor(Keys modifiers)
        {
            return ((modifiers & Keys.Control) != 0 ? 2U : 0U) | ((modifiers & Keys.Alt) != 0 ? 1U : 0U) |
                ((modifiers & Keys.Shift) != 0 ? 4U : 0U) | (KeyIsDown(Keys.LWin) || KeyIsDown(Keys.RWin) ? 8U : 0U);
        }
        internal void RecordKey(Keys keyData, bool up)
        {
            Keys key = keyData & Keys.KeyCode;
            if (up) {
                held.Remove(key);
                if (captured && !HasHeldKeys) {
                    captured = false;
                    if (GestureReleased != null) GestureReleased(this, EventArgs.Empty);
                }
                return;
            }
            held.Add(key);
            if (Modifier(key)) return;
            ApplyCandidate((int)key, ModifiersFor(keyData));
        }
        internal void RecordPhysicalKey(int key, uint modifiers)
        {
            if (Modifier((Keys)key)) return;
            held.Add((Keys)key);
            ApplyCandidate(key, modifiers);
        }
        private void ApplyCandidate(int key, uint modifiers)
        {
            captured = true;
            HotkeyBinding candidate; string reason;
            bool changed;
            if (HotkeyBinding.TryCreate(modifiers, key, out candidate, out reason)) {
                changed = Error != null || !candidate.SameAs(Binding);
                Binding = candidate; Error = null;
                if (changed) Text = candidate.DisplayText;
            } else { changed = Error != reason; Error = reason; }
            if (changed && BindingChanged != null) BindingChanged(this, EventArgs.Empty);
        }
        public override bool PreProcessMessage(ref Message msg)
        {
            if (Focused && Enabled && (msg.Msg == 0x100 || msg.Msg == 0x104 || msg.Msg == 0x101 || msg.Msg == 0x105)) {
                RecordKey((Keys)msg.WParam.ToInt32() | Control.ModifierKeys, msg.Msg == 0x101 || msg.Msg == 0x105);
                return true;
            }
            if (Focused && (msg.Msg == 0x102 || msg.Msg == 0x106 || msg.Msg == 0x103 || msg.Msg == 0x107)) return true;
            return base.PreProcessMessage(ref msg);
        }
        protected override void OnEnter(EventArgs e)
        {
            held.RemoveWhere(delegate(Keys key) { return !KeyIsDown(key); });
            base.OnEnter(e); SelectAll();
        }
    }
}
