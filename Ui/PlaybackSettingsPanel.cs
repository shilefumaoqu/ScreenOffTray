using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ScreenOffTray.Playback;

namespace ScreenOffTray.Ui
{
    // Presentation only. Delegates read the coordinator's cache and save only the list.
    internal sealed class PlaybackSettingsPanel : TableLayoutPanel
    {
        // Only fonts constructed by this view belong here; inherited/system fonts are borrowed.
        private readonly List<Font> ownedFonts = new List<Font>();
        private readonly Dictionary<Font,float> fontPoints = new Dictionary<Font,float>(SettingsDialog.FontIdentity);
        private readonly UiText text;
        private readonly Action refreshAudio;
        private readonly Func<string[], string> saveWhitelist;
        private readonly ToolTip tips = new ToolTip();
        private readonly Label status, warning, saveStatus, whitelistCount;
        private readonly LinkLabel warningToggle;
        private readonly TextBox warningDetails;
        private bool warningExpanded;
        private readonly TableLayoutPanel sources;
        private readonly Panel sourcesViewport;
        private readonly ComboBox whitelist;
        private readonly Button remove;
        private readonly CheckBox limitEnabled;
        private readonly MintNumberBox limitMinutes;
        private readonly MintStepper limitStepper;
        private readonly Label limitHint;
        private readonly ApplicationIconCache iconCache = new ApplicationIconCache();
        private readonly Image fallbackIcon = SystemIcons.Application.ToBitmap();
        private readonly PictureBox selectedIcon;
        private string[] paths = new string[0];
        private AudioSource[] currentSources = new AudioSource[0];
        private string whitelistKey, sourcesKey;
        private bool savingList;
        private bool whitelistKnown = true;

