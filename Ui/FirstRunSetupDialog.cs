using System;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace ScreenOffTray.Ui
{
    internal sealed class FirstRunSetupState
    {
        internal bool Checking, Preparing, Ready, CanPrepare;
        internal string Detail, Diagnostics, Evidence;
    }

    internal sealed class FirstRunSetupDialog : Form
    {
        private readonly UiText text;
        private readonly Action prepare, cancelPrepare, recheck, openDisplaySettings, openInstallationHelp;
        private Font headingFont, statusFont;
        private readonly System.Collections.Generic.List<Font> ownedFonts = new System.Collections.Generic.List<Font>();
        private float uiScale = 1F;
        private readonly Icon ownedIcon;
        private readonly Panel header, body, footer;
        private readonly IconView productIcon;
        private readonly Label heading, status, description;
        private readonly TextBox diagnostics;
        private readonly LinkLabel disclosure, displayLink;
        private readonly Button primary, secondary;
        private FirstRunSetupState current = new FirstRunSetupState { Checking = true };
        private bool prepareRequested, cancelRequested, closeWhenStopped, expanded, handlingClose, physicalLinkVisible;
        private int refreshVersion;
        private bool layingOutBody, layingOutFooter, fittingWorkArea;
        private int expandedHeightAdded;
        private enum Route { None, Prepare, Display, Help, Recheck, Close, Cancel }
        private Route primaryRoute, secondaryRoute;
        private sealed class IconView : Control
        {
            internal Icon Artwork;
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (Artwork != null) e.Graphics.DrawIcon(Artwork, ClientRectangle);
            }
        }

        internal FirstRunSetupDialog(UiText text, Action prepare, Action cancelPrepare, Action recheck,
            Action openDisplaySettings, Action openInstallationHelp, Icon applicationIcon)
        {
            this.text = text; this.prepare = prepare; this.cancelPrepare = cancelPrepare; this.recheck = recheck;
            this.openDisplaySettings = openDisplaySettings; this.openInstallationHelp = openInstallationHelp;
            Text = text.Pick("ScreenOffTray · 首次使用设置", "ScreenOffTray · First-time setup");
            Font = new Font(text.English ? "Segoe UI" : "Microsoft YaHei UI", 9.5F); ownedFonts.Add(Font);
            headingFont = new Font(Font.FontFamily, 13F, FontStyle.Bold); ownedFonts.Add(headingFont);
            statusFont = new Font(Font, FontStyle.Bold); ownedFonts.Add(statusFont);
            if (applicationIcon != null) { ownedIcon = (Icon)applicationIcon.Clone(); Icon = ownedIcon; }
            StartPosition = FormStartPosition.CenterScreen; ShowInTaskbar = true;
            MinimizeBox = false; MaximizeBox = false; AutoScaleMode = AutoScaleMode.None;
            BackColor = MintTheme.Paper; ForeColor = MintTheme.Ink; ClientSize = new Size(450, 310);
            MinimumSize = new Size(300, 230);
            body = new Panel { Name = "body", Dock = DockStyle.Fill, AutoScroll = true, BackColor = MintTheme.Paper };
            header = new Panel { Name = "header", Dock = DockStyle.Top, Height = 58, BackColor = MintTheme.Header };
            footer = new Panel { Name = "footer", Dock = DockStyle.Bottom, Height = 56, BackColor = MintTheme.Paper };
            productIcon = new IconView { Name = "productIcon", Artwork = ownedIcon, BackColor = MintTheme.Header };
            heading = new Label { Name = "heading", AutoSize = true, Font = headingFont, ForeColor = MintTheme.Ink };
            status = new Label { Name = "status", AutoSize = true, Font = statusFont };
            description = new Label { Name = "description", AutoSize = true, ForeColor = MintTheme.Ink };
            disclosure = new LinkLabel { Name = "disclosure", AutoSize = true, LinkColor = MintTheme.Green, Text = text.Pick("查看诊断详情", "Show diagnostic details") };
            diagnostics = new TextBox { Name = "diagnostics", Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle, BackColor = MintTheme.Paper, ForeColor = MintTheme.Ink, Visible = false };
            displayLink = new LinkLabel { Name = "displayLink", AutoSize = true, LinkColor = MintTheme.Green, Text = text.Pick("打开显示设置", "Open display settings"), Visible = false };
            disclosure.LinkBehavior = displayLink.LinkBehavior = LinkBehavior.HoverUnderline;
            primary = new Button { Name = "primary", AutoSize = false, FlatStyle = FlatStyle.Flat, BackColor = MintTheme.Green, ForeColor = SystemInformation.HighContrast ? SystemColors.HighlightText : Color.White, UseVisualStyleBackColor = false };
            secondary = new Button { Name = "secondary", AutoSize = false, FlatStyle = FlatStyle.Flat, BackColor = MintTheme.Paper, ForeColor = MintTheme.Ink, UseVisualStyleBackColor = false };
            secondary.FlatAppearance.BorderSize = 1; secondary.FlatAppearance.BorderColor = MintTheme.Line;
            primary.FlatAppearance.BorderSize = 0;
            header.Controls.AddRange(new Control[] { productIcon, heading });
            body.Controls.AddRange(new Control[] { status, description, displayLink, disclosure, diagnostics });
            footer.Controls.AddRange(new Control[] { primary, secondary });
            Controls.Add(body); Controls.Add(footer); Controls.Add(header);
            primary.Click += delegate { InvokeRoute(primaryRoute); };
            secondary.Click += delegate { InvokeRoute(secondaryRoute); };
            displayLink.LinkClicked += delegate { InvokeRoute(Route.Display); };
            disclosure.LinkClicked += delegate { ToggleDetails(); };
            body.SizeChanged += delegate { LayoutBody(); };
            footer.SizeChanged += delegate { LayoutFooter(); };
            FormClosing += HandleClosing;
            Shown += delegate {
                using (SettingsDpiContext.EnterUi()) {
                    int dpi = (int)GetDpiForWindow(Handle), systemDpi = (int)GetDpiForSystem();
                    ApplyInitialDpi(dpi > 0 ? dpi : 96, systemDpi > 0 ? systemDpi : 96);
                    FitWorkArea(); Activate(); BringToFront();
                }
            };
            heading.Text = text.Pick("首次使用设置", "First-time setup");
            productIcon.SetBounds(20, 13, 32, 32);
            heading.Location = new Point(62, Math.Max(0, (header.Height - heading.Height) / 2));
            RefreshSetup(current);
        }

        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
        // Font points are scaled only for a window on a monitor with a different DPI.
        internal void ApplyInitialDpi(int dpi, int systemDpi)
        {
            if (dpi <= 0 || systemDpi <= 0) return;
            uiScale = dpi / 96F;
            float fontScale = (float)dpi / systemDpi;
            Font next = new Font(text.English ? "Segoe UI" : "Microsoft YaHei UI", 9.5F * fontScale);
            Font nextHeading = new Font(next.FontFamily, 13F * fontScale, FontStyle.Bold);
            Font nextStatus = new Font(next, FontStyle.Bold);
            headingFont = nextHeading; statusFont = nextStatus; ownedFonts.Add(next); ownedFonts.Add(nextHeading); ownedFonts.Add(nextStatus); Font = next; heading.Font = nextHeading; status.Font = nextStatus;
            MinimumSize = new Size((int)Math.Round(300 * uiScale), (int)Math.Round(230 * uiScale));
            ClientSize = new Size((int)Math.Round(450 * uiScale), (int)Math.Round((expanded ? 414 : 310) * uiScale));
            header.Height = Units(58);
            productIcon.SetBounds(Units(20), Units(13), Units(32), Units(32));
            heading.Location = new Point(Units(62), Math.Max(0, (header.Height - heading.Height) / 2));
            LayoutBody(); LayoutFooter(); }
        private int Units(int value) { return Math.Max(1, (int)Math.Round(value * uiScale)); }
        private void FitWorkArea()
        {
            if (WindowState == FormWindowState.Normal) FitToWorkArea(Screen.FromControl(this).WorkingArea);
        }
        internal void FitToWorkArea(Rectangle area)
        {
            if (fittingWorkArea || area.Width <= 0 || area.Height <= 0 || IsDisposed) return;
            fittingWorkArea = true;
            try {
                int w = Math.Min(Width, area.Width), h = Math.Min(Height, area.Height);
                MinimumSize = new Size(Math.Min(MinimumSize.Width, w), Math.Min(MinimumSize.Height, h));
                Size = new Size(w, h);
                Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)), Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
            } finally { fittingWorkArea = false; }
        }
        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            // Display changes, taskbar/work-area changes and monitor moves can
            // arrive while the user follows the guide in Windows Settings.
            if (body != null && Visible && !IsDisposed &&
                (message.Msg == 0x007E || message.Msg == 0x001A || message.Msg == 0x02E0 || message.Msg == 0x0005))
                using (SettingsDpiContext.EnterUi()) FitWorkArea();
        }
        private void LayoutBody()
        {
            if (layingOutBody) return; layingOutBody = true;
            try {
            Point scroll = body.AutoScrollPosition;
            int width = Math.Max(1, body.ClientSize.Width - Units(48) - SystemInformation.VerticalScrollBarWidth);
            int y = Units(12);
            foreach (Control c in new Control[] { status, description, displayLink, disclosure, diagnostics }) {
                if (c == displayLink && !physicalLinkVisible) continue;
                if (c == disclosure && String.IsNullOrWhiteSpace(diagnostics.Text)) continue;
                if (c == diagnostics && (!expanded || String.IsNullOrWhiteSpace(diagnostics.Text))) continue;
                if (c == diagnostics) c.SetBounds(Units(24) + scroll.X, y + scroll.Y, width, Units(90));
                else { c.MaximumSize = new Size(width, 0); c.Location = new Point(Units(24) + scroll.X, y + scroll.Y); }
                y += c.Height + Units(7);
            }
            body.AutoScrollMinSize = new Size(0, y + Units(8)); } finally { layingOutBody = false; }
        }
        private Size ButtonSize(Button button, int available)
        {
            int padX = Units(24), padY = Units(12), minimumHeight = Units(36);
            Size natural = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            Size preferred = button.GetPreferredSize(Size.Empty);
            int width = Math.Min(available, Math.Max(Units(90), Math.Max(preferred.Width, natural.Width + padX)));
            Size wrapped = TextRenderer.MeasureText(button.Text, button.Font, new Size(Math.Max(1, width - padX), 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return new Size(width, Math.Max(Math.Max(minimumHeight, preferred.Height), wrapped.Height + padY));
        }
        private void LayoutFooter()
        {
            if (layingOutFooter || footer.ClientSize.Width <= 0) return;
            layingOutFooter = true;
            try {
            int margin = Math.Min(Units(16), Math.Max(0, (footer.ClientSize.Width - 1) / 4));
            int gap = Units(10), available = Math.Max(1, footer.ClientSize.Width - 2 * margin);
            Size p = ButtonSize(primary, available), q = ButtonSize(secondary, available);
            bool hasSecond = secondaryRoute != Route.None;
            bool stacked = hasSecond && p.Width + q.Width + gap > available;
            int height = stacked ? p.Height + q.Height + 3 * gap : Math.Max(p.Height, hasSecond ? q.Height : 0) + 2 * gap;
            if (footer.Height != height) footer.Height = height;
            primary.SetBounds(footer.ClientSize.Width - margin - p.Width, gap, p.Width, p.Height);
            if (hasSecond) secondary.SetBounds(stacked ? footer.ClientSize.Width - margin - q.Width : primary.Left - gap - q.Width,
                stacked ? primary.Bottom + gap : gap, q.Width, q.Height);
            } finally { layingOutFooter = false; }
        }
        private void UpdateDisclosure()
        {
            diagnostics.Visible = expanded && !String.IsNullOrWhiteSpace(diagnostics.Text);
            disclosure.Text = expanded ? text.Pick("收起诊断详情", "Hide diagnostic details") : text.Pick("查看诊断详情", "Show diagnostic details");
        }
        private void ToggleDetails()
        {
            int before = ClientSize.Height;
            expanded = !expanded;
            UpdateDisclosure();
            int delta = expanded ? Units(104) : -expandedHeightAdded;
            ClientSize = new Size(ClientSize.Width, Math.Max(MinimumSize.Height, ClientSize.Height + delta));
            if (Visible && !IsDisposed) FitWorkArea();
            if (expanded) expandedHeightAdded = Math.Max(0, ClientSize.Height - before);
            else expandedHeightAdded = 0;
            LayoutBody(); LayoutFooter();
        }
        private static string FriendlyDetail(string detail, string fallback)
        {
            if (String.IsNullOrWhiteSpace(detail)) return fallback;
            detail = detail.Trim();
            return detail.Length <= 400 ? detail : detail.Substring(0, 400) + "…";
        }
        private static bool Has(string value, string needle) { return (value ?? "").IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0; }
        internal void RefreshSetup(FirstRunSetupState state)
        {
            if (state == null) throw new ArgumentNullException("state");
            refreshVersion++; current = state;
            if (!state.Preparing) { prepareRequested = false; cancelRequested = false; }
            if (closeWhenStopped && !state.Preparing && !handlingClose) { closeWhenStopped = false; Close(); return; }
            string evidence = (state.Detail ?? "") + "\n" + (state.Evidence ?? "");
            bool physical = Has(evidence, "仅支持一个已启用的实体屏") || Has(evidence, "One active physical display only") || Has(evidence, "实体屏未启用");
            bool component = Has(evidence, "需要唯一可用的 Root\\MttVDD") || Has(evidence, "Exactly one available Root\\MttVDD");
            bool priorityDetail = Has(state.Detail, "恢复尚未确认") || Has(state.Detail, "recovery is unverified") ||
                Has(state.Detail, "准备已取消") || Has(state.Detail, "preparation cancelled") ||
                Has(state.Detail, "不支持自动准备") || Has(state.Detail, "preparation is unsupported") ||
                Has(state.Detail, "准备未完成") || Has(state.Detail, "preparation did not complete");
            // A recovery warning changes the explanation, never the repair route.
            // Fresh physical/component evidence still gates automatic preparation.
            primaryRoute = secondaryRoute = Route.None;
            primary.Visible = secondary.Visible = true; primary.Enabled = secondary.Enabled = true;
            physicalLinkVisible = physical && !state.Preparing && !state.Checking && !state.Ready;
            displayLink.Visible = physicalLinkVisible;
            if (state.Ready && !state.Preparing && !state.Checking) {
                status.Text = text.Pick("已就绪", "Ready"); description.Text = text.Pick("配置已完成，可以开始使用。", "Setup is complete. You're ready to go.");
                primary.Text = text.Pick("开始使用", "Start using the app"); primaryRoute = Route.Close; secondary.Visible = false;
            } else if (state.Preparing || prepareRequested) {
                status.Text = text.Pick("正在准备…", "Preparing…"); description.Text = FriendlyDetail(state.Detail, text.Pick("请稍候，显示可能短暂闪动。", "Please wait. Your display may flicker briefly."));
                primary.Text = text.Pick("取消", "Cancel"); primaryRoute = Route.Cancel; primary.Enabled = !cancelRequested; secondary.Visible = false;
            } else if (state.Checking) {
                status.Text = text.Pick("正在检查…", "Checking…"); description.Text = text.Pick("正在检查显示和准备情况。", "Checking the display and setup status.");
                primary.Text = text.Pick("检查中…", "Checking…"); primary.Enabled = false;
                secondary.Text = text.Pick("稍后处理", "Not now"); secondaryRoute = Route.Close;
            } else if (physical) {
                status.Text = text.Pick("需要检查显示条件", "Check display requirements"); description.Text = text.Pick("需要一个已启用的实体屏。\r\n若通过虚拟屏远程操作，可先稍后处理，实体屏恢复后再检查。无需重复安装。", "One active physical display is required.\r\nIf using a virtual display remotely, check again when the physical display is available. You can do this later; no need to reinstall.");
                primary.Text = text.Pick("重新检查", "Check again"); primaryRoute = Route.Recheck;
                secondary.Text = text.Pick("稍后处理", "Not now"); secondaryRoute = Route.Close;
            } else if (component) {
                status.Text = text.Pick("组件需要检查", "Component needs attention"); description.Text = text.Pick("所需组件可能缺失、不可用或存在冲突，请查看安装帮助。", "The required component may be missing, unavailable, or conflicting. See installation help.");
                primary.Text = text.Pick("安装帮助", "Installation help"); primaryRoute = Route.Help;
                secondary.Text = text.Pick("重新检查", "Check again"); secondaryRoute = Route.Recheck;
            } else {
                status.Text = text.Pick("显示准备", "Display setup");
                description.Text = FriendlyDetail(state.Detail, state.CanPrepare ? text.Pick("完成配置后即可使用，显示可能短暂闪动。", "Complete setup to get started. Your display may flicker briefly.") : text.Pick("请重新检查准备情况。", "Check the setup status again."));
                primary.Text = state.CanPrepare ? text.Pick("一键完成配置", "Complete setup") : text.Pick("重新检查", "Check again");
                primaryRoute = state.CanPrepare ? Route.Prepare : Route.Recheck;
                secondary.Text = text.Pick("稍后处理", "Not now"); secondaryRoute = Route.Close;
            }
            if (priorityDetail && !state.Ready && !state.Preparing && !state.Checking && !prepareRequested && (physical || component))
                description.Text = FriendlyDetail(state.Detail, "") + Environment.NewLine + description.Text;
            diagnostics.Text = CombineDetails(state.Detail, state.Diagnostics); disclosure.Visible = !String.IsNullOrWhiteSpace(diagnostics.Text);
            UpdateDisclosure(); LayoutBody(); LayoutFooter();
            if (Visible && !IsDisposed) FitWorkArea();
        }
        private static string CombineDetails(string detail, string extra)
        {
            detail = (detail ?? "").Trim(); extra = (extra ?? "").Trim();
            if (detail.Length == 0) return extra;
            if (extra.Length == 0 || detail.IndexOf(extra, StringComparison.Ordinal) >= 0) return detail;
            if (extra.IndexOf(detail, StringComparison.Ordinal) >= 0) return extra;
            return detail + Environment.NewLine + Environment.NewLine + extra;
        }
        private void ActionFailed(Exception ex)
        {
            description.Text = text.Pick("操作未完成，请稍后重试。", "The action could not be completed. Please try again later.");
            diagnostics.Text = CombineDetails(CombineDetails(current.Detail, current.Diagnostics), ex.Message);
            disclosure.Visible = !String.IsNullOrWhiteSpace(diagnostics.Text);
            UpdateDisclosure(); LayoutBody();
        }
        private void InvokeRoute(Route route)
        {
            switch (route) {
                case Route.Prepare:
                    if (prepareRequested || current.Preparing || !current.CanPrepare) return;
                    prepareRequested = true; cancelRequested = false; int before = refreshVersion;
                    RefreshPending();
                    try { prepare(); }
                    catch (Exception ex) {
                        if (!current.Preparing) { prepareRequested = false; if (refreshVersion == before) RefreshSetup(current); }
                        ActionFailed(ex);
                    }
                    break;
                case Route.Cancel: RequestCancel(); break;
                case Route.Recheck: try { recheck(); } catch (Exception ex) { ActionFailed(ex); } break;
                case Route.Display: try { openDisplaySettings(); } catch (Exception ex) { ActionFailed(ex); } break;
                case Route.Help: try { openInstallationHelp(); } catch (Exception ex) { ActionFailed(ex); } break;
                case Route.Close: Close(); break;
            }
        }
        private void RefreshPending()
        {
            physicalLinkVisible = false; displayLink.Visible = false;
            status.Text = text.Pick("正在准备…", "Preparing…"); description.Text = text.Pick("请稍候，显示可能短暂闪动。", "Please wait. Your display may flicker briefly.");
            primary.Text = text.Pick("取消", "Cancel"); primaryRoute = Route.Cancel; secondaryRoute = Route.None; secondary.Visible = false; LayoutBody(); LayoutFooter();
        }
        private void RequestCancel()
        {
            if (!(current.Preparing || prepareRequested) || cancelRequested) return;
            cancelRequested = true; primary.Enabled = false;
            try { cancelPrepare(); } catch (Exception ex) {
                description.Text = text.Pick("取消请求未完成，正在等待准备状态返回。", "The cancellation request failed. Waiting for the setup status to return.");
                diagnostics.Text = CombineDetails(CombineDetails(current.Detail, current.Diagnostics), ex.Message);
                disclosure.Visible = true; UpdateDisclosure(); LayoutBody();
            }
        }
        private void HandleClosing(object sender, FormClosingEventArgs e)
        {
            if (current.Preparing || prepareRequested) { e.Cancel = true; closeWhenStopped = true; handlingClose = true; try { RequestCancel(); } finally { handlingClose = false; } if (!current.Preparing && !prepareRequested) { closeWhenStopped = false; e.Cancel = false; } }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { if (ownedIcon != null) { productIcon.Artwork = null; Icon = null; ownedIcon.Dispose(); } foreach (Font font in ownedFonts) font.Dispose(); ownedFonts.Clear(); }
            base.Dispose(disposing);
        }
    }
}
