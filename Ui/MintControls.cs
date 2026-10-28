using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace ScreenOffTray.Ui
{
    internal static class MintTheme
    {
        internal static Color Paper { get { return SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(255, 255, 255); } }
        internal static Color Header { get { return SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(236, 250, 246); } }
        internal static Color Ink { get { return SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(16, 44, 54); } }
        internal static Color Muted { get { return SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(73, 106, 120); } }
        internal static Color Green { get { return SystemInformation.HighContrast ? SystemColors.HotTrack : Color.FromArgb(0, 143, 135); } }
        internal static Color Line { get { return SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(209, 228, 231); } }
        internal static Color Field { get { return SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(198,213,223); } }
        internal static Color Soft { get { return SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(243,248,249); } }
    }

    internal sealed class MintStatusMark : Control
    {
        internal bool Ready;
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle bounds = Rectangle.Inflate(ClientRectangle,-2,-2);
            if (!Ready) return;
            using (var brush = new SolidBrush(SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(34,183,90))) e.Graphics.FillEllipse(brush,bounds);
            using (var pen = new Pen(SystemInformation.HighContrast ? SystemColors.HighlightText : Color.White, Math.Max(1.5F,Height/12F))) {
                e.Graphics.DrawLines(pen,new[] { new PointF(Width*.28F,Height*.51F),new PointF(Width*.44F,Height*.67F),new PointF(Width*.73F,Height*.34F) });
            }
        }
    }
    internal sealed class MintDisclosureLink : LinkLabel
    {
        internal bool Expanded;
        internal MintDisclosureLink() { LinkBehavior = LinkBehavior.NeverUnderline; TextAlign = ContentAlignment.MiddleLeft; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var pen = new Pen(MintTheme.Line)) { e.Graphics.DrawLine(pen,0,0,Width-1,0); e.Graphics.DrawLine(pen,0,Height-1,Width-1,Height-1); }
            float x = Width-Font.Height, y = Height/2F, r = Font.Height/4F;
            using (var pen = new Pen(MintTheme.Ink,1.5F)) e.Graphics.DrawLines(pen,new[] { new PointF(x-r,y+(Expanded ? r/2 : -r/2)),new PointF(x,y+(Expanded ? -r/2 : r/2)),new PointF(x+r,y+(Expanded ? r/2 : -r/2)) });
        }
    }

    // These retain CheckBox/ComboBox state, keyboard, notifications and accessibility.
    internal sealed class MintCheckBox : CheckBox
    {
        private float visualScale = 1F;
        private int BoxSize { get { return Math.Max(Font.Height+2,(int)Math.Round(22*visualScale)); } }
        protected override void ScaleControl(SizeF factor,BoundsSpecified specified) { visualScale *= factor.Height; base.ScaleControl(factor,specified); }
        public override Size GetPreferredSize(Size proposedSize)
        {
            if(SystemInformation.HighContrast) return base.GetPreferredSize(proposedSize);
            int maximum = MaximumSize.Width > 0 ? MaximumSize.Width : Int32.MaxValue;
            int available = Math.Max(1,maximum-BoxSize-10);
            Size text=TextRenderer.MeasureText(Text,Font,new Size(available,0),TextFormatFlags.WordBreak|TextFormatFlags.NoPadding);
            return new Size(Math.Min(maximum,BoxSize+10+text.Width),Math.Max(BoxSize,text.Height)+4);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            if(SystemInformation.HighContrast) { base.OnPaint(e); return; }
            e.Graphics.Clear(BackColor); e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int box=BoxSize; var bounds=new Rectangle(0,(Height-box)/2,box,box);
            using(var path=SurfaceButton.Outline(bounds,4)) {
                using(var brush=new SolidBrush(Checked ? MintTheme.Green : MintTheme.Paper)) e.Graphics.FillPath(brush,path);
                if(!Checked) using(var pen=new Pen(MintTheme.Line)) e.Graphics.DrawPath(pen,path);
            }
            if(Checked) using(var pen=new Pen(Enabled ? Color.White : SystemColors.GrayText,Math.Max(1.5F,box/12F))) {
                e.Graphics.DrawLines(pen,new[] { new PointF(box*.23F,bounds.Top+box*.5F),new PointF(box*.43F,bounds.Top+box*.7F),new PointF(box*.78F,bounds.Top+box*.28F) });
            }
            var flags=TextFormatFlags.VerticalCenter|TextFormatFlags.WordBreak;
            if(!ShowKeyboardCues) flags|=TextFormatFlags.HidePrefix;
            TextRenderer.DrawText(e.Graphics,Text,Font,new Rectangle(box+10,0,Math.Max(1,Width-box-10),Height),Enabled ? ForeColor : SystemColors.GrayText,flags);
            if(Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(ClientRectangle,-1,-1));
        }
    }
    internal sealed class MintComboBox : ComboBox
    {
        private float itemScale=1F;
        internal MintComboBox() { DrawMode=DrawMode.OwnerDrawFixed; RefreshItemHeight(); }
        private void RefreshItemHeight() {
            // Framework's owner-drawn DropDownList adds six pixels around the item.
            ItemHeight=Math.Max(Font.Height,(int)Math.Round(39*itemScale)-6);
        }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); RefreshItemHeight(); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); RefreshItemHeight(); }
        protected override void ScaleControl(SizeF factor,BoundsSpecified specified) {
            itemScale*=factor.Height; base.ScaleControl(factor,specified); RefreshItemHeight();
        }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            bool selected=(e.State & DrawItemState.Selected)!=0 && (e.State & DrawItemState.ComboBoxEdit)==0;
            Color background=selected ? SystemColors.Highlight : BackColor;
            Color foreground=!Enabled ? SystemColors.GrayText : selected ? SystemColors.HighlightText : ForeColor;
            using(var brush=new SolidBrush(background)) e.Graphics.FillRectangle(brush,e.Bounds);
            string caption=e.Index>=0 && e.Index<Items.Count ? GetItemText(Items[e.Index]) : Text;
            Rectangle bounds=e.Bounds; bounds.Inflate(-7,0);
            TextRenderer.DrawText(e.Graphics,caption,e.Font,bounds,foreground,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix|TextFormatFlags.EndEllipsis);
            e.DrawFocusRectangle(); base.OnDrawItem(e);
        }
    }

    // Text stays untouched on blur: invalid drafts must reach Save validation.
    internal sealed class MintNumberBox : TextBox
    {
        internal decimal Minimum = 1, Maximum = 1440;
        private decimal lastValue = 1;
        internal decimal Value {
            get { int n; return Int32.TryParse(Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out n) && n >= Minimum && n <= Maximum ? n : lastValue; }
            set { lastValue = Math.Max(Minimum, Math.Min(Maximum, value)); Text = lastValue.ToString(CultureInfo.CurrentCulture); }
        }
        internal bool Valid { get { int n; return Int32.TryParse(Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out n) && n >= Minimum && n <= Maximum; } }
        internal void Step(int delta) { if (Valid && Enabled) Value = Value + delta; }
        protected override void OnKeyDown(KeyEventArgs e) {
            if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down) { Step(e.KeyCode == Keys.Up ? 1 : -1); e.SuppressKeyPress = true; }
            base.OnKeyDown(e);
        }
    }

    internal sealed class MintStepper : Panel
    {
        private readonly MintNumberBox number;
        private readonly Button minus, plus;
        private float visualScale = 1F;
        private int Units(float logical) { return Math.Max(1,(int)Math.Round(logical*visualScale)); }
        internal MintStepper(MintNumberBox number)
        {
            this.number = number; Name = number.Name + "-frame"; Size = new Size(158, 39); Margin = new Padding(0); TabStop = false;
            number.BorderStyle = BorderStyle.None; number.TextAlign = HorizontalAlignment.Center;
            minus = new StepButton(false) { Name = number.Name + "-minus", AccessibleName = "− " + number.AccessibleName, TabIndex = 0 };
            plus = new StepButton(true) { Name = number.Name + "-plus", AccessibleName = "+ " + number.AccessibleName, TabIndex = 2 };
            number.TabIndex = 1; Controls.AddRange(new Control[] { minus, number, plus });
            minus.Click += delegate { number.Step(-1); }; plus.Click += delegate { number.Step(1); };
            number.TextChanged += delegate { UpdateButtons(); }; number.EnabledChanged += delegate { UpdateButtons(); };
            number.Enter += delegate { Invalidate(); }; number.Leave += delegate { Invalidate(); };
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); UpdateButtons();
        }
        private void UpdateButtons() { minus.Enabled = number.Enabled && number.Valid && number.Value > number.Minimum; plus.Enabled = number.Enabled && number.Valid && number.Value < number.Maximum; }
        protected override void ScaleControl(SizeF factor,BoundsSpecified specified) { visualScale *= factor.Height; base.ScaleControl(factor,specified); }
        protected override void OnLayout(LayoutEventArgs e) {
            base.OnLayout(e); if (number == null || minus == null || plus == null) return;
            // Opaque child HWNDs remain inside the rounded stroke, including at focus.
            int inset = Units(5), innerHeight = Math.Max(1,Height-inset*2), edge = Math.Max(Units(24),innerHeight);
            minus.SetBounds(inset,inset,edge,innerHeight); plus.SetBounds(Width-edge-inset,inset,edge,innerHeight);
            number.SetBounds(edge+inset+Units(2), Math.Max(inset,(Height-number.PreferredHeight)/2),
                Math.Max(1,Width-(edge+inset+Units(2))*2),number.PreferredHeight);
        }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e); e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float stroke = (ContainsFocus ? 2F : 1F)*visualScale;
            int inset = Math.Max(1,(int)Math.Ceiling(stroke/2));
            using (var path = SurfaceButton.Outline(Rectangle.Inflate(ClientRectangle,-inset+1,-inset+1),Units(6)))
            using (var pen = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : ContainsFocus ? MintTheme.Green : MintTheme.Field,stroke)) e.Graphics.DrawPath(pen,path);
            using (var pen = new Pen(MintTheme.Field)) {
                pen.Width = Math.Max(1F,visualScale);
                e.Graphics.DrawLine(pen,minus.Right+Units(1),Units(5),minus.Right+Units(1),Height-Units(5));
                e.Graphics.DrawLine(pen,plus.Left-Units(1),Units(5),plus.Left-Units(1),Height-Units(5));
            }
        }
        private sealed class StepButton : Button
        {
            private readonly bool plus;
            private float visualScale = 1F;
            internal StepButton(bool plus) { this.plus = plus; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false; BackColor = SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(243,248,249); }
            protected override void ScaleControl(SizeF factor,BoundsSpecified specified) { visualScale *= factor.Height; base.ScaleControl(factor,specified); }
            protected override void OnPaint(PaintEventArgs e) {
                e.Graphics.Clear(BackColor);
                using (var pen = new Pen(Enabled ? MintTheme.Green : SystemColors.GrayText, 1.4F*visualScale)) {
                    float x = Width / 2F, y = Height / 2F, r = Font.Height / 3F;
                    e.Graphics.DrawLine(pen, x-r, y, x+r, y); if (plus) e.Graphics.DrawLine(pen, x, y-r, x, y+r);
                }
                if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
            }
        }
    }
}