        internal PlaybackSettingsPanel(UiText text, bool enabled, int minutes,
            Action refreshAudio, Func<string[], string> saveWhitelist)
        {
            this.text = text; this.refreshAudio = refreshAudio; this.saveWhitelist = saveWhitelist;
            Font = OwnFont(new Font(text.English ? "Segoe UI" : "Microsoft YaHei UI", 11F)); ForeColor = MintTheme.Ink;
            Name = "playback-settings"; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            ColumnCount = 1; ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Margin = new Padding(0); Padding = new Padding(0); TabStop = false;
            var titleRow = Row("playback-heading"); titleRow.Margin = new Padding(0,0,0,12);
            var title = Label("playback-title", text.Pick("播放保护", "Playback protection"));
            title.Font = OwnFont(new Font(Font.FontFamily,14.25F,FontStyle.Bold)); title.ForeColor = ForeColor; titleRow.Controls.Add(title, 0, 0);
            var refresh = new LinkLabel { Name = "refresh-audio", Text = text.Pick("刷新", "Refresh"), AutoSize = true, Margin = new Padding(0,3,0,3), TabIndex = 0,
                LinkColor = MintTheme.Green, ActiveLinkColor = MintTheme.Green, VisitedLinkColor = MintTheme.Green, LinkBehavior = LinkBehavior.HoverUnderline };
            refresh.Enabled = refreshAudio != null;
            refresh.LinkClicked += delegate {
                try { if (this.refreshAudio != null) this.refreshAudio(); }
                catch (Exception ex) { ShowReadError(ex.Message); }
            };
            titleRow.Controls.Add(refresh, 1, 0); Controls.Add(titleRow, 0, 0);
            status = Label("playback-status", text.Pick("等待播放状态…", "Waiting for playback status…"));
            status.Margin = new Padding(0, 7, 0, 3); Controls.Add(status, 0, 2);
            sources = new TableLayoutPanel { Name = "playing-sources", ColumnCount = 3, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Margin = new Padding(0) };
            sources.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48)); sources.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); sources.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            sourcesViewport = new Panel { Name = "playing-viewport", Dock = DockStyle.Top, Height = 36, AutoScroll = true,
                Margin = new Padding(0, 0, 0, 16), TabStop = false };
            sourcesViewport.Controls.Add(sources); Controls.Add(sourcesViewport, 0, 1);
            var listHeading = Row("whitelist-heading"); listHeading.Padding = new Padding(0,8,0,0); listHeading.Margin = new Padding(0,0,0,9);
            listHeading.Paint += delegate(object sender,PaintEventArgs e) { using(var pen = new Pen(MintTheme.Line)) e.Graphics.DrawLine(pen,0,0,listHeading.Width-1,0); };
            whitelistCount = Label("whitelist-count", ""); whitelistCount.Font = OwnFont(new Font(Font,FontStyle.Bold)); whitelistCount.ForeColor = ForeColor; listHeading.Controls.Add(whitelistCount, 0, 0);
            var instant = Label("whitelist-instant", text.Pick("即时保存", "Saved instantly")); instant.ForeColor = MintTheme.Muted; instant.Font = OwnFont(new Font(Font.FontFamily,10F)); listHeading.Controls.Add(instant,1,0);
            Controls.Add(listHeading, 0, 3);
            var listRow = Row("whitelist-line"); listRow.Margin = new Padding(0, 0, 0, 5);
            whitelist = new MintComboBox { Name = "whitelist-picker", Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList, IntegralHeight = false, MaxDropDownItems = 8,
                DropDownHeight = 200, Margin = new Padding(0, 3, 10, 3), TabIndex = 0,
                AccessibleName = text.Pick("白名单中的程序", "Allowed applications"),
                AccessibleDescription = text.Pick("方向键选择程序，Alt+下箭头展开；移除仅影响选中路径。", "Use arrow keys to select, Alt+Down to expand. Remove affects only the selected path.") };
            whitelist.SelectedIndexChanged += delegate { UpdateSelection(); };
            whitelist.DropDownClosed += delegate { RenderWhitelist(); };
            whitelist.LostFocus += delegate { RenderWhitelist(); };
            remove = ActionButton("remove-whitelist", text.Pick("移除", "Remove"), 1);
            remove.Click += delegate {
                var choice = whitelist.SelectedItem as Choice;
                if (choice != null && choice.Path != null) Persist(paths.Where(p => !Same(p, choice.Path)).ToArray());
            };
            var selection = new TableLayoutPanel { Name = "whitelist-selection", ColumnCount = 2, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0,0,9,0) };
            selection.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,28)); selection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            selectedIcon = new PictureBox { Name = "whitelist-icon", Size = new Size(20,20), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0,10,4,0), TabStop = false };
            selection.Controls.Add(selectedIcon,0,0); whitelist.Margin = new Padding(0,0,0,0); selection.Controls.Add(whitelist,1,0);
            listRow.Controls.Add(selection, 0, 0); listRow.Controls.Add(remove, 1, 0); Controls.Add(listRow, 0, 4);
            var listHint = Label("playback-hint",text.Pick("名单中的程序播放音频时，暂缓自动关屏。", "Allowed apps defer auto off while playing audio."));
            listHint.Font = OwnFont(new Font(Font.FontFamily,10F)); listHint.Margin = new Padding(0,0,0,8); Controls.Add(listHint,0,5);
            var capRow = new FlowLayoutPanel { Name = "playback-limit-row", AutoSize = true, Dock = DockStyle.Top,
                WrapContents = false, Margin = new Padding(0), TabIndex = 3 };
            limitEnabled = new MintCheckBox { Name = "playback-limit-enabled", Text = text.Pick("保护上限", "Protection limit"),
                Checked = enabled, AutoSize = true, ForeColor = ForeColor, Margin = new Padding(0, 5, 8, 0), TabIndex = 0 };
            limitMinutes = new MintNumberBox { Name = "playback-limit-minutes", Minimum = 1, Maximum = 1440,
                Value = minutes >= 1 && minutes <= 1440 ? minutes : 60, Width = 74,
                AccessibleName = text.Pick("播放保护上限分钟数，1至1440", "Playback limit in minutes, 1 to 1440") };
            limitStepper = new MintStepper(limitMinutes) { Width = 158, TabIndex = 1, Enabled = enabled };
            limitEnabled.CheckedChanged += delegate { limitStepper.Enabled = limitEnabled.Checked; UpdateLimitHint(); };
            limitMinutes.TextChanged += delegate { UpdateLimitHint(); };
            var capLine = Row("protection-limit-line"); capLine.Margin = new Padding(0,12,0,0);
            capLine.Controls.Add(limitEnabled,0,0); capRow.Controls.Add(limitStepper);
            capRow.Controls.Add(new System.Windows.Forms.Label { AutoSize = true, Text = text.Pick("分钟", "min"), Margin = new Padding(12, 9, 0, 0) });
            capLine.Controls.Add(capRow,1,0); Controls.Add(capLine, 0, 6);
            limitHint = Label("playback-limit-hint", ""); limitHint.Font = OwnFont(new Font(Font.FontFamily,10F)); limitHint.Margin = new Padding(0,9,0,0); Controls.Add(limitHint,0,7); UpdateLimitHint();
            warning = Label("playback-warning", ""); warning.Visible = false; warning.AccessibleRole = AccessibleRole.Alert;
            warning.AutoSize = false; warning.Height = 38; warning.AutoEllipsis = true;
            warning.Font = OwnFont(new Font(Font.FontFamily,10F));
            saveStatus = Label("whitelist-save-status", ""); saveStatus.Visible = false; saveStatus.AccessibleRole = AccessibleRole.Alert;
            warningToggle = new LinkLabel { Name = "playback-warning-toggle", Text = text.Pick("诊断详情", "Diagnostic details"), AutoSize = true, Visible = false,
                Margin = new Padding(0,0,0,5), LinkColor = MintTheme.Green, ActiveLinkColor = MintTheme.Green };
            warningDetails = new TextBox { Name = "playback-warning-details", AutoSize = false, ReadOnly = true, Multiline = true, WordWrap = true,
                ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Top, Height = 96, Visible = false, Margin = new Padding(0,0,0,6),
                AccessibleName = text.Pick("完整播放诊断", "Full playback diagnostics") };
            warningDetails.Font = OwnFont(new Font(Font.FontFamily,10F));
            warningToggle.LinkClicked += delegate {
                using (SettingsDpiContext.EnterUi()) {
                    warningExpanded = !warningExpanded; warningDetails.Visible = warningExpanded;
                    warningToggle.Text = warningExpanded ? text.Pick("收起诊断", "Hide diagnostics") : text.Pick("诊断详情", "Diagnostic details");
                }
            };
            Controls.Add(warning, 0, 8); Controls.Add(warningToggle,0,9); Controls.Add(warningDetails,0,10); Controls.Add(saveStatus, 0, 11);
            iconCache.Changed += IconsChanged;
            RenderWhitelist(); RenderSources(null);
            titleRow.TabIndex = 0; sourcesViewport.TabIndex = 1; listRow.TabIndex = 2;
        }

        private Font OwnFont(Font font) { ownedFonts.Add(font); fontPoints.Add(font,font.Size); return font; }
        internal void ApplyFontDpi(int dpi,float rasterDpi)
        {
            SettingsDialog.RescaleOwnedFonts(this,ownedFonts,fontPoints,dpi/rasterDpi);
            // Rebuild from logical geometry once the whole tree has finished scaling.
            // Re-scaling rounded physical bounds (120 -> 144) otherwise differs by a
            // pixel from a later row built directly at 144 DPI.
            sourcesKey = null; RenderSourcesFromCache();
        }
        private Label Label(string name, string value)
        {
            return new Label { Name = name, Text = value, AutoSize = true, Dock = DockStyle.Top,
                Margin = new Padding(0, 3, 0, 3), ForeColor = SystemInformation.HighContrast ? SystemColors.ControlText : MintTheme.Muted };
        }
        private static TableLayoutPanel Row(string name)
        {
            var row = new TableLayoutPanel { Name = name, ColumnCount = 2, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Margin = new Padding(0), TabStop = false };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); return row;
        }
        private Button ActionButton(string name, string caption, int tab)
        {
            Button button = SystemInformation.HighContrast ? new Button() : new SurfaceButton();
            button.Name = name; button.Text = caption; button.AutoSize = true;
            button.MinimumSize = new Size(83, 39); button.Padding = new Padding(8, 2, 8, 2);
            button.Margin = new Padding(0); button.TabIndex = tab; button.AccessibleName = caption;
            return button;
        }
        internal bool LimitEnabled { get { return limitEnabled.Checked; } }
        internal bool TryReadLimit(out int minutes, out string error)
        {
            error = null;
            if (!limitEnabled.Checked) { minutes = (int)limitMinutes.Value; return true; }
            if (Int32.TryParse(limitMinutes.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out minutes) && minutes >= 1 && minutes <= 1440) return true;
            error = text.Pick("播放保护上限请输入 1–1440 分钟。", "Enter a playback limit of 1–1440 minutes."); return false;
        }
        internal void RefreshState(PlaybackPanelState state)
        {
            using (SettingsDpiContext.EnterUi()) {
            if (IsDisposed) return;
            if (state == null) { ShowReadError(text.Pick("播放状态暂不可用", "Playback status is unavailable")); return; }
            currentSources = state.Audio == null || state.Audio.Sources == null ? new AudioSource[0] : state.Audio.Sources.Where(s => s != null).ToArray();
            if (state.AppliedOptions != null) {
                paths = Normalize(state.AppliedOptions.WhitelistPaths);
                whitelistKnown = state.AppliedOptions.WhitelistKnown;
            }
            // Applied options never reset the in-progress checkbox or numeric draft.
            RenderWhitelist(); RenderSources(state.Audio);
            AutoOffPauseReason reason = state.Timing.PauseReason;
            status.Text = reason == AutoOffPauseReason.UserPause ? text.Pick("自动关屏已暂停", "Auto off is paused") :
                state.Timing.PlaybackLimitReached && !state.Timing.Blocked ? text.Pick("已达保护上限", "Protection limit reached") :
                reason == AutoOffPauseReason.PlaybackSettingsUnknown ? text.Pick("白名单读取失败，自动关屏已暂停", "Whitelist unavailable. Auto off is paused") :
                reason == AutoOffPauseReason.DisplayRequired ? text.Pick("系统正在请求保持显示", "Windows is keeping the display on") :
                reason == AutoOffPauseReason.Playback ? (String.IsNullOrWhiteSpace(state.Timing.SourceName) ? text.Pick("名单程序", "Allowed app") : state.Timing.SourceName) + text.Pick("正在播放，暂缓自动关屏", " is playing; auto off is on hold") :
                reason == AutoOffPauseReason.StateUnknown || reason == AutoOffPauseReason.AudioUnknown ? text.Pick("暂时无法确认播放状态", "Playback state is uncertain") : "";
            status.Visible = reason != AutoOffPauseReason.Playback && !String.IsNullOrEmpty(status.Text);
            var messages = new List<string>();
            if (!String.IsNullOrEmpty(state.SettingsWarning)) messages.Add(Localize(state.SettingsWarning));
            if (state.Audio == null || !state.Audio.Known) messages.Add(text.Pick("暂时无法读取播放状态；可点击刷新重试。", "Playback state unavailable. Use Refresh to retry."));
            if (state.Audio != null && state.Audio.UnidentifiedSourceCount > 0) messages.Add(text.Pick("另有无法识别的播放来源，不能加入白名单。", "Other playing sources could not be identified and cannot be added."));
            if (state.Audio != null && !String.IsNullOrEmpty(state.Audio.Warning)) messages.Add(Localize(state.Audio.Warning));
            string summary = state.Audio == null || !state.Audio.Known ? text.Pick("播放状态读取失败，请展开查看原因。", "Playback state unavailable. Expand for the cause.") :
                state.Audio.UnidentifiedSourceCount > 0 ? text.Pick("有 ", "There are ") + state.Audio.UnidentifiedSourceCount + text.Pick(" 个来源无法识别，不能加入白名单。", " unidentified sources; they cannot be added.") :
                text.Pick("播放保护存在诊断信息，请展开查看。", "Playback diagnostics available. Expand for details.");
            SetWarning(summary,String.Join("\r\n", messages));
            }
        }
        private string FormatIdle(uint milliseconds)
        {
            return (milliseconds / 60000).ToString(CultureInfo.CurrentCulture) + text.Pick(" 分 ", "m ") +
                ((milliseconds / 1000) % 60).ToString("00", CultureInfo.CurrentCulture) + text.Pick(" 秒", "s");
        }
        internal void ShowReadError(string message)
        {
            using (SettingsDpiContext.EnterUi()) {
            audioKnown = false; currentSources = new AudioSource[0]; sourcesKey = null; RenderSourcesFromCache();
            status.Text = text.Pick("暂时无法确认播放状态", "Playback state is uncertain"); status.Visible = true;
            SetWarning(text.Pick("播放状态读取失败，请展开查看原因。", "Playback state unavailable. Expand for the cause."),text.Pick("读取失败：", "Read failed: ") + Localize(message));
            }
        }
        private void SetWarning(string summary,string detail)
        {
            bool present = !String.IsNullOrEmpty(detail);
            warning.Text = summary; warning.AccessibleDescription = detail; warning.Visible = present;
            tips.SetToolTip(warning,detail);
            // Preserve selection/scroll while the same snapshot is refreshed every second.
            if (warningDetails.Text != detail) warningDetails.Text = detail;
            warningToggle.Visible = present; warningDetails.Visible = present && warningExpanded;
        }
        private string Localize(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            string[] parts = value.Split(new[] { " / " }, StringSplitOptions.None);
            return parts.Length > 1 ? (text.English ? parts[parts.Length - 1] : parts[0]) : value;
        }
        private static bool Same(string a, string b) { return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        private static string[] Normalize(string[] values)
        {
            return (values ?? new string[0]).Select(ExeIdentity.Normalize).Where(p => p != null).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        private string NameFor(string path)
        {
            var source = currentSources.FirstOrDefault(s => Same(ExeIdentity.Normalize(s.ExePath), path));
            return source != null && !String.IsNullOrWhiteSpace(source.DisplayName) ? source.DisplayName : Path.GetFileName(path);
        }
        private sealed class Choice
        {
            internal readonly string Path; private readonly string caption;
            internal Choice(string path, string caption) { Path = path; this.caption = caption; }
            public override string ToString() { return caption; }
        }
        private void RenderWhitelist(bool force = false)
        {
            using (SettingsDpiContext.EnterUi()) {
            if (IsDisposed) return;
            string[] names = paths.Select(NameFor).ToArray();
            string key = (whitelistKnown ? "known:" : "unknown:") + String.Join("\n", paths.Select((p, i) => p + "\u001f" + names[i]));
            if (whitelistKey == key || (!force && (whitelist.Focused || whitelist.DroppedDown))) return;
            var previous = whitelist.SelectedItem as Choice;
            int oldIndex = whitelist.SelectedIndex;
            whitelist.BeginUpdate();
            try {
                whitelist.Items.Clear();
                if (paths.Length == 0) whitelist.Items.Add(new Choice(null, whitelistKnown
                    ? text.Pick("尚未添加程序", "No apps added") : text.Pick("名单暂不可用", "Whitelist unavailable")));
                for (int i = 0; i < paths.Length; i++) {
                    bool duplicateName = names.Count(n => Same(n, names[i])) > 1;
                    whitelist.Items.Add(new Choice(paths[i], names[i] + (duplicateName ? " — " + paths[i] : "")));
                }
                int kept = previous == null ? -1 : Array.FindIndex(paths, p => Same(p, previous.Path));
                whitelist.SelectedIndex = kept >= 0 ? kept : Math.Max(0, Math.Min(oldIndex, whitelist.Items.Count - 1));
                whitelistKey = key;
                whitelistCount.Text = text.Pick("白名单", "Allowlist") + (whitelistKnown ? " (" + paths.Length + ")" : "");
                whitelist.DropDownWidth = Math.Min(900, Math.Max(whitelist.Width, paths.Length == 0 ? 200 :
                    whitelist.Items.Cast<object>().Max(o => TextRenderer.MeasureText(o.ToString(), whitelist.Font).Width) + SystemInformation.VerticalScrollBarWidth + 24));
            } finally { whitelist.EndUpdate(); }
            UpdateSelection();
            }
        }
        private void UpdateSelection()
        {
            using (SettingsDpiContext.EnterUi()) {
            var choice = whitelist.SelectedItem as Choice;
            selectedIcon.Tag = choice == null ? null : choice.Path; RefreshPicture(selectedIcon);
            bool hasPath = choice != null && choice.Path != null;
            selectedIcon.Visible = hasPath;
            ((TableLayoutPanel)selectedIcon.Parent).ColumnStyles[0].Width = hasPath ? SourceHeight(28) : 0;
            remove.Enabled = saveWhitelist != null && !savingList && choice != null && choice.Path != null;
            tips.SetToolTip(whitelist, choice == null ? null : choice.Path);
            whitelist.AccessibleDescription = choice != null && choice.Path != null ? choice.Path : whitelistKnown
                ? text.Pick("尚未添加程序", "No apps added") : text.Pick("名单暂不可用", "Whitelist unavailable");
            }
        }
        private void Persist(string[] candidate)
        {
            if (savingList || saveWhitelist == null) return;
            savingList = true;
            try {
                string error = saveWhitelist((string[])candidate.Clone());
                if (error != null) { SaveFailure(error); return; }
                paths = (string[])candidate.Clone(); whitelistKnown = true;
                RenderWhitelist(true); sourcesKey = null; RenderSourcesFromCache();
                saveStatus.Text = text.Pick("白名单已保存", "Allowlist saved"); saveStatus.ForeColor = ForeColor; saveStatus.Visible = true;
            } catch (Exception ex) { SaveFailure(ex.Message); }
            finally { savingList = false; UpdateSelection(); }
        }
        private void SaveFailure(string error)
        {
            saveStatus.Text = (whitelistKnown
                ? text.Pick("保存失败，原白名单仍有效：", "Save failed. Previous allowlist retained: ")
                : text.Pick("名单未保存，自动关屏仍暂停：", "Whitelist not saved. Auto off remains paused: ")) +
                (String.IsNullOrWhiteSpace(error) ? text.Pick("请重试", "Please retry") : Localize(error));
            saveStatus.ForeColor = SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(166, 38, 24); saveStatus.Visible = true;
        }
        // WinForms scales the existing child tree, but controls created by a
        // later audio/list refresh must receive the same accumulated factors.
        private SizeF sourceScale = new SizeF(1F,1F);
        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        {
            sourceScale = new SizeF(sourceScale.Width*factor.Width,sourceScale.Height*factor.Height);
            base.ScaleControl(factor,specified);
        }
        private int SourceHeight(int logical) { return (int)Math.Round(logical*sourceScale.Height); }
        private bool audioKnown;
        private void RenderSources(AudioSnapshot audio) { audioKnown = audio != null && audio.Known; RenderSourcesFromCache(); }
        private void RenderSourcesFromCache()
        {
            using (SettingsDpiContext.EnterUi()) {
            string key = audioKnown + "\n" + String.Join("\n", currentSources.Select(s => s.ExePath + "\u001f" + s.DisplayName)) + "\n" + String.Join("\n", paths);
            if (sourcesKey == key) return;
            sourcesKey = key;
            sources.SuspendLayout();
            try {
                foreach (Control child in sources.Controls.Cast<Control>().ToArray()) { tips.SetToolTip(child, null); child.Dispose(); }
                sources.Controls.Clear(); sources.RowStyles.Clear(); sources.RowCount = 0;
                var valid = audioKnown ? currentSources.GroupBy(s => ExeIdentity.Normalize(s.ExePath) ?? s.DisplayName).Select(g => g.First()).ToArray() : new AudioSource[0];
                if (valid.Length == 0) {
                    var empty = Label("playing-empty", audioKnown ? text.Pick("暂无正在播放的程序", "No apps playing") : text.Pick("暂时无法确认播放状态", "Playback state is uncertain")); empty.Margin = new Padding(0); empty.AutoSize = false; empty.Dock = DockStyle.Fill; empty.TextAlign = ContentAlignment.MiddleCenter; empty.MinimumSize = new Size(0,48); empty.Scale(sourceScale); sources.Controls.Add(empty,0,0); sources.SetColumnSpan(empty,3);
                }
                for (int i = 0; i < valid.Length; i++) {
                    AudioSource source = valid[i]; string path = ExeIdentity.Normalize(source.ExePath);
                    var name = Label("playing-source-" + i, String.IsNullOrWhiteSpace(source.DisplayName) ? (path == null ? text.Pick("未知来源", "Unknown source") : Path.GetFileName(path)) : source.DisplayName);
                    var picture = new PictureBox { Name = "playing-icon-" + i, Size = new Size(35,35), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0,6,13,7), TabStop = false, Tag = path };
                    picture.Scale(sourceScale); RefreshPicture(picture); sources.Controls.Add(picture,0,i);
                    name.Font = Font; name.ForeColor = ForeColor; name.AutoSize = false; name.AutoEllipsis = true; name.Dock = DockStyle.Top; name.TextAlign = ContentAlignment.MiddleLeft; name.Height = 24; name.Margin = new Padding(0); name.AccessibleDescription = path;
                    tips.SetToolTip(name, name.Text + (path == null ? "" : "\n" + path));
                    var copy = new TableLayoutPanel { Font = Font, Name = "playing-copy-"+i, ColumnCount = 1, RowCount = 2, AutoSize = false, Height = 48, Dock = DockStyle.Fill, Margin = new Padding(0,0,8,0) };
                    copy.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); copy.RowStyles.Add(new RowStyle(SizeType.Percent,50)); copy.RowStyles.Add(new RowStyle(SizeType.Percent,50));
                    var caption = Label("playing-caption-"+i,text.Pick("正在播放", "Playing")); caption.Font = limitHint.Font; caption.Margin = new Padding(0); caption.Dock = DockStyle.Fill; caption.TextAlign = ContentAlignment.MiddleLeft;
                    copy.Controls.Add(name,0,0); copy.Controls.Add(caption,0,1); copy.Scale(sourceScale); sources.Controls.Add(copy,1,i);
                    bool added = path != null && paths.Any(p => Same(p, path));
                    var add = ActionButton("add-whitelist-" + i, added ? text.Pick("已加入", "Added") : text.Pick("加入", "Add"), i);
                    add.Enabled = path != null && !added && saveWhitelist != null;
                    add.AccessibleName = add.Text + " " + name.Text;
                    add.Click += delegate { if (path != null && !paths.Any(p => Same(p, path))) Persist(paths.Concat(new[] { path }).ToArray()); };
                    add.Font = Font;
                    var surface = add as SurfaceButton; if (surface != null) surface.Primary = true;
                    add.Margin = new Padding(0,4,0,5); add.Scale(sourceScale); sources.Controls.Add(add, 2, i);
                }
                // Bound the live source list; whitelist size never changes panel height.
                int rowHeight = Math.Max(Font.Height + SourceHeight(12), SourceHeight(48));
                sourcesViewport.Height = rowHeight * Math.Max(1, Math.Min(3, valid.Length));
            } finally { sources.ResumeLayout(true); }
            }
        }
        internal void AdaptWidth()
        {
            int logical = (int)Math.Round(Width/Math.Max(.01F,sourceScale.Width));
            // Keep the cap on one row when its native checkbox fits; stack the
            // two groups for longer translations instead of shrinking the type.
            var capLine = (TableLayoutPanel)Controls["protection-limit-line"];
            var capRow = Controls.Find("playback-limit-row",true).FirstOrDefault() as FlowLayoutPanel;
            if (capRow == null) return;
            int stepWidth = logical < (text.English ? 370 : 310) ? 108 : 158;
            bool stack = logical < (text.English ? 300 : 235);
            limitStepper.Width = SourceHeight(stepWidth);
            if ((capLine.ColumnCount == 1) != stack) {
                capLine.SuspendLayout(); capLine.Controls.Remove(limitEnabled); capLine.Controls.Remove(capRow);
                capLine.ColumnCount = stack ? 1 : 2; capLine.RowCount = stack ? 2 : 1; capLine.ColumnStyles.Clear(); capLine.RowStyles.Clear();
                capLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); if (!stack) capLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                capLine.Controls.Add(limitEnabled,0,0); capLine.Controls.Add(capRow,stack ? 0 : 1,stack ? 1 : 0); capLine.ResumeLayout(true);
            }
        }
        private void UpdateLimitHint()
        {
            if (limitHint == null) return;
            limitHint.Text = limitEnabled.Checked ? text.Pick("连续 " + limitMinutes.Text + " 分钟无实体操作，即使仍在播放也会关屏。", "No physical input for " + limitMinutes.Text + " min: protection ends.")
                : text.Pick("播放期间持续保护，直到播放停止或手动关屏。", "Protected until playback stops or you turn off the display manually.");
        }
        private void RefreshPicture(PictureBox picture)
        {
            if (picture.IsDisposed) return;
            picture.Image = iconCache.GetOrQueue(picture.Tag as string, Math.Max(16,picture.Width)) ?? fallbackIcon;
        }
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e); if (Visible) IconsChanged(this,EventArgs.Empty);
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e); IconsChanged(this,EventArgs.Empty);
        }
        private void IconsChanged(object sender, EventArgs e)
        {
            if (IsDisposed || Disposing || !IsHandleCreated || !Visible) return;
            try { BeginInvoke(new Action(delegate {
                using (SettingsDpiContext.EnterUi()) {
                if (IsDisposed || Disposing || !Visible) return;
                RefreshPicture(selectedIcon);
                foreach (Control child in sources.Controls) { var picture = child as PictureBox; if (picture != null) RefreshPicture(picture); }
                }
            })); } catch (InvalidOperationException) { }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { iconCache.Changed -= IconsChanged; tips.Dispose(); }
            base.Dispose(disposing); // PictureBoxes stop borrowing before cache images are released.
            if (disposing) {
                iconCache.Dispose(); fallbackIcon.Dispose();
                foreach (Font font in ownedFonts) font.Dispose();
                ownedFonts.Clear();
                fontPoints.Clear();
            }
        }
    }
}
