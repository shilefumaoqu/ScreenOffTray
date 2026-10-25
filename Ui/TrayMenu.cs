using System;
using System.Drawing;
using System.Windows.Forms;

namespace ScreenOffTray.Ui
{
    internal sealed class UiText
    {
        internal readonly bool English;
        internal UiText(string language) { English = language == "en"; }
        internal string Pick(string zh, string en) { return English ? en : zh; }
        internal string Title { get { return Pick("自动关屏", "Display Off"); } }
        internal string Minutes(int n) { return n + Pick(" 分钟", " min"); }
    }

    // Controls only: the coordinator supplies status and availability.
    internal sealed class TrayMenu : IDisposable
    {
        internal readonly ContextMenuStrip Menu;
        internal readonly ToolStripMenuItem Status, Immediate, Pause, Settings, Exit;
        private readonly UiText text;
        private readonly Font menuFont;
        internal TrayMenu(UiText text, Action turnOff, Action pause, Action settings, Action exit, float scale)
        {
            this.text = text;
            menuFont = new Font(text.English ? "Segoe UI" : "Microsoft YaHei UI", 9.5F * scale);
            Menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = false, Font = menuFont,
                Renderer = SystemInformation.HighContrast
                    ? (ToolStripRenderer)new ToolStripSystemRenderer()
                    : new ReadableMenuRenderer(),
                Padding = new Padding((int)Math.Round(2 * scale), (int)Math.Round(5 * scale),
                    (int)Math.Round(2 * scale), (int)Math.Round(5 * scale)) };
            Status = new ToolStripMenuItem { Name = "status", Enabled = false };
            Immediate = Item("immediate", text.Pick("立即关屏(&O)", "Turn off now (&O)"), turnOff);
            Pause = Item("pause", "", pause);
            Settings = Item("settings", text.Pick("设置…(&S)", "Settings… (&S)"), settings);
            Exit = Item("exit", text.Pick("退出(&X)", "Exit (&X)"), exit);
            Menu.Items.AddRange(new ToolStripItem[] { Status, new ToolStripSeparator(), Immediate, Pause,
                Settings, new ToolStripSeparator(), Exit });
            foreach (ToolStripItem item in Menu.Items) {
                if (item is ToolStripSeparator) continue;
                // DropDownMenu measures native horizontal insets itself. Extra
                // item padding would extend row bounds beyond the popup width.
                item.Padding = new Padding(0, (int)Math.Round(5 * scale), 0, (int)Math.Round(5 * scale));
            }
            Update("", false, false, false, false, false);
        }
        private static ToolStripMenuItem Item(string name, string label, Action action)
        {
            var item = new ToolStripMenuItem(label) { Name = name, ShowShortcutKeys = false };
            item.Click += delegate { action(); };
            return item;
        }
        internal void Update(string status, bool paused, bool canTurnOff, bool canPause, bool canSettings, bool exiting)
        {
            Status.Text = status ?? ""; Status.Enabled = false;
            Pause.Text = paused ? text.Pick("恢复自动关屏(&P)", "Resume auto off (&P)")
                : text.Pick("暂停自动关屏(&P)", "Pause auto off (&P)");
            Immediate.Enabled = canTurnOff && !exiting; Pause.Enabled = canPause && !exiting;
            Settings.Enabled = canSettings && !exiting; Exit.Enabled = !exiting;
        }
        public void Dispose() { Menu.Dispose(); menuFont.Dispose(); }
    }

    internal sealed class ReadableMenuRenderer : ToolStripProfessionalRenderer
    {
        internal ReadableMenuRenderer() : base(new ReadableMenuColorTable()) { RoundedEdges = false; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Name == "status" || !e.Item.Enabled
                ? Color.FromArgb(100, 112, 128)
                : Color.FromArgb(36, 48, 62);
            base.OnRenderItemText(e);
        }
    }

    internal sealed class ReadableMenuColorTable : ProfessionalColorTable
    {
        private static readonly Color Background = Color.White;
        private static readonly Color Selected = Color.FromArgb(233, 242, 251);
        private static readonly Color Border = Color.FromArgb(221, 227, 234);

        internal ReadableMenuColorTable() { UseSystemColors = false; }
        public override Color ToolStripDropDownBackground { get { return Background; } }
        public override Color MenuItemSelected { get { return Selected; } }
        public override Color MenuItemSelectedGradientBegin { get { return Selected; } }
        public override Color MenuItemSelectedGradientEnd { get { return Selected; } }
        public override Color MenuItemBorder { get { return Border; } }
        public override Color ToolStripBorder { get { return Border; } }
        public override Color SeparatorDark { get { return Border; } }
        public override Color SeparatorLight { get { return Background; } }
    }
}
