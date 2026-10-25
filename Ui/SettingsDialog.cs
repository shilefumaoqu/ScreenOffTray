using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenOffTray.Ui
{
    internal sealed class SettingsValues
    {
        internal int TimeoutMinutes;
        internal HotkeyBinding Hotkey;
        internal string Language;
        internal bool Startup;
        internal bool AutomaticUpdateChecks = true;
    }

    internal sealed class SetupPanelState
    {
        internal bool Visible, Checking, Preparing, CanPrepare, Ready;
        internal string Detail, Diagnostics;
    }

    internal sealed class SettingsDialog : Form
    {
        private readonly UiText text;
        private readonly Func<SettingsValues, string> save;
        private readonly Action prepare, cancelPrepare, openDisplaySettings;
        private readonly Action checkUpdates, openRelease;
        private readonly TableLayoutPanel content, configuration, setupSection;
        private readonly Panel viewport;
        private readonly NumericUpDown timeout;
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

        internal SettingsDialog(UiText text, SettingsValues current, Func<SettingsValues, string> save,
            Action prepare, Action cancelPrepare, Action openDisplaySettings,
            Action checkUpdates = null, Action openRelease = null, string version = null)
        {
            this.text = text; this.save = save; this.prepare = prepare;
            this.cancelPrepare = cancelPrepare; this.openDisplaySettings = openDisplaySettings;
            this.checkUpdates = checkUpdates; this.openRelease = openRelease;
            Text = "ScreenOffTray";
            Font = new Font(text.English ? "Segoe UI" : "Microsoft YaHei UI", 10F);
            BackColor = SystemInformation.HighContrast ? SystemColors.Control : Color.White;
            ForeColor = SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(36, 48, 62);
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false; ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(540, 440);

            content = Table("content", 1); content.Dock = DockStyle.Top; content.Padding = new Padding(28, 22, 28, 24);
            var heading = Paragraph("heading", text.Pick("自动关屏设置", "Display off settings"));
            heading.Font = new Font(Font.FontFamily, 17F, FontStyle.Bold);
            heading.Margin = new Padding(0, 0, 0, 8); content.Controls.Add(heading, 0, 0);
            var description = Paragraph("description", text.Pick("仅关闭显示，不锁屏、不睡眠。", "Turns off the display without locking or sleeping."));
            description.ForeColor = SecondaryText; description.Margin = new Padding(0, 0, 0, 26);
            content.Controls.Add(description, 0, 1);
            configuration = Table("configuration", 3); configuration.Dock = DockStyle.Top;
            configuration.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            configuration.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            configuration.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
            var timeoutRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            timeout = new NumericUpDown { Name = "timeout", Minimum = 1, Maximum = 1440, Value = Math.Max(1, Math.Min(1440, current.TimeoutMinutes)),
                Width = 112, Margin = new Padding(0), TabIndex = 0, AccessibleName = text.Pick("空闲关屏分钟数，1至1440", "Idle timeout in minutes, 1 to 1440") };
            timeoutRow.Controls.Add(new InputFrame(timeout) { Width = 124, Margin = new Padding(0) });
            timeoutRow.Controls.Add(new Label { Text = text.Pick("分钟", "minutes"), AutoSize = true, Margin = new Padding(10, 9, 0, 0), ForeColor = SecondaryText });
            AddField(configuration, 0, text.Pick("空闲关屏(&T)", "Idle timeout (&T)"), timeoutRow);

            hotkey = new HotkeyCaptureBox { Name = "hotkey", Dock = DockStyle.Fill, TabIndex = 1,
                AccessibleName = text.Pick("关屏快捷键，按组合键录入", "Display-off shortcut, press a key combination") };
            hotkey.SetBinding(current.Hotkey ?? HotkeyBinding.Default);
            AddField(configuration, 1, text.Pick("关屏快捷键(&K)", "Shortcut (&K)"), new InputFrame(hotkey) { Dock = DockStyle.Fill });
            defaults = Button("defaults", text.Pick("恢复默认(&D)", "Use default (&D)"), 2);
            configuration.Controls.Add(defaults, 2, 1);
            var hint = Paragraph("hint", text.Pick("点击输入框后按组合键；松开后继续。", "Focus the box and press a shortcut; release to continue."));
            hint.ForeColor = SecondaryText;
            hint.Font = new Font(Font.FontFamily, 9F);
            hint.Margin = new Padding(0, 0, 0, 16);
            configuration.Controls.Add(hint, 1, 2); configuration.SetColumnSpan(hint, 2);
            error = Paragraph("error", ""); error.Visible = false;
            error.ForeColor = SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(166, 38, 24);
            error.BackColor = SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(255, 244, 242);
            error.Padding = new Padding(10, 8, 10, 8); error.Margin = new Padding(0, 0, 0, 16);
            error.AccessibleRole = AccessibleRole.Alert;
            configuration.Controls.Add(error, 1, 3); configuration.SetColumnSpan(error, 2);
            language = new ComboBox { Name = "language", DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 190, FlatStyle = FlatStyle.Flat, TabIndex = 3, AccessibleName = text.Pick("界面语言", "Interface language") };
            language.Items.AddRange(new object[] { "简体中文", "English" });
            language.SelectedIndex = current.Language == "en" ? 1 : 0;
            AddField(configuration, 4, text.Pick("界面语言(&L)", "Language (&L)"), language);
            startup = new CheckBox { Name = "startup", Text = text.Pick("开机时启动(&W)", "Start with Windows (&W)"),
                Checked = current.Startup, AutoSize = true, TabIndex = 4, Margin = new Padding(0, 4, 0, 0) };
            configuration.Controls.Add(startup, 1, 5); configuration.SetColumnSpan(startup, 2);
            automaticUpdates = new CheckBox { Name = "automatic-updates",
                Text = text.Pick("自动检查更新(&U)", "Automatically check for updates (&U)"),
                Checked = current.AutomaticUpdateChecks, AutoSize = true, TabIndex = 5, Margin = new Padding(0, 14, 0, 0) };
            configuration.Controls.Add(automaticUpdates, 1, 6); configuration.SetColumnSpan(automaticUpdates, 2);
            updateHint = Paragraph("update-hint", text.Pick("启动时检查；安装前由你确认。", "Check on launch. You choose when to install."));
            updateHint.Font = new Font(Font.FontFamily, 9F); updateHint.ForeColor = SecondaryText;
            updateHint.Margin = new Padding(0, 5, 0, 0);
            configuration.Controls.Add(updateHint, 1, 7); configuration.SetColumnSpan(updateHint, 2);
            content.Controls.Add(configuration, 0, 2);

            setupSection = Table("setup", 1); setupSection.Dock = DockStyle.Top; setupSection.Visible = false;
            setupSection.Margin = new Padding(0, 20, 0, 0); setupSection.Padding = new Padding(14);
            setupSection.BackColor = SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(245, 247, 250);
            var setupTitle = Paragraph("setup-title", text.Pick("准备关屏功能", "Set up display off"));
            setupTitle.Font = new Font(Font, FontStyle.Bold);
            setupSection.Controls.Add(setupTitle, 0, 0);
            setupDetail = Paragraph("setup-detail", ""); setupDetail.MinimumSize = new Size(0, 34);
            setupSection.Controls.Add(setupDetail, 0, 1);
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = false, Margin = new Padding(0, 4, 0, 4) };
            prepareButton = Button("prepare", text.Pick("自动准备(&A)", "Prepare (&A)"), 0);
            cancelSetup = Button("cancel-prepare", text.Pick("取消准备", "Cancel setup"), 1);
            actions.Controls.AddRange(new Control[] { prepareButton, cancelSetup }); setupSection.Controls.Add(actions, 0, 2);
            details = new LinkLabel { Name = "details", AutoSize = true, TabIndex = 2, Margin = new Padding(0, 5, 0, 5) };
            StyleLink(details);
            setupSection.Controls.Add(details, 0, 3);
            diagnostics = new TextBox { Name = "diagnostics", ReadOnly = true, Multiline = true, WordWrap = false,
                ScrollBars = ScrollBars.Both, Dock = DockStyle.Top, Height = 100, Visible = false, TabIndex = 3,
                AccessibleName = text.Pick("诊断详情", "Diagnostic details") };
            setupSection.Controls.Add(diagnostics, 0, 4);
            displaySettings = new LinkLabel { Name = "display-settings", Text = text.Pick("高级：Windows 显示设置", "Advanced: Windows display settings"),
                AutoSize = true, Visible = false, TabIndex = 4, Margin = new Padding(0, 6, 0, 0) };
            StyleLink(displaySettings);
            setupSection.Controls.Add(displaySettings, 0, 5);
            content.Controls.Add(setupSection, 0, 3);

            var behavior = Paragraph("behavior", text.Pick("关屏后保护 3 秒，首次实体输入只负责亮屏。",
                "After a 3-second guard, the first physical input only wakes the display."));
            behavior.ForeColor = SecondaryText;
            behavior.Font = new Font(Font.FontFamily, 9F);
            behavior.Margin = new Padding(0, 24, 0, 18); content.Controls.Add(behavior, 0, 4);
            var buttons = new FlowLayoutPanel { Name = "buttons", FlowDirection = FlowDirection.RightToLeft, AutoSize = true,
                Dock = DockStyle.Top, WrapContents = false, Margin = new Padding(0, 6, 0, 0), TabIndex = 20 };
            cancelButton = Button("cancel", text.Pick("取消", "Cancel"), 1);
            saveButton = Button("save", text.Pick("保存(&S)", "Save (&S)"), 0);
            StylePrimaryButton(saveButton);
            buttons.Controls.AddRange(new Control[] { cancelButton, saveButton });
            var footer = Table("footer", 2); footer.Dock = DockStyle.Top; footer.TabIndex = 2;
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var versionRow = new FlowLayoutPanel { Name = "version-row", AutoSize = true, Dock = DockStyle.Fill,
                WrapContents = true, Margin = new Padding(0, 6, 10, 0), Padding = new Padding(0, 9, 0, 0), TabIndex = 0 };
            var versionLabel = new Label { Name = "version", AutoSize = true, ForeColor = SecondaryText,
                Font = new Font(Font.FontFamily, 9F), Text = "v" + (version ?? Application.ProductVersion),
                Margin = new Padding(0, 0, 12, 4) };
            updateCheck = new LinkLabel { Name = "check-updates", AutoSize = true, TabIndex = 0,
                Font = new Font(Font.FontFamily, 9F), Text = text.Pick("检查更新", "Check for updates"), Margin = new Padding(0, 0, 0, 4) };
            StyleLink(updateCheck); versionRow.Controls.AddRange(new Control[] { versionLabel, updateCheck });
            buttons.TabIndex = 1;
            footer.Controls.Add(versionRow, 0, 0); footer.Controls.Add(buttons, 1, 0);
            content.Controls.Add(footer, 0, 5);
            updateSection = Table("update-result", 2); updateSection.Dock = DockStyle.Top; updateSection.Visible = false; updateSection.TabIndex = 3;
            updateSection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); updateSection.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            updateSection.Margin = new Padding(0, 14, 0, 0);
            updateDetail = Paragraph("update-detail", ""); updateDetail.Font = new Font(Font.FontFamily, 9F); updateDetail.ForeColor = SecondaryText;
            updateDetail.AccessibleRole = AccessibleRole.StaticText;
            releasePage = new LinkLabel { Name = "release-page", AutoSize = true, Visible = false,
                Font = new Font(Font.FontFamily, 9F), Text = text.Pick("前往 GitHub 下载", "Download on GitHub"),
                Margin = new Padding(14, 3, 0, 3), TabIndex = 0 };
            StyleLink(releasePage); updateSection.Controls.Add(updateDetail, 0, 0); updateSection.Controls.Add(releasePage, 1, 0);
            content.Controls.Add(updateSection, 0, 6);
            viewport = new Panel { Name = "viewport", Dock = DockStyle.Fill, AutoScroll = true, TabStop = false };
            viewport.Controls.Add(content); Controls.Add(viewport);
            configuration.TabIndex = 0; setupSection.TabIndex = 1;
            AcceptButton = saveButton; CancelButton = cancelButton;
            // Setting CancelButton assigns Cancel automatically. Closing is gated below.
            cancelButton.DialogResult = DialogResult.None;
            defaults.Click += delegate { hotkey.SetBinding(HotkeyBinding.Default); SetError(""); };
            hotkey.BindingChanged += delegate { SetError(Localize(hotkey.Error)); };
            hotkey.GestureReleased += delegate { if (hotkey.Focused && hotkey.Error == null) defaults.Focus(); };
            saveButton.Click += delegate { SaveValues(); };
            cancelButton.Click += delegate { RequestClose(); };
            prepareButton.Click += delegate { StartPrepare(); };
            cancelSetup.Click += delegate { RequestCancel(false); };
            details.LinkClicked += delegate { expanded = !expanded; ApplySetup(); };
            displaySettings.LinkClicked += delegate { InvokeAction(this.openDisplaySettings); };
            updateCheck.LinkClicked += delegate { if (this.checkUpdates != null && !checkingUpdates && !Running) InvokeAction(this.checkUpdates); };
            releasePage.LinkClicked += delegate { if (this.openRelease != null && !Running) InvokeAction(this.openRelease); };
            automaticUpdates.CheckedChanged += delegate { UpdatePreferenceHint(); };
            FormClosing += ClosingDialog;
            Activated += delegate { active = true; };
            Deactivate += delegate { active = false; };
            content.Layout += delegate { FitHeight(); };
            Shown += delegate { timeout.Focus(); FitHeight(); };
            UpdatePreferenceHint(); ApplySetup();
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
            button.MinimumSize = new Size(96, 36); button.Padding = new Padding(10, 2, 10, 2);
            button.TabIndex = tab; button.Margin = new Padding(8, 0, 0, 0); button.DialogResult = DialogResult.None;
            return button;
        }
        private static Color SecondaryText { get { return SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(94, 106, 122); } }
        private static void StyleLink(LinkLabel link)
        {
            if (SystemInformation.HighContrast) return;
            link.LinkColor = Color.FromArgb(0, 93, 174);
            link.ActiveLinkColor = Color.FromArgb(0, 65, 126);
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
            table.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 9, 8, 18), TabIndex = control.TabIndex }, 0, row);
            control.Margin = new Padding(0, 2, 0, 16); table.Controls.Add(control, 1, row);
        }
        private void FitHeight()
        {
            if (sizing || IsDisposed) return;
            sizing = true;
            try {
                // Measure wrapped text at the actual window width, rather than
                // allowing PreferredSize to assume a wider, shorter panel.
                // Reserve the scrollbar width when measuring so its appearance
                // cannot oscillate between two wrapping heights during layout.
                int width = Math.Max(1, ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
                int height = content.GetPreferredSize(new Size(width, 0)).Height;
                Rectangle workArea = Screen.FromControl(this).WorkingArea;
                int available = workArea.Height - (Height - ClientSize.Height) - 32;
                int visibleHeight = Math.Min(height, Math.Max(300, available));
                viewport.AutoScrollMinSize = new Size(0, height);
                if (height > 0 && ClientSize.Height != visibleHeight) ClientSize = new Size(ClientSize.Width, visibleHeight);
                // Keep expanded diagnostics on screen. Intentionally off-screen
                // component previews do not intersect the work area and stay there.
                if (Visible && Bounds.IntersectsWith(workArea))
                    Location = new Point(Math.Max(workArea.Left, Math.Min(Left, workArea.Right - Width)),
                        Math.Max(workArea.Top, Math.Min(Top, workArea.Bottom - Height)));
            }
            finally { sizing = false; }
        }
        private string Localize(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            string[] parts = value.Split(new[] { " / " }, StringSplitOptions.None);
            return parts.Length > 1 ? (text.English ? parts[parts.Length - 1] : parts[0]) : value;
        }
        private void SetError(string value)
        {
            error.Text = Localize(value); error.Visible = !String.IsNullOrEmpty(error.Text); FitHeight();
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
            var value = new SettingsValues { TimeoutMinutes = minutes, Hotkey = hotkey.Binding,
                Language = language.SelectedIndex == 1 ? "en" : "zh-CN", Startup = startup.Checked,
                AutomaticUpdateChecks = automaticUpdates.Checked };
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
            try { action(); } catch (Exception ex) { SetError(ex.Message); }
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
            if (!Running) return;
            e.Cancel = true; DialogResult = DialogResult.None; handlingClose = true;
            try { RequestCancel(true); } finally { handlingClose = false; }
            // The callback may synchronously publish its final state.
            if (!Running && closeWhenSafe && IsHandleCreated) BeginInvoke(new Action(CloseCancelled));
        }

        internal void RefreshSetup(SetupPanelState state)
        {
            if (state == null) throw new ArgumentNullException("state");
            if (IsDisposed) return;
            setup = new SetupPanelState { Visible = state.Visible, Checking = state.Checking, Preparing = state.Preparing,
                CanPrepare = state.CanPrepare, Ready = state.Ready, Detail = state.Detail, Diagnostics = state.Diagnostics };
            prepareRequested = false;
            if (!Running) cancelRequested = false;
            ApplySetup();
            if (closeWhenSafe && !Running) CloseCancelled();
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
        private void UpdateActions()
        {
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
            configuration.Enabled = !running && !closeWhenSafe;
            saveButton.Enabled = !running && !closeWhenSafe && !saving;
            cancelButton.Enabled = !closeWhenSafe;
            cancelButton.Text = closeWhenSafe ? text.Pick("等待取消…", "Waiting…") : text.Pick("取消", "Cancel");
            setupSection.Visible = setup.Visible && !(setup.Ready && !running);
            setupDetail.Text = setup.Detail ?? "";
            if (prepareRequested) setupDetail.Text = text.Pick("正在准备，请稍候…", "Preparing. Please wait…");
            if (cancelRequested) setupDetail.Text += "\r\n" + text.Pick("已请求取消，等待处理结果…", "Cancellation requested; waiting for the result…");
            prepareButton.Text = running ? text.Pick("正在准备…", "Preparing…") : setup.Checking
                ? text.Pick("正在检查…", "Checking…") : text.Pick("自动准备(&A)", "Prepare (&A)");
            prepareButton.Enabled = !running && !setup.Checking && setup.CanPrepare && !closeWhenSafe;
            cancelSetup.Visible = running; cancelSetup.Enabled = running && !cancelRequested;
            details.Text = expanded ? text.Pick("收起诊断", "Hide diagnostics") : text.Pick("诊断详情", "Diagnostic details");
            diagnostics.Text = setup.Diagnostics ?? ""; diagnostics.Visible = displaySettings.Visible = expanded;
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
            TabStop = false; Height = 36; Width = input.Width + 20; Padding = new Padding(10, 4, 10, 4);
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
                ? Color.FromArgb(0, 95, 184) : Color.FromArgb(211, 218, 227);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = SurfaceButton.Outline(ClientRectangle, Math.Max(4, Font.Height / 3)))
            using (var pen = new Pen(border, ContainsFocus ? 2F : 1F)) e.Graphics.DrawPath(pen, path);
        }
    }

    internal sealed class SurfaceButton : Button
    {
        internal bool Primary;
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
            Color background = !Enabled ? Color.FromArgb(241, 243, 246) : Primary
                ? (pressed ? Color.FromArgb(0, 65, 132) : hovered ? Color.FromArgb(0, 80, 158) : Color.FromArgb(0, 95, 184))
                : (pressed ? Color.FromArgb(232, 238, 245) : hovered ? Color.FromArgb(245, 248, 252) : Color.White);
            Color foreground = !Enabled ? Color.FromArgb(122, 132, 146) : Primary ? Color.White : Color.FromArgb(36, 48, 62);
            using (var path = Outline(ClientRectangle, Math.Max(5, Font.Height / 3))) {
                using (var brush = new SolidBrush(background)) e.Graphics.FillPath(brush, path);
                if (!Primary) using (var pen = new Pen(Color.FromArgb(211, 218, 227))) e.Graphics.DrawPath(pen, path);
            }
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, foreground, flags);
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
