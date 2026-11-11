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
    internal sealed class PlaybackSettingsPanel : MintCardLayoutPanel
    {
        // Only fonts constructed by this view belong here; inherited/system fonts are borrowed.
        private readonly List<Font> ownedFonts = new List<Font>();
        private readonly Dictionary<Font,float> fontPoints = new Dictionary<Font,float>(SettingsDialog.FontIdentity);
        private UiText text;
        private PlaybackPanelState lastState;
        private string lastReadError,lastSaveError;
        private bool listSaved;
        private readonly Action refreshAudio;
        private readonly Func<string[], string> saveWhitelist;
        private readonly ToolTip tips = new ToolTip();
        private readonly Label status, warning, saveStatus, whitelistCount, instant;
        private readonly LinkLabel warningToggle;
        private readonly TextBox warningDetails;
        private bool warningExpanded;
        private readonly TableLayoutPanel sources;
        private readonly Panel sourcesViewport;
        private readonly MintComboBox whitelist;
        private readonly Button remove;
        private readonly CheckBox limitEnabled;
        private readonly MintNumberBox limitMinutes;
        private readonly MintStepper limitStepper;
        private readonly Label limitHint;
        private Font sourceNameFont;
        private readonly int sourceNameFontIndex;
        private readonly ApplicationIconCache iconCache = new ApplicationIconCache();
        private readonly Image fallbackIcon = SystemIcons.Application.ToBitmap();
        private readonly PictureBox selectedIcon;
        private string[] paths = new string[0];
        private AudioSource[] currentSources = new AudioSource[0];
        private string whitelistKey, sourcesKey;
        private bool savingList;
        private bool whitelistKnown = true;
        private bool iconRefreshQueued;
        private int iconHandleGeneration;

        internal PlaybackSettingsPanel(UiText initialText, bool enabled, int minutes,
            Action refreshAudio, Func<string[], string> saveWhitelist) : base(1)
        {
            this.text = initialText; this.refreshAudio = refreshAudio; this.saveWhitelist = saveWhitelist;
            Font = OwnFont(new Font(text.English ? "Segoe UI" : "Microsoft YaHei UI",15.5F)); ForeColor = MintTheme.Ink;
            sourceNameFontIndex=ownedFonts.Count;
            sourceNameFont = OwnFont(new Font(Font.FontFamily,18F,FontStyle.Bold));
            Name = "playback-settings"; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Margin = new Padding(0); Padding = new Padding(0); TabStop = false;
            var titleRow = Row("playback-heading"); titleRow.Margin = new Padding(0,0,0,6);
            var title = new MintIconLabel { Name="playback-title", Text=text.Pick("播放保护", "Playback protection"), IconKind=MintIconKind.Shield,
                AutoSize=true, ForeColor=ForeColor, MinimumSize=new Size(0,28), Margin=new Padding(0) };
            title.Font = OwnFont(new Font(Font.FontFamily,18F,FontStyle.Bold)); title.ForeColor = ForeColor; title.MinimumSize = new Size(0,28); title.Margin = new Padding(0); titleRow.Controls.Add(title, 0, 0);
            var refresh = new MintRefreshLink { Name = "refresh-audio", Text = text.Pick("刷新", "Refresh"), AutoSize = true, Font = OwnFont(new Font(Font.FontFamily,14F)), Margin = new Padding(0,0,0,0), TabIndex = 0,
                LinkColor = MintTheme.Green, ActiveLinkColor = MintTheme.Green, VisitedLinkColor = MintTheme.Green, LinkBehavior = LinkBehavior.HoverUnderline };
            refresh.Enabled = refreshAudio != null;
            refresh.LinkClicked += delegate {
                try { if (this.refreshAudio != null) this.refreshAudio(); }
                catch (Exception ex) { ShowReadError(ex.Message); }
            };
            titleRow.Controls.Add(refresh, 1, 0); Controls.Add(titleRow, 0, 0);
            status = new MintStatusLabel { Name="playback-status", Text=text.Pick("等待播放状态…", "Waiting for playback status…"), Dock=DockStyle.Top,
                ForeColor=MintTheme.Green, Margin=new Padding(0,0,0,10) };
            Controls.Add(status, 0, 2);
            sources = new TableLayoutPanel { Name = "playing-sources", ColumnCount = 3, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, BackColor=Color.Transparent, Margin = new Padding(0) };
            sources.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76)); sources.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); sources.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            sourcesViewport = new Panel { Name = "playing-viewport", BackColor=Color.Transparent, Dock = DockStyle.Top, Height = 36, AutoScroll = true,
                Margin = new Padding(0, 0, 0, 4), TabStop = false };
            sourcesViewport.Controls.Add(sources); Controls.Add(sourcesViewport, 0, 1);
            var listHeading = Row("whitelist-heading"); listHeading.Padding = new Padding(0,4,0,0); listHeading.Margin = new Padding(0,0,0,5);
            // The status banner, rather than a divider, separates playback and the list.
            whitelistCount = new MintIconLabel { Name="whitelist-count", Text="", IconKind=MintIconKind.Stack, AutoSize=true, ForeColor=ForeColor, Margin=new Padding(0) };
            whitelistCount.Font = OwnFont(new Font(Font.FontFamily,17F,FontStyle.Bold)); listHeading.Controls.Add(whitelistCount, 0, 0);
            instant = Label("whitelist-instant", text.Pick("即时保存", "Saved instantly")); instant.ForeColor = MintTheme.Muted; instant.Margin = new Padding(0); instant.Font = OwnFont(new Font(Font.FontFamily,14F)); listHeading.Controls.Add(instant,1,0);
            instant.AccessibleRole = AccessibleRole.StatusBar;
            Controls.Add(listHeading, 0, 3);
            var listRow = Row("whitelist-line"); listRow.Margin = new Padding(0, 0, 0, 3);
            whitelist = new MintComboBox { Font = OwnFont(new Font(Font.FontFamily,16F)), Name = "whitelist-picker", Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList, IntegralHeight = false, MaxDropDownItems = 8,
                DropDownHeight = 200, Margin = new Padding(0, 3, 10, 3), TabIndex = 0,
                AccessibleName = text.Pick("白名单中的程序", "Allowed applications"),
                AccessibleDescription = text.Pick("方向键选择程序，Alt+下箭头展开；移除仅影响选中路径。", "Use arrow keys to select, Alt+Down to expand. Remove affects only the selected path.") };
            whitelist.SelectedIndexChanged += delegate { UpdateSelection(); };
            whitelist.DropDownClosed += delegate { RenderWhitelist(); };
            whitelist.LostFocus += delegate { RenderWhitelist(); };
            remove = ActionButton("remove-whitelist", text.Pick("移除", "Remove"), 1);
            remove.Font = OwnFont(new Font(Font.FontFamily,16F));
            remove.Click += delegate {
                var choice = whitelist.SelectedItem as Choice;
                if (choice != null && choice.Path != null) Persist(paths.Where(p => !Same(p, choice.Path)).ToArray());
            };
            var selection = new TableLayoutPanel { Name = "whitelist-selection", BackColor=Color.Transparent, ColumnCount = 2, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0,0,9,0) };
            selection.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,28)); selection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            selectedIcon = new PictureBox { Name = "whitelist-icon", Size = new Size(20,20), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0,10,4,0), TabStop = false };
            selection.Controls.Add(selectedIcon,0,0); whitelist.Margin = new Padding(0,0,0,0); selection.Controls.Add(whitelist,1,0);
            listRow.Controls.Add(selection, 0, 0); listRow.Controls.Add(remove, 1, 0); Controls.Add(listRow, 0, 4);
            var listHint = Label("playback-hint",text.Pick("名单内程序播放音频时，暂缓自动关屏。", "Allowed apps defer auto off while playing audio."));
            listHint.Font = OwnFont(new Font(Font.FontFamily,14F)); listHint.Margin = new Padding(0,0,0,12); Controls.Add(listHint,0,5);
            var capRow = new FlowLayoutPanel { Name = "playback-limit-row", BackColor=Color.Transparent, AutoSize = true, Dock = DockStyle.Top,
                WrapContents = false, Margin = new Padding(0), TabIndex = 3 };
            limitEnabled = new MintCheckBox { Font = OwnFont(new Font(Font.FontFamily,16F)), Name = "playback-limit-enabled", Text = text.Pick("保护上限", "Protection limit"),
                Checked = enabled, AutoSize = true, ForeColor = ForeColor, Margin = new Padding(0, 2, 8, 0), TabIndex = 0 };
            limitMinutes = new MintNumberBox { Font = OwnFont(new Font(Font.FontFamily,20F)), Name = "playback-limit-minutes", Minimum = 1, Maximum = 1440,
                Value = minutes >= 1 && minutes <= 1440 ? minutes : 60, Width = 74,
                AccessibleName = text.Pick("播放保护上限分钟数，1至1440", "Playback limit in minutes, 1 to 1440") };
            limitStepper = new MintStepper(limitMinutes) { Width = 158, TabIndex = 1, Enabled = enabled };
            limitEnabled.CheckedChanged += delegate { limitStepper.Enabled = limitEnabled.Checked; UpdateLimitHint(); };
            limitMinutes.TextChanged += delegate { UpdateLimitHint(); };
            var capLine = Row("protection-limit-line"); capLine.Margin = new Padding(0,8,0,0);
            capLine.Controls.Add(limitEnabled,0,0); capRow.Controls.Add(limitStepper);
            capRow.Controls.Add(new MintLabel { Name="playback-limit-unit", AutoSize = true, Text = text.Pick("分钟", "min"), Margin = new Padding(12, 9, 0, 0) });
            capLine.Controls.Add(capRow,1,0); Controls.Add(capLine, 0, 6);
            limitHint = Label("playback-limit-hint", ""); limitHint.Font = OwnFont(new Font(Font.FontFamily,14F)); limitHint.Margin = new Padding(0,4,0,0); Controls.Add(limitHint,0,7); UpdateLimitHint();
            warning = Label("playback-warning", ""); warning.Visible = false; warning.AccessibleRole = AccessibleRole.Alert;
            warning.AutoSize = false; warning.Height = 38; warning.AutoEllipsis = true;
            warning.Font = OwnFont(new Font(Font.FontFamily,14F));
            saveStatus = Label("whitelist-save-status", ""); saveStatus.Visible = false; saveStatus.AccessibleRole = AccessibleRole.Alert;
            warningToggle = new LinkLabel { Name = "playback-warning-toggle", Text = text.Pick("诊断详情", "Diagnostic details"), AutoSize = true, Visible = false,
                Margin = new Padding(0,0,0,5), LinkColor = MintTheme.Green, ActiveLinkColor = MintTheme.Green };
            warningDetails = new TextBox { Name = "playback-warning-details", AutoSize = false, ReadOnly = true, Multiline = true, WordWrap = true,
                ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Top, Height = 96, Visible = false, Margin = new Padding(0,0,0,6),
                AccessibleName = text.Pick("完整播放诊断", "Full playback diagnostics") };
            warningDetails.Font = OwnFont(new Font(Font.FontFamily,14F));
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
        internal void ApplyFontDpi(int dpi,float rasterDpi,string family=null)
        {
            SettingsDialog.RescaleOwnedFonts(this,ownedFonts,fontPoints,dpi,rasterDpi,family);
            // RescaleOwnedFonts replaces and disposes owned Font objects. This
            // detached template must follow the replacement before row rebuild.
            sourceNameFont=ownedFonts[sourceNameFontIndex];
            // Rebuild from logical geometry once the whole tree has finished scaling.
            // Re-scaling rounded physical bounds (120 -> 144) otherwise differs by a
            // pixel from a later row built directly at 144 DPI.
            sourcesKey = null; RenderSourcesFromCache();
        }
        private void Caption(string name,string zh,string en)
        {
            Control[] matches=Controls.Find(name,true);
            if(matches.Length!=0) matches[0].Text=text.Pick(zh,en);
        }
        internal void ApplyLanguage(UiText value,int dpi,float rasterDpi)
        {
            text=value;
            Caption("playback-title","播放保护","Playback protection");
            Caption("refresh-audio","刷新","Refresh");
            Caption("remove-whitelist","移除","Remove");
            Caption("playback-hint","名单内程序播放音频时，暂缓自动关屏。","Allowed apps defer auto off while playing audio.");
            Caption("playback-limit-enabled","保护上限","Protection limit");
            Caption("playback-limit-unit","分钟","min");
            warningToggle.Text=warningExpanded ? text.Pick("收起诊断","Hide diagnostics") : text.Pick("诊断详情","Diagnostic details");
            warningDetails.AccessibleName=text.Pick("完整播放诊断","Full playback diagnostics");
            remove.AccessibleName=remove.Text;
            whitelist.AccessibleName=text.Pick("白名单中的程序","Allowed applications");
            limitMinutes.AccessibleName=text.Pick("播放保护上限分钟数，1至1440","Playback limit in minutes, 1 to 1440");
            limitStepper.Controls["playback-limit-minutes-minus"].AccessibleName="− "+limitMinutes.AccessibleName;
            limitStepper.Controls["playback-limit-minutes-plus"].AccessibleName="+ "+limitMinutes.AccessibleName;
            ApplyFontDpi(dpi,rasterDpi,text.English ? "Segoe UI" : "Microsoft YaHei UI");
            whitelistKey=null;sourcesKey=null;RenderWhitelist();RenderSourcesFromCache();
            UpdateLimitHint();
            if(lastState!=null) {
                // Applied options in the cached read may predate an immediate
                // list save. Re-localize presentation without replacing paths.
                var state=new PlaybackPanelState { Audio=lastState.Audio, Timing=lastState.Timing,
                    SettingsWarning=lastState.SettingsWarning };
                RefreshState(state);
            } else if(lastReadError!=null) ShowReadError(lastReadError);
            instant.Text=lastSaveError!=null ? text.Pick("保存失败","Save failed") : listSaved ? text.Pick("已保存","Saved") : text.Pick("即时保存","Saved instantly");
            if(lastSaveError!=null) SaveFailure(lastSaveError);
        }
        private Label Label(string name, string value)
        {
            return new MintLabel { Name = name, Text = value, AutoSize = true, Dock = DockStyle.Top,
                Margin = new Padding(0, 3, 0, 3), ForeColor = SystemInformation.HighContrast ? SystemColors.ControlText : MintTheme.Muted };
        }
        private static TableLayoutPanel Row(string name)
        {
            var row = new TableLayoutPanel { Name = name, ColumnCount = 2, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, BackColor=Color.Transparent, Margin = new Padding(0), TabStop = false };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); return row;
        }
        private Button ActionButton(string name, string caption, int tab)
        {
            Button button = SystemInformation.HighContrast ? new Button() : new SurfaceButton();
            button.Name = name; button.Text = caption; button.AutoSize = true; button.Font = Font;
            button.MinimumSize = new Size(102, 39); button.Padding = new Padding(8, 2, 8, 2);
            button.Margin = new Padding(0); button.TabIndex = tab; button.AccessibleName = caption;
            return button;
        }
        internal bool LimitEnabled { get { return limitEnabled.Checked; } }
        internal bool TryReadLimit(out int minutes, out string error)
        {
            error = null;
            if (!limitEnabled.Checked) { minutes = (int)limitMinutes.Value; return true; }
            if (Int32.TryParse(limitMinutes.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out minutes) && minutes >= 1 && minutes <= 1440) return true;
            error = "播放保护上限请输入 1–1440 分钟。 / Enter a playback limit of 1–1440 minutes."; return false;
        }
        internal void RefreshState(PlaybackPanelState state)
        {
            using (SettingsDpiContext.EnterUi()) {
            if (IsDisposed) return;
            lastState=state;lastReadError=null;
            if (state == null) { ShowReadError("播放状态暂不可用 / Playback status is unavailable"); return; }
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
                reason == AutoOffPauseReason.Playback ? (String.IsNullOrWhiteSpace(state.Timing.SourceName) ? text.Pick("名单程序", "Allowed app") : state.Timing.SourceName) + text.Pick(" 播放中，已暂缓自动关屏。", " is playing; auto off is on hold") :
                reason == AutoOffPauseReason.StateUnknown || reason == AutoOffPauseReason.AudioUnknown ? text.Pick("暂时无法确认播放状态", "Playback state is uncertain") : "";
            status.Visible = !String.IsNullOrEmpty(status.Text);
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
            lastReadError=message;lastState=null;
            audioKnown = false; currentSources = new AudioSource[0]; RenderSourcesFromCache();
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
            if(String.IsNullOrEmpty(value)) return "";
            string[] lines=value.Replace("\r\n","\n").Split('\n');
            for(int i=0;i<lines.Length;i++) {
                string[] parts=lines[i].Split(new[]{" / "},StringSplitOptions.None);
                if(parts.Length>1) lines[i]=text.English ? parts[parts.Length-1] : parts[0];
            }
            return String.Join(Environment.NewLine,lines);
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
                    whitelist.Items.Cast<object>().Max(o => whitelist.MeasureItemWidth(o.ToString())) + SystemInformation.VerticalScrollBarWidth + 2));
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
            selectedIcon.Visible = false;
            whitelist.SelectedImage = hasPath ? selectedIcon.Image : null;
            ((TableLayoutPanel)selectedIcon.Parent).ColumnStyles[0].Width = 0;
            remove.Enabled = saveWhitelist != null && !savingList && choice != null && choice.Path != null;
            tips.SetToolTip(whitelist, choice == null ? null : choice.Path);
            whitelist.AccessibleDescription = choice != null && choice.Path != null ? choice.Path : whitelistKnown
                ? text.Pick("尚未添加程序", "No apps added") : text.Pick("名单暂不可用", "Whitelist unavailable");
            }
        }
        internal Func<string[],System.Threading.Tasks.Task<string>> SaveWhitelistAsync;
        private async void Persist(string[] candidate)
        {
            if (savingList || saveWhitelist == null) return;
            savingList = true;
            try {
                string error = SaveWhitelistAsync==null ? saveWhitelist((string[])candidate.Clone()) : await SaveWhitelistAsync((string[])candidate.Clone());
                if(IsDisposed || Disposing)return;
                if (error != null) { SaveFailure(error); return; }
                paths = (string[])candidate.Clone(); whitelistKnown = true;
                RenderWhitelist(true); sourcesKey = null; RenderSourcesFromCache();
                listSaved=true;lastSaveError=null;
                instant.Text = text.Pick("已保存", "Saved"); instant.ForeColor = MintTheme.Green;
                saveStatus.Text = ""; saveStatus.Visible = false;
            } catch (Exception ex) { if(!IsDisposed && !Disposing)SaveFailure(ex.Message); }
            finally { savingList = false; if(!IsDisposed && !Disposing)UpdateSelection(); }
        }
        private void SaveFailure(string error)
        {
            lastSaveError=error ?? "";listSaved=false;
            instant.Text = text.Pick("保存失败", "Save failed");
            instant.ForeColor = SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(166, 38, 24);
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
                    var picture = new PictureBox { Name = "playing-icon-" + i, BackColor=Color.Transparent, Size = new Size(46,46), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(8,0,16,0), TabStop = false, Tag = path };
                    picture.Scale(sourceScale); RefreshPicture(picture); sources.Controls.Add(picture,0,i);
                    name.Font = sourceNameFont; name.ForeColor = ForeColor; name.AutoSize = false; name.AutoEllipsis = true; name.Dock = DockStyle.Fill; name.TextAlign = ContentAlignment.MiddleLeft; name.Height = 30; name.Margin = new Padding(0); name.AccessibleDescription = path;
                    tips.SetToolTip(name, name.Text + (path == null ? "" : "\n" + path));
                    var copy = new TableLayoutPanel { BackColor=Color.Transparent, Font = Font, Name = "playing-copy-"+i, ColumnCount = 1, RowCount = 2, AutoSize = false, Height = 56, Dock = DockStyle.Fill, Margin = new Padding(0,0,8,0) };
                    copy.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); copy.RowStyles.Add(new RowStyle(SizeType.Percent,58)); copy.RowStyles.Add(new RowStyle(SizeType.Percent,42));
                    var caption = Label("playing-caption-"+i,text.Pick("正在播放", "Playing")); caption.Font = limitHint.Font; caption.Margin = new Padding(0); caption.Dock = DockStyle.Fill; caption.TextAlign = ContentAlignment.MiddleLeft;
                    copy.Controls.Add(name,0,0); copy.Controls.Add(caption,0,1); copy.Scale(sourceScale); sources.Controls.Add(copy,1,i);
                    bool added = path != null && paths.Any(p => Same(p, path));
                    var add = ActionButton("add-whitelist-" + i, added ? text.Pick("已加入", "Added") : text.Pick("加入", "Add"), i);
                    add.Enabled = path != null && !added && saveWhitelist != null;
                    add.AccessibleName = add.Text + " " + name.Text;
                    add.Click += delegate { if (path != null && !paths.Any(p => Same(p, path))) Persist(paths.Concat(new[] { path }).ToArray()); };
                    add.Font = remove.Font;
                    var surface = add as SurfaceButton; if (surface != null) surface.Primary = true;
                    add.Margin = new Padding(0,4,0,5); add.Scale(sourceScale); sources.Controls.Add(add, 2, i);
                }
                // Bound the live source list; whitelist size never changes panel height.
                int rowHeight = Math.Max(Font.Height + SourceHeight(12), SourceHeight(56));
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
            int stepWidth = logical < (text.English ? 440 : 390) ? 128 : 194;
            bool stack = logical < (text.English ? 440 : 380);
            int nextStepperWidth = SourceHeight(stepWidth);
            if (limitStepper.Width != nextStepperWidth) limitStepper.Width = nextStepperWidth;
            var selection=selectedIcon.Parent;
            Padding selectionMargin=new Padding(0,0,SourceHeight(9),0);
            if (selection.Margin != selectionMargin) selection.Margin=selectionMargin;
            Size removeSize=new Size(SourceHeight(102),SourceHeight(39));
            if (remove.MinimumSize != removeSize) remove.MinimumSize=removeSize;
            if (remove.Margin != Padding.Empty) remove.Margin=Padding.Empty;
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
            limitHint.Text = limitEnabled.Checked ? text.Pick("连续 " + limitMinutes.Text + " 分钟无实体操作后，播放不再阻止关屏。", "No physical input for " + limitMinutes.Text + " min ends protection.")
                : text.Pick("播放期间持续保护，直到播放停止或手动关屏。", "Protected until playback stops or you turn off the display manually.");
        }
        private void RefreshPicture(PictureBox picture)
        {
            if (picture.IsDisposed) return;
            Image next = iconCache.GetOrQueue(picture.Tag as string, Math.Max(16,picture.Width)) ?? fallbackIcon;
            if (!Object.ReferenceEquals(picture.Image,next)) picture.Image = next;
            if(picture==selectedIcon) whitelist.SelectedImage=selectedIcon.Tag==null ? null : next;
        }
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e); if (Visible) IconsChanged(this,EventArgs.Empty);
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e); IconsChanged(this,EventArgs.Empty);
        }
        protected override void OnHandleDestroyed(EventArgs e)
        {
            iconHandleGeneration++; iconRefreshQueued = false;
            base.OnHandleDestroyed(e);
        }
        private void IconsChanged(object sender, EventArgs e)
        {
            if (IsDisposed || Disposing || !IsHandleCreated || !Visible || iconRefreshQueued) return;
            int generation = iconHandleGeneration;
            iconRefreshQueued = true;
            try { BeginInvoke(new Action(delegate {
                if (generation != iconHandleGeneration) return;
                iconRefreshQueued = false;
                using (SettingsDpiContext.EnterUi()) {
                if (IsDisposed || Disposing || !Visible) return;
                RefreshPicture(selectedIcon);
                foreach (Control child in sources.Controls) { var picture = child as PictureBox; if (picture != null) RefreshPicture(picture); }
                }
            })); } catch (InvalidOperationException) {
                if (generation == iconHandleGeneration) iconRefreshQueued = false;
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { iconRefreshQueued = false; iconHandleGeneration++; iconCache.Changed -= IconsChanged; tips.Dispose(); }
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
