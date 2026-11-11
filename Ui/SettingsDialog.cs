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
        private readonly TableLayoutPanel root;
        private readonly MintCardLayoutPanel footer;
        private readonly MintGradientPanel header;
        private readonly Label heading, subheading;
        private readonly TableLayoutPanel stateLine;
        private readonly MintCardLayoutPanel basics;
        private readonly Label headerStatus;
        private readonly LinkLabel recheckLink;
        private readonly StaticCatControl cat;
        private readonly MintArtControl headerScene;
        private Icon ownedApplicationIcon;
        private Action recheck;
        private readonly MintStatusMark displayMark;
        private readonly MintCardLayoutPanel footerRow;
        private readonly FlowLayoutPanel versionRow, buttons;
        // Only fonts constructed by this view belong here; inherited/system fonts are borrowed.
        private readonly List<Font> ownedFonts = new List<Font>();
        private readonly List<TableLayoutPanel> buildingTables = new List<TableLayoutPanel>();
        internal static readonly IEqualityComparer<Font> FontIdentity = new FontReferenceComparer();
        private sealed class FontReferenceComparer : IEqualityComparer<Font>
        {
            public bool Equals(Font first,Font second) { return Object.ReferenceEquals(first,second); }
            public int GetHashCode(Font font) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(font); }
        }
        private readonly Dictionary<Font,float> fontPoints = new Dictionary<Font,float>(FontIdentity);
        private readonly float fontRasterDpi;
        private UiText text;
        private bool changingLanguage, updateFailed;
        private string rawError, rawUpdateDetail;
        internal event EventHandler LanguagePreviewChanged;
        internal bool English { get { return text.English; } }
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
        private bool layoutReady, adapting, wholeWindowScroll;
        private int presentationDepth;
        private bool setupShown, setupExpanded, setupRunning, releaseShown;
        private Rectangle lastWorkArea;
        [Flags] private enum LayoutReason { None = 0, Width = 1, Height = 2, Content = 4, Dpi = 8, WorkArea = 16 }
        private LayoutReason pendingLayout;
        private bool layoutQueued, interactiveSizing, finalLayoutPending, finishingLayout, layoutStopped;
        private bool interactiveRootSuspended;
        private int layoutGeneration, measuredWidth = -1, measuredBody, measuredFooter, errorVersion, scrollErrorVersion;
        private string measuredBodyKey;
        private Size arrangedClientSize, arrangedViewportSize, moveStartSize;

        internal SettingsDialog(UiText initialText, SettingsValues current, Func<SettingsValues, string> save,
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
            this.text = initialText; this.save = save; this.prepare = prepare;
            this.cancelPrepare = cancelPrepare; this.openDisplaySettings = openDisplaySettings;
            this.checkUpdates = checkUpdates; this.openRelease = openRelease;
            this.readPlayback = readPlayback;
            Text = text.Pick("ScreenOffTray · 设置", "ScreenOffTray · Settings");
            Font = OwnFont(new Font(text.English ? "Segoe UI" : "Microsoft YaHei UI",15.5F));
            BackColor = MintTheme.Page; ForeColor = MintTheme.Ink;
            AutoScaleDimensions = new SizeF(96, 96);
            FormBorderStyle = FormBorderStyle.Sizable; ShowInTaskbar = true;
            MinimizeBox = MaximizeBox = true;
            StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(984, text.English ? 720 : 654);
            MinimumSize = new Size(300, 300);
            AccessibleDescription = text.Pick("编辑期间暂缓自动关屏；关闭设置后继续在托盘运行。", "Auto off is on hold while editing. Closing settings keeps the app in the tray.");
            root = Table("settings-window", 1); root.AutoSize = false; root.Dock = DockStyle.Fill; root.BackColor = MintTheme.Page;
            root.RowCount = 3; root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);
            header = new MintGradientPanel { Name = "header", Dock = DockStyle.Top, Height = 116, Margin = new Padding(0), BackColor = MintTheme.Header };
            // Panel does not repaint its old bottom rule when its height changes.
            // Repaint this header and transparent children only on actual resize.
            header.SizeChanged += delegate { header.Invalidate(true); };
            heading = Paragraph("heading", "ScreenOffTray"); heading.Dock = DockStyle.None;
            heading.Font = OwnFont(new Font(Font.FontFamily, 24F, FontStyle.Bold)); heading.Margin = new Padding(0); heading.BackColor = Color.Transparent;
            subheading = Paragraph("subheading", text.Pick("设置", "Settings")); subheading.Dock = DockStyle.None;
            subheading.Font = OwnFont(new Font(Font.FontFamily,16F)); subheading.ForeColor = MintTheme.Muted; subheading.Margin = new Padding(0); subheading.BackColor = Color.Transparent;
            // Hidden controls keep the borrowed title fonts inside the DPI
            // rescaling walk; the gradient header paints the visible text.
            heading.Visible = false; subheading.Visible = false;
            header.Controls.Add(heading); header.Controls.Add(subheading);
            header.Title = heading.Text; header.Subtitle = subheading.Text;
            header.TitleFont = heading.Font; header.SubtitleFont = subheading.Font; header.SubtitleColor = subheading.ForeColor;
            stateLine = Table("display-state-line", 3); stateLine.Dock = DockStyle.None;
            stateLine.BackColor = Color.Transparent;
            stateLine.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38)); stateLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); stateLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            displayMark = new MintStatusMark { Name = "display-mark", Size = new Size(32,32), Margin = new Padding(0), TabStop = false };
            headerStatus = Paragraph("display-status", ""); headerStatus.ForeColor = MintTheme.Ink; headerStatus.Margin = new Padding(0,2,0,0); headerStatus.BackColor = Color.Transparent;
            recheckLink = new LinkLabel { Name = "recheck", AutoSize = true, Text = text.Pick("检查", "Check"), Margin = new Padding(8,2,0,0) }; StyleLink(recheckLink);
            recheckLink.BackColor = Color.Transparent;
            stateLine.Controls.Add(displayMark,0,0); stateLine.Controls.Add(headerStatus,1,0); stateLine.Controls.Add(recheckLink,2,0); header.Controls.Add(stateLine);
            cat = new StaticCatControl { English = text.English, Name = "mint-cat", Size = new Size(76,76), Margin = new Padding(0) };
            cat.Visible = !SystemInformation.HighContrast; header.Controls.Add(cat);
            headerScene = new MintArtControl("ScreenOffTray.Ui.Resources.theme.header-scene.png") { Name="header-scene", Margin=new Padding(0), TabStop=false };
            headerScene.Visible = !SystemInformation.HighContrast; header.Controls.Add(headerScene);
            root.Controls.Add(header,0,0);
            viewport = new Panel { Name = "viewport", Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0), TabStop = false, BackColor = MintTheme.Page };
            content = Table("content", 1); content.AutoSize = false; content.Dock = DockStyle.Top; content.Padding = new Padding(14,14,14,14); content.BackColor = MintTheme.Page;
            viewport.Controls.Add(content); root.Controls.Add(viewport, 0, 1);
            configuration = Table("configuration",2); configuration.Dock = DockStyle.Top; configuration.TabIndex = 1; configuration.BackColor = MintTheme.Page;
            configuration.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,47.2F)); configuration.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,52.8F));
            basics = new MintCardLayoutPanel(1) { Name="basic-settings", AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink, Dock=DockStyle.Fill, Margin=new Padding(0,0,6,0), Padding=new Padding(24) };
            basics.SuspendLayout(); buildingTables.Add(basics); basics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            basics.ArtResource = "ScreenOffTray.Ui.Resources.theme.card-left-scene.png";
            var basicTitle = new MintIconLabel { Name="basic-title", Text=text.Pick("基础设置", "Basic settings"), IconKind=MintIconKind.Paw, AutoSize=true, Margin=new Padding(0,0,0,20) };
            basicTitle.Font = OwnFont(new Font(Font.FontFamily,18F,FontStyle.Bold)); basics.Controls.Add(basicTitle,0,0);
            var timeoutLine = Table("timeout-line",2); timeoutLine.Dock = DockStyle.Top; timeoutLine.Padding = new Padding(0,0,0,11); timeoutLine.Margin = new Padding(0,0,0,12);
            timeoutLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); timeoutLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var idleLabel = new MintIconLabel { Name="timeout-label", Text=text.Pick("空闲关屏", "Idle time"), IconKind=MintIconKind.Display, AutoSize=true, Margin=new Padding(0,5,0,0) };
            timeoutLine.Controls.Add(idleLabel,0,0);
            timeout = new MintNumberBox { Font = OwnFont(new Font(Font.FontFamily,20F)), Name = "timeout", Value = Math.Max(1,Math.Min(1440,current.TimeoutMinutes)), AccessibleName = text.Pick("空闲关屏分钟数，1至1440", "Idle timeout in minutes, 1 to 1440") };
            var timeoutRow = new FlowLayoutPanel { Name = "timeout-row", BackColor=Color.Transparent, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            timeoutRow.Controls.Add(new MintStepper(timeout)); timeoutRow.Controls.Add(new MintLabel { Name="timeout-unit", Text = text.Pick("分钟", "min"), AutoSize = true, Margin = new Padding(8,9,0,0) });
            timeoutLine.Controls.Add(timeoutRow,1,0); basics.Controls.Add(timeoutLine,0,1);
            // The reference has open spacing between settings, without separator rules.
            var fields = Table("basic-fields",3); fields.Dock = DockStyle.Top;
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,78)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            hotkey = new HotkeyCaptureBox { Font = OwnFont(new Font(Font.FontFamily,14F)), Name = "hotkey", Dock = DockStyle.Fill, AccessibleName = text.Pick("关屏快捷键，按组合键录入", "Display-off shortcut, press a key combination") };
            hotkey.SetBinding(current.Hotkey ?? HotkeyBinding.Default);
            AddField(fields,0,text.Pick("快捷键", "Shortcut"), new InputFrame(hotkey) { Dock = DockStyle.Fill },MintIconKind.Keyboard);
            defaults = Button("defaults",text.Pick("恢复默认", "Reset"),1); defaults.Font = OwnFont(new Font(Font.FontFamily,14F)); defaults.MinimumSize = new Size(92,39); defaults.Padding = new Padding(4,2,4,2); defaults.Margin = new Padding(8,0,0,16); fields.Controls.Add(defaults,2,0);
            language = new MintComboBox { Font = OwnFont(new Font(Font.FontFamily,16F)), Name = "language", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Standard, TabIndex = 2, AccessibleName = text.Pick("界面语言", "Language") };
            language.Items.AddRange(new object[] { "简体中文", "English" }); language.SelectedIndex = current.Language == "en" ? 1 : 0;
            AddField(fields,1,text.Pick("语言", "Language"),language,MintIconKind.Language); fields.SetColumnSpan(language,2); basics.Controls.Add(fields,0,2);
            startup = new MintCheckBox { Font = OwnFont(new Font(Font.FontFamily,16F)), Name = "startup", MinimumSize = new Size(0,42), Text = text.Pick("开机启动", "Start with Windows"), Checked = current.Startup, AutoSize = true, TabIndex = 3, Margin = new Padding(0) };
            automaticUpdates = new MintCheckBox { Font = OwnFont(new Font(Font.FontFamily,16F)), Name = "automatic-updates", MinimumSize = new Size(0,42), Text = text.Pick("自动检查更新", "Auto-check updates"), Checked = current.AutomaticUpdateChecks, AutoSize = true, TabIndex = 4, Margin = new Padding(0) };
            basics.Controls.Add(startup,0,3); basics.Controls.Add(automaticUpdates,0,4);
            updateHint = Paragraph("update-hint", ""); updateHint.Visible = false; basics.Controls.Add(updateHint,0,5);
            playbackPanel = new PlaybackSettingsPanel(text,current.PlaybackLimitEnabled,current.PlaybackLimitMinutes,refreshAudio,saveWhitelist) { Dock = DockStyle.Fill, TabIndex = 1, Margin = new Padding(6,0,0,0), Padding = new Padding(24) };
            playbackPanel.ArtResource = "ScreenOffTray.Ui.Resources.theme.card-right-scene.png";
            configuration.Controls.Add(basics,0,0); configuration.Controls.Add(playbackPanel,1,0); content.Controls.Add(configuration,0,1);
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

            footer = new MintCardLayoutPanel(1) { Name="footer", AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink, Dock=DockStyle.Top,
                Margin=new Padding(14,6,14,12), Padding=new Padding(15,12,15,12) };
            footer.SuspendLayout(); buildingTables.Add(footer); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            footerRow = new MintCardLayoutPanel(3) { Name="footer-row", AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink,
                Dock=DockStyle.Top, Margin=new Padding(0), RoundedSurface=false, BackColor=Color.Transparent,
                ArtResource="ScreenOffTray.Ui.Resources.theme.footer-flowers.png" };
            footerRow.SuspendLayout(); buildingTables.Add(footerRow);
            footerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,55)); footerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,12));
            footerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var footerCopy = Table("footer-copy",1); footerCopy.Dock = DockStyle.Fill; footerCopy.Margin = new Padding(0,0,12,0);
            var behavior = Paragraph("behavior",text.Pick("关屏后保护 3 秒，首次实体按键或点击只亮屏。", "3-second guard; first physical key or click only wakes the display."));
            behavior.Font = OwnFont(new Font(Font.FontFamily,14F)); behavior.ForeColor = SecondaryText; behavior.Margin = new Padding(0);
            var feedback = Table("feedback-line",2); feedback.Dock = DockStyle.Top; feedback.Margin = new Padding(0,0,0,6);
            feedback.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,38)); feedback.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            feedback.Controls.Add(new MintInfoMark { Name = "feedback-info", Size = new Size(26,26), Margin = new Padding(0,2,10,0), TabStop = false },0,0);
            feedback.Controls.Add(behavior,1,0); footerCopy.Controls.Add(feedback,0,0);
            versionRow = new FlowLayoutPanel { Name = "version-row", BackColor=Color.Transparent, AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = new Padding(0) };
            var versionLabel = Paragraph("version", "v" + (version ?? Application.ProductVersion)); versionLabel.ForeColor = SecondaryText; versionLabel.Font = OwnFont(new Font(Font.FontFamily,14F)); versionLabel.Margin = new Padding(0,0,18,0);
            updateCheck = new LinkLabel { Name = "check-updates", AutoSize = true, Font = OwnFont(new Font(Font.FontFamily,14F)), Text = text.Pick("检查更新", "Check updates"), Margin = new Padding(0) }; StyleLink(updateCheck);
            versionRow.Controls.AddRange(new Control[] { versionLabel,updateCheck }); footerCopy.Controls.Add(versionRow,0,1); footerRow.Controls.Add(footerCopy,0,0);
            buttons = new FlowLayoutPanel { Name = "buttons", BackColor = Color.Transparent, FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0), TabIndex = 1, Anchor = AnchorStyles.Right };
            cancelButton = Button("cancel",text.Pick("取消", "Cancel"),0); saveButton = Button("save",text.Pick("保存", "Save"),1); StylePrimaryButton(saveButton);
            var saveSurface = saveButton as SurfaceButton; if (saveSurface != null) saveSurface.Symbol = 4;
            cancelButton.Font = saveButton.Font = OwnFont(new Font(Font.FontFamily,16F)); cancelButton.MinimumSize = new Size(140,54); saveButton.MinimumSize = new Size(158,54); cancelButton.Margin = new Padding(0,0,16,0); saveButton.Margin = new Padding(0);
            buttons.Controls.AddRange(new Control[] { cancelButton,saveButton }); footerRow.Controls.Add(buttons,2,0); footer.Controls.Add(footerRow,0,0); root.Controls.Add(footer,0,2);
            AcceptButton = saveButton; CancelButton = cancelButton; cancelButton.DialogResult = DialogResult.None;
            defaults.Click += delegate { hotkey.SetBinding(HotkeyBinding.Default); SetError(""); };
            hotkey.BindingChanged += delegate { SetError(hotkey.Error); };
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
            content.Layout += ContentLayoutChanged;
            // configuration has a measured fixed row height. Genuine card changes
            // must invalidate that measure, while geometry committed by our own
            // sizing transaction must not feed back into another full layout.
            basics.Layout += CardContentLayoutChanged;
            playbackPanel.Layout += CardContentLayoutChanged;
            // Record geometry even during a presentation transaction. RequestLayout
            // defers the work; dropping the notification here can lose a real resize.
            viewport.SizeChanged += delegate {
                int width = Math.Max(1,viewport.ClientSize.Width);
                bool narrow = width < Units(840);
                // Scrollbar- and dock-induced feedback after a coordinated commit is
                // already represented by content width and topology.
                if (content.Width == width && (configuration.ColumnCount == 1) == narrow) return;
                RequestLayout(SizeReasons());
            };
            ClientSizeChanged += delegate { RequestLayout(SizeReasons()); };
            Load += delegate {
                // Manual positioning belongs to the caller (including isolated test desktops).
                if (StartPosition == FormStartPosition.CenterScreen) {
                    AdaptToCurrentWorkArea(true);
                }
            };
            Shown += delegate { saveButton.Focus(); };
            UpdatePreferenceHint(); ApplySetup();
            for (int i = buildingTables.Count - 1; i >= 0; i--) buildingTables[i].ResumeLayout(true);
            buildingTables.Clear();
            layoutReady = true;
            ApplyWindowDpi((int)GetDpiForWindow(Handle));
            ResumeLayout(true);
            CompleteLayout(true);
            language.SelectedIndexChanged += delegate { PreviewLanguage(); };
        }

        private void Caption(string name,string zh,string en)
        {
            Control[] matches=Controls.Find(name,true);
            if(matches.Length!=0) matches[0].Text=text.Pick(zh,en);
        }
        private void PreviewLanguage()
        {
            if(changingLanguage || !layoutReady || IsDisposed || language.SelectedIndex<0) return;
            bool english=language.SelectedIndex==1;
            if(text.English==english) return;
            if(Running || closeWhenSafe || saving) {
                changingLanguage=true;
                try { language.SelectedIndex=text.English ? 1 : 0; }
                finally { changingLanguage=false; }
                return;
            }
            using(SettingsDpiContext.EnterUi()) {
                changingLanguage=true;
                bool defaultHeight=Math.Abs(ClientSize.Height-Units(text.English ? 720 : 654))<=Units(2);
                BeginPresentation(); SuspendLayoutTree(this);
                try {
                    text=new UiText(english ? "en" : "zh-CN");
                    string family=english ? "Segoe UI" : "Microsoft YaHei UI";
                    RescaleOwnedFonts(this,ownedFonts,fontPoints,appliedDpi,fontRasterDpi,family);
                    Text=text.Pick("ScreenOffTray · 设置","ScreenOffTray · Settings");
                    subheading.Text=text.Pick("设置","Settings");cat.English=english;
                    AccessibleDescription=text.Pick("编辑期间暂缓自动关屏；关闭设置后继续在托盘运行。","Auto off is on hold while editing. Closing settings keeps the app in the tray.");
                    Caption("basic-title","基础设置","Basic settings");
                    Caption("timeout-label","空闲关屏","Idle time");
                    Caption("timeout-unit","分钟","min");
                    Caption("hotkey-frame-label","快捷键","Shortcut");
                    Caption("defaults","恢复默认","Reset");
                    Caption("language-label","语言","Language");
                    Caption("startup","开机启动","Start with Windows");
                    Caption("automatic-updates","自动检查更新","Auto-check updates");
                    Caption("behavior","关屏后保护 3 秒，首次实体按键或点击只亮屏。","3-second guard; first physical key or click only wakes the display.");
                    Caption("cancel-prepare","取消准备","Cancel setup");
                    Caption("display-settings","高级：Windows 显示设置","Advanced: Windows display settings");
                    Caption("release-page","前往 GitHub 下载","Download on GitHub");
                    Caption("save","保存","Save");
                    language.AccessibleName=text.Pick("界面语言","Language");
                    hotkey.AccessibleName=text.Pick("关屏快捷键，按组合键录入","Display-off shortcut, press a key combination");
                    timeout.AccessibleName=text.Pick("空闲关屏分钟数，1至1440","Idle timeout in minutes, 1 to 1440");
                    timeout.Parent.Controls["timeout-minus"].AccessibleName="− "+timeout.AccessibleName;
                    timeout.Parent.Controls["timeout-plus"].AccessibleName="+ "+timeout.AccessibleName;
                    playbackPanel.ApplyLanguage(text,appliedDpi,fontRasterDpi);
                    UpdatePreferenceHint();ApplySetup();
                    RefreshUpdate(checkingUpdates,rawUpdateDetail,releaseShown,updateFailed);
                    SetError(rawError);
                    if(defaultHeight && WindowState==FormWindowState.Normal) {
                        ClientSize=new Size(ClientSize.Width,Units(english ? 720 : 654));
                        AdaptToCurrentWorkArea(false);
                    }
                    // Re-read cached setup/update presentation in the host's DPI
                    // context. This event never requests display or audio work.
                    EventHandler changed=LanguagePreviewChanged;
                    if(changed!=null) InvokeAction(delegate { changed(this,EventArgs.Empty); });
                } finally {
                    try { ResumeLayoutTree(this); }
                    finally { changingLanguage=false;EndPresentation(true); }
                }
                RequestLayout(LayoutReason.Content);CompleteLayout(true);
            }
        }

        internal void RefreshPlayback()
        {
            using (SettingsDpiContext.EnterUi()) {
            if (IsDisposed || readPlayback == null) return;
            try { playbackPanel.RefreshState(readPlayback()); }
            catch (Exception ex) { playbackPanel.ShowReadError(ex.Message); }
            // Text/visibility/list changes propagate through content.Layout.
            // An identical timer snapshot must not force a whole-page layout.
            }
        }

        protected override void SetVisibleCore(bool value)
        {
            if (!value || !layoutReady) { base.SetVisibleCore(value); return; }
            // Native child creation, initial playback and Combo DPI repair all
            // belong to one first-show layout, not one full Fit per child.
            presentationDepth++;
            SuspendLayoutTree(this);
            try { base.SetVisibleCore(value); }
            finally {
                try { ResumeLayoutTree(this); }
                finally { presentationDepth--; }
            }
            RequestLayout(LayoutReason.Content);
            CompleteLayout(true);
        }
        private static void SuspendLayoutTree(Control control)
        {
            control.SuspendLayout();
            foreach (Control child in control.Controls) SuspendLayoutTree(child);
        }
        private static void ResumeLayoutTree(Control control)
        {
            foreach (Control child in control.Controls) ResumeLayoutTree(child);
            control.ResumeLayout(true);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) {
                layoutStopped = true; layoutGeneration++; layoutQueued = false; pendingLayout = LayoutReason.None;
                ResumeInteractiveRoot(false);
            }
            if (disposing && playbackTimer != null) { playbackTimer.Stop(); playbackTimer.Dispose(); }
            base.Dispose(disposing);
            if (disposing) {
                if (ownedApplicationIcon != null) { ownedApplicationIcon.Dispose(); ownedApplicationIcon = null; }
                foreach (Font font in ownedFonts) font.Dispose();
                ownedFonts.Clear();
                fontPoints.Clear();
            }
        }

        private TableLayoutPanel Table(string name, int columns)
        {
            var table = new TableLayoutPanel { Name = name, ColumnCount = columns, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0), Padding = new Padding(0), BackColor = Color.Transparent, TabStop = false };
            table.SuspendLayout(); buildingTables.Add(table);
            if (columns == 1) table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return table;
        }
        private static Label Paragraph(string name, string value)
        {
            return new MintLabel { Name = name, Text = value, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 3, 0, 3) };
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
        private static void AddField(TableLayoutPanel table, int row, string label, Control control, MintIconKind icon = MintIconKind.None)
        {
            table.Controls.Add(new MintIconLabel { Name = control.Name+"-label", Text = label, IconKind=icon, AutoSize = true, Margin = new Padding(0, 4, 8, 0), TabIndex = control.TabIndex }, 0, row);
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
        internal static void RescaleOwnedFonts(Control view, List<Font> owned, Dictionary<Font,float> points, int dpi, float rasterDpi,string family=null)
        {
            var nativeFonts = new HashSet<Font>(FontIdentity);
            CollectNativeFonts(view,nativeFonts);
            var replacements = new Dictionary<Font,Font>(FontIdentity);
            foreach (Font font in owned) {
                bool native = nativeFonts.Contains(font);
                float size = points[font] * (native ? dpi/72F : dpi/rasterDpi);
                GraphicsUnit unit = native ? GraphicsUnit.Pixel : GraphicsUnit.Point;
                // Framework 4.8 can cache the native point-font DC at the host's
                // initial 96 DPI. Pixel fonts give native HFONT and GDI painting
                // the same explicit window size regardless of cache order.
                if (font.Unit != unit || Math.Abs(font.Size-size) > .01F || (!String.IsNullOrEmpty(family) && font.FontFamily.Name!=family))
                    replacements.Add(font,new Font(family ?? font.FontFamily.Name,size,font.Style,unit));
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
        private static void CollectNativeFonts(Control control, HashSet<Font> nativeFonts)
        {
            // These fields own dedicated fonts. Keep point fonts on the existing
            // GDI-painted labels/buttons; converting them changes their glyph size.
            if(control is MintNumberBox || control is HotkeyCaptureBox || control is MintComboBox) nativeFonts.Add(control.Font);
            foreach(Control child in control.Controls) CollectNativeFonts(child,nativeFonts);
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
                SuspendLayoutTree(this);
                bool previousSizing = sizing; sizing = true;
                try {
                    float target = dpi / 96F, ratio = target / layoutScale;
                    if (Math.Abs(ratio-1F) > .001F) Scale(new SizeF(ratio,ratio));
                    RescaleOwnedFonts(this,ownedFonts,fontPoints,dpi,fontRasterDpi);
                    playbackPanel.ApplyFontDpi(dpi,fontRasterDpi);
                    appliedDpi = dpi; AutoScaleDimensions = new SizeF(dpi,dpi);
                } finally { try { ResumeLayoutTree(this); } finally { sizing = previousSizing; } }
                RequestLayout(LayoutReason.Dpi);
                CompleteLayout(true);
            }
        }
        // Pure geometry seam: fake work areas do not enumerate or alter displays.
        internal static Rectangle FitToWorkArea(Rectangle desired, Rectangle work, int dpi, Size frame, bool initial, int preferredHeight = 654)
        {
            float scale = Math.Max(1,dpi)/96F;
            int gap = Math.Min((int)Math.Round(10*scale),Math.Max(0,Math.Min(work.Width,work.Height)/8));
            Rectangle available = Rectangle.Inflate(work,-gap,-gap);
            int maxWidth = Math.Max(1,initial ? Math.Min(available.Width,(int)(work.Width*.9)) : available.Width);
            int maxHeight = Math.Max(1,initial ? Math.Min(available.Height,(int)(work.Height*.8)) : available.Height);
            int width = Math.Min(maxWidth,Math.Max(1,initial ? (int)Math.Round(984*scale)+frame.Width : desired.Width));
            int height = Math.Min(maxHeight,Math.Max(1,initial ? (int)Math.Round(preferredHeight*scale)+frame.Height : desired.Height));
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
                    Rectangle fitted = FitToWorkArea(Bounds,work,appliedDpi,new Size(Width-ClientSize.Width,Height-ClientSize.Height),initial,text.English ? 720 : 654);
                    if (Bounds != fitted) Bounds = fitted;
                    lastWorkArea = work;
                } finally { adapting = false; }
            }
        }
        private void QueueWorkAreaAdaptation()
        {
            if (!adapting && Visible) RequestLayout(LayoutReason.WorkArea);
        }
        protected override void WndProc(ref Message message)
        {
            using (SettingsDpiContext.EnterUi()) {
                if (message.Msg == 0x02E0 && layoutReady) { // WM_DPICHANGED: this view owns scaling, not process-wide WinForms opt-ins.
                    bool resumeFreeze=interactiveRootSuspended;
                    ResumeInteractiveRoot(false);
                    int dpi = (int)(message.WParam.ToInt64() & 0xffff);
                    Rectangle? suggested = null;
                    if (message.LParam != IntPtr.Zero) {
                        var rect = (NativeRect)Marshal.PtrToStructure(message.LParam,typeof(NativeRect));
                        suggested = Rectangle.FromLTRB(rect.Left,rect.Top,rect.Right,rect.Bottom);
                    }
                    ApplyWindowDpi(dpi);
                    if (WindowState == FormWindowState.Normal && suggested.HasValue) Bounds = suggested.Value;
                    RequestLayout(LayoutReason.Dpi | LayoutReason.WorkArea | SizeReasons());
                    CompleteLayout(true);
                    if(resumeFreeze && interactiveSizing) SuspendInteractiveRoot();
                    message.Result = IntPtr.Zero; return;
                }
                if (message.Msg == 0x0231) { interactiveSizing = true; moveStartSize = ClientSize; SuspendInteractiveRoot(); }
                base.WndProc(ref message);
                if (!layoutReady) return;
                if (message.Msg == 0x0232) {
                    interactiveSizing = false;
                    ResumeInteractiveRoot(true);
                    LayoutReason finalReason = SizeReasons();
                    if (WindowWorkArea() != lastWorkArea) finalReason |= LayoutReason.WorkArea;
                    if (ClientSize != moveStartSize || finalLayoutPending || pendingLayout != LayoutReason.None || finalReason != LayoutReason.None) {
                        RequestLayout(finalReason); CompleteLayout(true);
                    }
                }
                else if (message.Msg == 0x007E || (message.Msg == 0x001A && message.WParam.ToInt64() == 0x002F))
                    QueueWorkAreaAdaptation();
                else if (message.Msg == 0x0047 && Visible && !adapting && WindowWorkArea() != lastWorkArea)
                    QueueWorkAreaAdaptation(); // WM_WINDOWPOSCHANGED on a different monitor/work area.
            }
        }
        private void SuspendInteractiveRoot()
        {
            if(!layoutReady || interactiveRootSuspended || root==null || root.IsDisposed) return;
            root.SuspendLayout();interactiveRootSuspended=true;
        }
        private void ResumeInteractiveRoot(bool performLayout)
        {
            if(!interactiveRootSuspended) return;
            interactiveRootSuspended=false;
            if(root==null || root.IsDisposed) return;
            bool previous=sizing;sizing=true;
            try { root.ResumeLayout(performLayout); }
            finally { sizing=previous; }
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
            RequestLayout(LayoutReason.Content);
        }
        private LayoutReason SizeReasons()
        {
            LayoutReason reason = LayoutReason.None;
            if (ClientSize.Width != arrangedClientSize.Width || (viewport != null && viewport.ClientSize.Width != arrangedViewportSize.Width)) reason |= LayoutReason.Width;
            if (ClientSize.Height != arrangedClientSize.Height || (viewport != null && viewport.ClientSize.Height != arrangedViewportSize.Height)) reason |= LayoutReason.Height;
            return reason;
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            if (!layoutReady || sizing || presentationDepth != 0 || root == null) { base.OnLayout(e); return; }
            // Let Dock set the root bounds, but leave its expensive child tree
            // to the one coordinated commit for this size notification.
            root.SuspendLayout();
            try { base.OnLayout(e); }
            finally { root.ResumeLayout(false); }
            RequestLayout(SizeReasons());
        }
        private void CardContentLayoutChanged(object sender,LayoutEventArgs e)
        {
            if(layoutReady && !sizing && presentationDepth==0 && !layoutStopped)
                RequestLayout(LayoutReason.Content);
        }
        private void ContentLayoutChanged(object sender, LayoutEventArgs e)
        {
            if (sizing || presentationDepth != 0) return;
            LayoutReason reason = SizeReasons();
            // Dock/scroll bounds notifications are geometry feedback, not new text.
            if (reason == LayoutReason.None && (e.AffectedControl != content || e.AffectedProperty != "Bounds")) reason = LayoutReason.Content;
            RequestLayout(reason);
        }
        private void RequestLayout(LayoutReason reason)
        {
            if (layoutStopped || IsDisposed || Disposing || reason == LayoutReason.None) return;
            pendingLayout |= reason;
            if (!layoutReady || presentationDepth != 0 || sizing || !IsHandleCreated) return;
            QueueLayout();
        }
        private void QueueLayout()
        {
            if (layoutQueued || layoutStopped || !IsHandleCreated || IsDisposed || Disposing) return;
            int generation = layoutGeneration; layoutQueued = true;
            try { BeginInvoke(new Action(delegate {
                if (generation != layoutGeneration || layoutStopped) return;
                layoutQueued = false;
                CompleteLayout(false);
            })); } catch (InvalidOperationException) { if (generation == layoutGeneration) layoutQueued = false; }
        }
        protected override void OnHandleDestroyed(EventArgs e)
        {
            ResumeInteractiveRoot(false);interactiveSizing=false;
            layoutGeneration++; layoutQueued = false;
            base.OnHandleDestroyed(e);
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (layoutReady && pendingLayout != LayoutReason.None) QueueLayout();
        }
        private void CompleteLayout(bool final)
        {
            if (!layoutReady || layoutStopped || presentationDepth != 0 || IsDisposed || Disposing || !IsHandleCreated) return;
            if (sizing) { finalLayoutPending |= final; return; }
            if (pendingLayout == LayoutReason.None && !finalLayoutPending) return;
            // Keep native resize feedback immediate while leaving the existing
            // TableLayoutPanel tree stable until the pointer is released.
            if (interactiveSizing && !final) {
                pendingLayout = LayoutReason.None; finalLayoutPending = true;
                return;
            }
            LayoutReason reason = pendingLayout; pendingLayout = LayoutReason.None; finalLayoutPending = false;
            sizing = true;
            try {
                if ((reason & LayoutReason.WorkArea) != 0) AdaptToCurrentWorkArea(false);
                reason |= SizeReasons();
                content.SuspendLayout(); footer.SuspendLayout();
                try {
                    // Height changes still need the docked root to commit the new
                    // viewport. Keep the body and footer suspended so root layout
                    // cannot recursively measure the complete settings tree.
                    if ((reason & LayoutReason.Height) != 0) root.PerformLayout();
                    ArrangeLayout(reason);
                }
                finally {
                    try { footer.ResumeLayout(true); }
                    finally { content.ResumeLayout(true); }
                }
                arrangedClientSize = ClientSize; arrangedViewportSize = viewport.ClientSize;
                if (scrollErrorVersion != 0 && scrollErrorVersion == errorVersion && error.Visible && Visible) {
                    scrollErrorVersion = 0;
                    if (wholeWindowScroll) ScrollControlIntoView(error); else viewport.ScrollControlIntoView(error);
                }
            } finally { sizing = false; }
            if (finalLayoutPending && !finishingLayout) {
                finishingLayout = true;
                try { CompleteLayout(true); }
                finally { finishingLayout = false; }
            }
            // A real change during commit gets one posted turn. Internal bounds
            // feedback is filtered above, so this cannot become a self-posting chain.
            if (!layoutStopped && (pendingLayout != LayoutReason.None || finalLayoutPending)) QueueLayout();
        }
        private void ArrangeLayout(LayoutReason reason)
        {
            using (SettingsDpiContext.EnterUi()) {
                bool measure = measuredWidth < 0 ||
                    (reason & (LayoutReason.Width | LayoutReason.Content | LayoutReason.Dpi)) != 0;
                if (!measure) {
                    CommitScrollGeometry(measuredBody,measuredFooter,false);
                    // A height change may add/remove the native scrollbar.
                    if (viewport.ClientSize.Width == measuredWidth || wholeWindowScroll) return;
                }
                if ((reason & (LayoutReason.Content | LayoutReason.Dpi)) != 0) measuredBodyKey = null;
                int fullWidth = Math.Max(1,wholeWindowScroll ? ClientSize.Width : viewport.Width);
                int barWidth = SystemInformation.VerticalScrollBarWidth;
                bool reserveBar = !wholeWindowScroll && viewport.VerticalScroll.Visible &&
                    (fullWidth < Units(840) || fullWidth-barWidth >= Units(840));
                int width = Math.Max(1,fullWidth-(reserveBar ? barWidth : 0));
                // Two bounded passes: full width, then a reserved vertical bar
                // only if needed. The breakpoint uses the same width as measure.
                for (int pass = 0; pass < 2; pass++) {
                MeasureAndArrangeBody(width);
                content.PerformLayout();
                // Parent docking commits the final column width only here. Flush
                // deferred child layout now, rather than leaving old one-column
                // HWND bounds behind a newly selected two-column topology.
                configuration.PerformLayout();
                basics.PerformLayout();
                playbackPanel.PerformLayout();
                bool narrow = width < Units(840);
                bool stackedFields = narrow;
                string bodyKey = (narrow ? "n" : "w") + (stackedFields ? "s" : "f") +
                    "|" + width.ToString(CultureInfo.InvariantCulture) +
                    "|" + layoutScale.ToString("R",CultureInfo.InvariantCulture);
                if (measuredBodyKey != bodyKey) {
                    int safeWidth = narrow ? Math.Min(width,Units(300)) : width;
                    measuredBody = content.GetPreferredSize(new Size(Math.Max(1,safeWidth),0)).Height;
                    measuredBodyKey = bodyKey;
                }
                int body = measuredBody;
                int footerHeight = ArrangeChrome(width < Units(840));
                int available = ClientSize.Height-header.Height-footerHeight;
                bool pageScroll = available < Units(120);
                bool needsBar = !pageScroll && body > available;
                if (pass == 0 && needsBar != reserveBar) {
                    reserveBar = needsBar;
                    width = Math.Max(1,fullWidth-(reserveBar ? barWidth : 0)); continue;
                }
                measuredWidth = width; measuredBody = body; measuredFooter = footerHeight;
                CommitScrollGeometry(body,footerHeight,true);
                break;
                }
            }
        }
        private void MeasureAndArrangeBody(int width)
        {
                bool narrow = width < Units(840);
                int innerWidth = Math.Max(1,width-Units(6)*2);
                int cardPadding = Units(24);
                int cardGap = Units(4);
                int basicOuter = narrow ? innerWidth : (int)((innerWidth-cardGap)*.472F);
                int playbackOuter = narrow ? innerWidth : Math.Max(1,innerWidth-cardGap-basicOuter);
                int basicWidth = Math.Max(1,basicOuter-cardPadding*2);
                basics.ReferenceMinimumHeight = playbackPanel.ReferenceMinimumHeight = narrow ? 0 : Units(398);
                // Read the requested geometry first; prepare the existing tables
                // once, then let the body consume one preferred-size pass.
                viewport.SuspendLayout(); content.SuspendLayout(); configuration.SuspendLayout(); basics.SuspendLayout();
                try {
                if ((configuration.ColumnCount == 1) != narrow) {
                    configuration.SuspendLayout(); configuration.Controls.Remove(basics); configuration.Controls.Remove(playbackPanel);
                    configuration.ColumnCount = narrow ? 1 : 2; configuration.RowCount = narrow ? 2 : 1;
                    configuration.ColumnStyles.Clear(); configuration.RowStyles.Clear();
                    configuration.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,narrow ? 100 : 47.2F));
                    if (!narrow) configuration.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,52.8F));
                    basics.Margin = new Padding(0,0,narrow ? 0 : Units(2),narrow ? Units(12) : 0);
                    playbackPanel.Margin = new Padding(narrow ? 0 : Units(2),0,0,0);
                    configuration.Controls.Add(basics,0,0); configuration.Controls.Add(playbackPanel,narrow ? 0 : 1,narrow ? 1 : 0);
                    configuration.ResumeLayout(false);
                }
                Padding contentPadding = new Padding(Units(6),0,Units(6),0);
                if (content.Padding != contentPadding) content.Padding = contentPadding;
                Padding basicMargin = new Padding(0,0,narrow ? 0 : Units(2),narrow ? Units(12) : 0);
                Padding playbackMargin = new Padding(narrow ? 0 : Units(2),0,0,0);
                if(basics.Margin != basicMargin) basics.Margin = basicMargin;
                if(playbackPanel.Margin != playbackMargin) playbackPanel.Margin = playbackMargin;
                if (content.Width != width) content.Width = width;
                Padding cardInsets=new Padding(Units(24),Units(24),Units(24),Units(narrow ? 104 : 24));
                if(basics.Padding!=cardInsets) basics.Padding=cardInsets;
                if(playbackPanel.Padding!=cardInsets) playbackPanel.Padding=cardInsets;
                var fields = (TableLayoutPanel)basics.Controls["basic-fields"];
                bool stackedFields = narrow;
                var shortcutLabel = fields.Controls["hotkey-frame-label"]; var languageLabel = fields.Controls["language-label"];
                var shortcutFrame = hotkey.Parent;
                Padding fieldMargin = new Padding(0,0,0,Units(16));
                if (shortcutFrame.Margin != fieldMargin) shortcutFrame.Margin = fieldMargin;
                if (language.Margin != fieldMargin) language.Margin = fieldMargin;
                if (shortcutFrame.Height != Units(39)) shortcutFrame.Height = Units(39);
                Size checkSize = new Size(0,Units(42));
                if (startup.MinimumSize != checkSize) startup.MinimumSize = checkSize;
                if (automaticUpdates.MinimumSize != checkSize) automaticUpdates.MinimumSize = checkSize;
                if (startup.Margin != Padding.Empty) startup.Margin = Padding.Empty;
                if (automaticUpdates.Margin != Padding.Empty) automaticUpdates.Margin = Padding.Empty;
                if ((fields.ColumnCount == 2) != stackedFields) {
                    fields.SuspendLayout(); fields.Controls.Clear(); fields.ColumnStyles.Clear(); fields.RowStyles.Clear();
                    fields.ColumnCount = stackedFields ? 2 : 3; fields.RowCount = stackedFields ? 4 : 2;
                    fields.ColumnStyles.Add(new ColumnStyle(stackedFields ? SizeType.Percent : SizeType.Absolute,stackedFields ? 100 : Units(78)));
                    fields.ColumnStyles.Add(new ColumnStyle(stackedFields ? SizeType.AutoSize : SizeType.Percent,stackedFields ? 0 : 100));
                    if(!stackedFields) fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                    fields.Controls.Add(shortcutLabel,0,0); fields.Controls.Add(shortcutFrame,stackedFields ? 0 : 1,stackedFields ? 1 : 0); fields.Controls.Add(defaults,stackedFields ? 1 : 2,stackedFields ? 1 : 0);
                    fields.Controls.Add(languageLabel,0,stackedFields ? 2 : 1); fields.Controls.Add(language,stackedFields ? 0 : 1,stackedFields ? 3 : 1);
                    fields.SetColumnSpan(shortcutLabel,stackedFields ? 2 : 1); fields.SetColumnSpan(languageLabel,stackedFields ? 2 : 1); fields.SetColumnSpan(language,2);
                    shortcutLabel.Margin = languageLabel.Margin = stackedFields ? new Padding(0,0,0,Units(6)) : new Padding(0,Units(9),Units(8),0);
                    fields.ResumeLayout(true);
                }
                if(!stackedFields) {
                    float logicalWidth = Units(narrow && basicWidth < Units(310) ? (text.English ? 78 : 76) : (text.English ? 150 : 150));
                    if (fields.ColumnStyles[0].Width != logicalWidth) fields.ColumnStyles[0].Width = logicalWidth;
                }
                var idleLine = (TableLayoutPanel)basics.Controls["timeout-line"]; var idleLabel = idleLine.Controls["timeout-label"]; var idleRow = timeout.Parent.Parent;
                if ((idleLine.ColumnCount == 1) != stackedFields) {
                    idleLine.SuspendLayout(); idleLine.Controls.Remove(idleLabel); idleLine.Controls.Remove(idleRow); idleLine.ColumnStyles.Clear(); idleLine.RowStyles.Clear();
                    idleLine.ColumnCount = stackedFields ? 1 : 2; idleLine.RowCount = stackedFields ? 2 : 1; idleLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
                    if(!stackedFields) idleLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                    idleLine.Controls.Add(idleLabel,0,0); idleLine.Controls.Add(idleRow,stackedFields ? 0 : 1,stackedFields ? 1 : 0);
                    idleLabel.Margin = stackedFields ? new Padding(0,0,0,Units(8)) : new Padding(0,Units(9),0,0); idleRow.Anchor = AnchorStyles.Right; idleLine.ResumeLayout(true);
                }
                if(!stackedFields) {
                    idleLine.ColumnStyles[0].SizeType = SizeType.Absolute;
                    float idleLabelWidth = Units(text.English ? 150 : 150);
                    if(Math.Abs(idleLine.ColumnStyles[0].Width-idleLabelWidth)>.1F) idleLine.ColumnStyles[0].Width = idleLabelWidth;
                }
                Size defaultSize = new Size(Units(narrow && basicWidth < Units(310) ? (text.English ? 68 : 80) : 92),Units(39));
                if (defaults.MinimumSize != defaultSize) defaults.MinimumSize = defaultSize;
                Padding defaultMargin = new Padding(Units(narrow && basicWidth < Units(310) ? 7 : 10),0,0,Units(16));
                if (defaults.Margin != defaultMargin) defaults.Margin = defaultMargin;
                int timeoutWidth = Units(narrow && basicWidth < Units(310) ? 128 : 180);
                if (timeout.Parent.Width != timeoutWidth) timeout.Parent.Width = timeoutWidth;
                int playbackWidth = Math.Max(1,playbackOuter-Units(4));
                if (playbackPanel.Width != playbackWidth) playbackPanel.Width = playbackWidth;
                playbackPanel.AdaptWidth();
                Size checkWidth = new Size(basicWidth,0);
                if (startup.MaximumSize != checkWidth) startup.MaximumSize = checkWidth;
                if (automaticUpdates.MaximumSize != checkWidth) automaticUpdates.MaximumSize = checkWidth;
                // Resolve row/column topology before the one preferred-size read.
                configuration.PerformLayout();
                // A percent-column TableLayoutPanel may ask each card to measure
                // against the entire row, then feed the resulting height back into
                // Dock=Fill. Measure each card at its real column width instead.
                int basicHeight=basics.GetPreferredSize(new Size(basicOuter,0)).Height;
                int playbackHeight=playbackPanel.GetPreferredSize(new Size(playbackOuter,0)).Height;
                int configurationHeight=narrow ? basicHeight+playbackHeight+Units(12) : Math.Max(basicHeight,playbackHeight);
                configuration.AutoSize=false;
                if(configuration.RowStyles.Count != (narrow ? 2 : 1)) {
                    configuration.RowStyles.Clear();
                    configuration.RowStyles.Add(new RowStyle(SizeType.Absolute,0));
                    if(narrow) configuration.RowStyles.Add(new RowStyle(SizeType.Absolute,0));
                }
                configuration.RowStyles[0].Height=narrow ? basicHeight+Units(12) : configurationHeight;
                if(narrow) configuration.RowStyles[1].Height=playbackHeight;
                if(configuration.Height != configurationHeight) configuration.Height=configurationHeight;
                } finally {
                    basics.ResumeLayout(false); configuration.ResumeLayout(false); content.ResumeLayout(false); viewport.ResumeLayout(false);
                }
        }
        private int ArrangeChrome(bool narrow)
        {
                footer.SuspendLayout(); root.SuspendLayout();
                try {
                Padding footerPadding = new Padding(Units(narrow ? 15 : 26),Units(22),Units(narrow ? 15 : 20),Units(22));
                if (footer.Padding != footerPadding) footer.Padding = footerPadding;
                Size rowMinimum=new Size(0,Units(68));
                if(footerRow.MinimumSize!=rowMinimum) footerRow.MinimumSize=rowMinimum;
                Padding footerMargin = new Padding(Units(narrow ? 6 : 10),Units(4),Units(narrow ? 6 : 10),Units(10));
                if (footer.Margin != footerMargin) footer.Margin = footerMargin;
                int catWidth = Units(narrow ? 92 : 134), catHeight = Units(narrow ? 88 : 128);
                Rectangle catBounds = new Rectangle(Units(narrow ? 16 : 30),Units(narrow ? 14 : 0),catWidth,catHeight);
                if (cat.Bounds != catBounds) cat.SetBounds(catBounds.X,catBounds.Y,catBounds.Width,catBounds.Height);
                int titleLeft = catBounds.Right+Units(narrow ? 12 : 14);
                int titleWidth = Math.Max(Units(120),TextRenderer.MeasureText(heading.Text,heading.Font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine).Width);
                header.Title = heading.Text; header.Subtitle = subheading.Text;
                header.TitleFont = heading.Font; header.SubtitleFont = subheading.Font; header.SubtitleColor = subheading.ForeColor;
                Rectangle titleBounds = new Rectangle(titleLeft,Units(narrow ? 22 : 27),titleWidth,Units(narrow ? 70 : 74));
                if (header.TitleBounds != titleBounds) { header.TitleBounds = titleBounds; header.Invalidate(titleBounds); }
                Rectangle sceneBounds = new Rectangle(Units(410),Units(14),Units(284),Units(116));
                if (headerScene.Bounds != sceneBounds) headerScene.SetBounds(sceneBounds.X,sceneBounds.Y,sceneBounds.Width,sceneBounds.Height);
                int statusTextWidth = TextRenderer.MeasureText(headerStatus.Text ?? "",headerStatus.Font,Size.Empty,
                    TextFormatFlags.NoPadding|TextFormatFlags.SingleLine).Width;
                int recheckWidth = recheckLink.Visible ? recheckLink.PreferredSize.Width : 0;
                bool compactState = narrow || header.Width < Units(950) ||
                    statusTextWidth+recheckWidth+Units(48) > Units(text.English ? 256 : 240);
                int headerHeight=Units(compactState ? 170 : 130);
                if(header.Height!=headerHeight) header.Height=headerHeight;
                headerScene.Visible = !compactState && !SystemInformation.HighContrast;
                int stateLeft = compactState ? (narrow ? Units(12) : titleLeft) : header.Width-Units(text.English ? 272 : 256);
                int stateWidth = Math.Max(Units(170),header.Width-stateLeft-Units(12));
                Rectangle stateBounds = new Rectangle(stateLeft,Units(compactState ? 117 : 42),stateWidth,Units(36));
                if (stateLine.Bounds != stateBounds) stateLine.SetBounds(stateBounds.X,stateBounds.Y,stateBounds.Width,stateBounds.Height);
                int copyWidth = text.English ? Units(500) : Units(530);
                bool narrowFooter = footer.ClientSize.Width-footer.Padding.Horizontal < copyWidth+buttons.PreferredSize.Width+Units(12);
                if ((footerRow.ColumnCount == 1) != narrowFooter) {
                    var copy = footerRow.Controls["footer-copy"];
                    footerRow.SuspendLayout(); footerRow.Controls.Remove(copy); footerRow.Controls.Remove(buttons);
                    footerRow.ColumnCount = narrowFooter ? 1 : 3; footerRow.RowCount = narrowFooter ? 2 : 1;
                    footerRow.ColumnStyles.Clear(); footerRow.RowStyles.Clear(); footerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,narrowFooter ? 100 : 55));
                    if (!narrowFooter) { footerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,12)); footerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); }
                    Padding copyMargin = new Padding(0,0,narrowFooter ? 0 : Units(12),narrowFooter ? Units(14) : 0);
                    if (copy.Margin != copyMargin) copy.Margin = copyMargin;
                    footerRow.Controls.Add(copy,0,0); footerRow.Controls.Add(buttons,narrowFooter ? 0 : 2,narrowFooter ? 1 : 0);
                    buttons.Anchor = AnchorStyles.Right; footerRow.ResumeLayout(true);
                }
                // AutoSize can retain the footer's former multi-line measurement
                // after columns stack. Reserve its current preferred height explicitly.
                int footerHeight = footer.GetPreferredSize(new Size(root.ClientSize.Width,0)).Height + footer.Margin.Vertical;
                if(root.RowStyles[2].SizeType != SizeType.Absolute) root.RowStyles[2].SizeType = SizeType.Absolute;
                if(Math.Abs(root.RowStyles[2].Height-footerHeight) > .1F) root.RowStyles[2].Height = footerHeight;
                return footerHeight;
                } finally { footer.ResumeLayout(false); root.ResumeLayout(true); }
        }
        private void CommitScrollGeometry(int body, int footerHeight, bool bodyChanged)
        {
                bool changed = bodyChanged;
                if (content.Height != body) { content.Height = body; changed = true; }
                // In a very short work area, scroll the complete page instead of
                // letting fixed chrome consume the body. Normal C stays unchanged.
                bool scrollWindow = ClientSize.Height < header.Height+footerHeight+Units(120);
                if(scrollWindow != wholeWindowScroll) {
                    changed = true;
                    wholeWindowScroll = scrollWindow;
                    AutoScrollPosition = Point.Empty; AutoScrollMinSize = Size.Empty;
                    viewport.AutoScrollMinSize = Size.Empty; viewport.AutoScroll = !scrollWindow;
                    AutoScroll = scrollWindow; root.Dock = scrollWindow ? DockStyle.Top : DockStyle.Fill;
                }
                if(wholeWindowScroll) {
                    if(root.Width != ClientSize.Width) { root.Width = ClientSize.Width; changed = true; }
                    int fullHeight = header.Height+body+footerHeight;
                    if(root.Height != fullHeight) { root.Height = fullHeight; changed = true; }
                    Size page = new Size(0,fullHeight);
                    if(AutoScrollMinSize != page) { AutoScrollMinSize = page; changed = true; }
                } else {
                    Size scroll = new Size(0,body);
                    if(viewport.AutoScrollMinSize != scroll) { viewport.AutoScrollMinSize = scroll; changed = true; }
                }
                if (bodyChanged) content.PerformLayout();
                if (changed) viewport.PerformLayout();
        }
        private int Units(int logical) { return Math.Max(1,(int)Math.Round(logical*layoutScale)); }
        private string Localize(string value)
        {
            if(String.IsNullOrEmpty(value)) return "";
            string[] lines=value.Replace("\r\n","\n").Split('\n');
            for(int i=0;i<lines.Length;i++) {
                string[] parts=lines[i].Split(new[]{" / "},StringSplitOptions.None);
                if(parts.Length>1) lines[i]=text.English ? parts[parts.Length-1] : parts[0];
            }
            return String.Join(Environment.NewLine,lines);
        }
        private void SetError(string value)
        {
            rawError=value;
            errorVersion++;
            error.Text = Localize(value); error.Visible = !String.IsNullOrEmpty(error.Text);
            scrollErrorVersion = error.Visible ? errorVersion : 0;
            FitHeight(); CompleteLayout(true);
        }
        private bool Running { get { return prepareRequested || setup.Preparing; } }

        internal Func<SettingsValues,System.Threading.Tasks.Task<string>> SaveAsync;
        internal void ConfigureWhitelistSave(Func<string[],System.Threading.Tasks.Task<string>> save) {playbackPanel.SaveWhitelistAsync=save;}
        private async void SaveValues()
        {
            if (Running || saving || closeWhenSafe) return;
            if (hotkey.HasHeldKeys) { SetError("请先松开组合键，再保存。 / Release the shortcut keys before saving."); return; }
            if (!String.IsNullOrEmpty(hotkey.Error)) { SetError(hotkey.Error); return; }
            int minutes;
            if (!Int32.TryParse(timeout.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out minutes) || minutes < 1 || minutes > 1440) {
                SetError("请输入 1–1440 分钟。 / Enter 1–1440 minutes."); return;
            }
            int playbackMinutes; string playbackError;
            if (!playbackPanel.TryReadLimit(out playbackMinutes, out playbackError)) { SetError(playbackError); return; }
            var value = new SettingsValues { TimeoutMinutes = minutes, Hotkey = hotkey.Binding,
                Language = language.SelectedIndex == 1 ? "en" : "zh-CN", Startup = startup.Checked,
                AutomaticUpdateChecks = automaticUpdates.Checked,
                PlaybackLimitEnabled = playbackPanel.LimitEnabled, PlaybackLimitMinutes = playbackMinutes };
            saving = true; saveButton.Enabled = false;
            try {
                string reason = SaveAsync == null ? save(value) : await SaveAsync(value);
                if (!String.IsNullOrEmpty(reason)) { SetError(reason); return; }
                saving = false; DialogResult = DialogResult.OK; Close();
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
            if(saving) {e.Cancel=true;return;}
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
            rawUpdateDetail=detail;updateFailed=failed;
            string checkText = checking ? text.Pick("正在检查…", "Checking…") :
                failed ? text.Pick("重新检查", "Check again") : text.Pick("检查更新", "Check for updates");
            string localizedDetail=Localize(detail);
            bool bodyChanged = updateDetail.Text != localizedDetail || updateCheck.Text != checkText || releaseShown != available;
            BeginPresentation();
            try {
            checkingUpdates = checking;
            updateCheck.Text = checkText;
            updateDetail.Text = localizedDetail;
            updateDetail.ForeColor = failed && !SystemInformation.HighContrast ? Color.FromArgb(166, 38, 24) : SecondaryText;
            updateSection.Visible = !String.IsNullOrEmpty(updateDetail.Text);
            releasePage.Visible = available;
            releaseShown = available;
            UpdateActions();
            } finally { EndPresentation(bodyChanged); }
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
            bool showSetup = setup.Visible && !(setup.Ready && !running);
            string detail = Localize(setup.Detail);
            if (prepareRequested) detail = text.Pick("正在准备，请稍候…", "Preparing. Please wait…");
            if (cancelRequested) detail += "\r\n" + text.Pick("已请求取消，等待处理结果…", "Cancellation requested; waiting for the result…");
            string diagnosticText = detail + (String.IsNullOrEmpty(setup.Diagnostics) ? "" : "\r\n\r\n" + setup.Diagnostics);
            string prepareText = running ? text.Pick("正在准备…", "Preparing…") : setup.Checking
                ? text.Pick("正在检查…", "Checking…") : text.Pick("一键准备", "Prepare");
            bool bodyChanged = setupShown != showSetup || (showSetup &&
                (setupDetail.Text != detail || setupExpanded != expanded || setupRunning != running || prepareButton.Text != prepareText ||
                 (expanded && diagnostics.Text != diagnosticText)));
            BeginPresentation();
            try {
            headerStatus.Text = running ? text.Pick("正在准备…", "Preparing…") : setup.Checking ? text.Pick("正在检查…", "Checking…") : setup.Ready ? text.Pick("显示已就绪", "Display ready") : text.Pick("关屏尚未启用", "Display off not enabled");
            bool ready = setup.Ready && !running && !setup.Checking;
            if (displayMark.Ready != ready) { displayMark.Ready = ready; displayMark.Invalidate(); }
            recheckLink.Visible = recheck != null;
            recheckLink.Text = setup.Ready ? text.Pick("检查", "Check") : text.Pick("重新检查", "Recheck");
            configuration.Enabled = !running && !closeWhenSafe;
            saveButton.Enabled = !running && !closeWhenSafe && !saving;
            cancelButton.Enabled = !closeWhenSafe;
            cancelButton.Text = closeWhenSafe ? text.Pick("等待取消…", "Waiting…") : text.Pick("取消", "Cancel");
            setupSection.Visible = showSetup;
            if (setupDetail.Text != detail) setupDetail.Text = detail;
            prepareButton.Text = prepareText;
            prepareButton.Enabled = !running && !setup.Checking && setup.CanPrepare && !closeWhenSafe;
            cancelSetup.Visible = running; cancelSetup.Enabled = running && !cancelRequested;
            details.Text = expanded ? text.Pick("收起诊断", "Hide diagnostics") : text.Pick("诊断详情", "Diagnostic details");
            setupDetail.AccessibleDescription = setupDetail.Text;
            if (diagnostics.Text != diagnosticText) diagnostics.Text = diagnosticText;
            diagnostics.Visible = displaySettings.Visible = expanded;
            displaySettings.Enabled = !running && !setup.Checking && !closeWhenSafe;
            UpdateActions();
            setupShown = showSetup; setupExpanded = expanded; setupRunning = running;
            } finally { EndPresentation(bodyChanged); }
        }
        private void BeginPresentation()
        {
            presentationDepth++;
            root.SuspendLayout(); content.SuspendLayout(); setupSection.SuspendLayout(); updateSection.SuspendLayout();
            stateLine.SuspendLayout(); versionRow.SuspendLayout();
        }
        private void EndPresentation(bool fitBody)
        {
            try {
                stateLine.ResumeLayout(true); versionRow.ResumeLayout(true);
                // Hidden diagnostic text and header-only state changes do not
                // require the body to consume its deferred preferred-size pass.
                setupSection.ResumeLayout(fitBody); updateSection.ResumeLayout(fitBody);
                content.ResumeLayout(fitBody); root.ResumeLayout(fitBody);
            } finally { presentationDepth--; }
            if (fitBody) FitHeight();
        }
    }

    // Presentation-only wrappers retain the native controls and their keyboard semantics.
    internal sealed class InputFrame : Panel
    {
        private readonly Control input;
        private float visualScale = 1F;
        public override Size GetPreferredSize(Size proposedSize)
        {
            return new Size(1,Math.Max(1,(int)Math.Round(39*visualScale)));
        }
        protected override void ScaleControl(SizeF factor,BoundsSpecified specified) { visualScale *= factor.Height; base.ScaleControl(factor,specified); }
        internal InputFrame(Control input)
        {
            this.input = input; Name = input.Name + "-frame"; TabIndex = input.TabIndex;
            TabStop = false; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Height = 39; Width = input.Width + 16; Padding = new Padding(8, 4, 8, 4);
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
            var box = input as TextBox;
            int h = box == null ? input.PreferredSize.Height : box.PreferredHeight;
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
        private float visualScale = 1F;
        protected override void ScaleControl(SizeF factor,BoundsSpecified specified) { visualScale *= factor.Height; base.ScaleControl(factor,specified); }
        public override Size GetPreferredSize(Size proposedSize)
        {
            Size caption=TextRenderer.MeasureText(Text,Font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine);
            int icon=Symbol==0 ? 0 : Font.Height+(int)Math.Round(6*visualScale);
            return new Size(Math.Max(MinimumSize.Width,caption.Width+Padding.Horizontal+icon+(int)Math.Round(6*visualScale)),
                Math.Max(MinimumSize.Height,Font.Height+Padding.Vertical+(int)Math.Round(6*visualScale)));
        }
        internal SurfaceButton()
        {
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.SupportsTransparentBackColor,true);BackColor=Color.Transparent;
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
            // WM_PRINTCLIENT can arrive without WM_ERASEBKGND. Reconstruct the
            // parent surface on this DC before painting rounded corners.
            if(Parent!=null) {
                var saved=e.Graphics.Save();
                try { e.Graphics.TranslateTransform(-Left,-Top);InvokePaintBackground(Parent,new PaintEventArgs(e.Graphics,Bounds)); }
                finally { e.Graphics.Restore(saved); }
            } else e.Graphics.Clear(MintTheme.Card);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color background = !Enabled ? Color.FromArgb(227,235,237) : Primary
                ? (pressed ? Color.FromArgb(0,124,119) : hovered ? Color.FromArgb(0,124,119) : MintTheme.Green)
                : (pressed ? Color.FromArgb(232,244,239) : hovered ? Color.FromArgb(232,243,242) : MintTheme.Soft);
            Color foreground = !Enabled ? Color.FromArgb(109,133,144) : Primary ? Color.White : Color.FromArgb(51,88,105);
            using (var path = Outline(ClientRectangle, Math.Max(5, Font.Height / 3))) {
                using (var brush = new LinearGradientBrush(ClientRectangle,
                    Enabled && Primary ? Color.FromArgb(54,191,145) : background,
                    Enabled && Primary ? Color.FromArgb(0,157,107) : background,90F)) e.Graphics.FillPath(brush, path);
                if(Primary) using(var pen=new Pen(Color.FromArgb(0,139,91),Math.Max(1,visualScale))) e.Graphics.DrawPath(pen,path);
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
                    else if (Symbol == 4) { e.Graphics.DrawLines(pen,new[] { new Point(left+2,top+size/2),new Point(left+size/2,top+size-3),new Point(left+size-2,top+2) }); }
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
