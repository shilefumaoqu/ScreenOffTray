using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenOffTray.Ui
{
    internal static class MintTheme
    {
        internal static Color Paper { get { return SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(255, 255, 255); } }
        internal static Color Header { get { return SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(236, 250, 246); } }
        internal static Color Page { get { return SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(218, 239, 224); } }
        internal static Color HeaderAccent { get { return SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(221, 246, 231); } }
        internal static Color Card { get { return SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(255, 255, 255); } }
        internal static Color CardLine { get { return SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(205, 231, 217); } }
        internal static Color MintWash { get { return SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(232, 249, 238); } }
        internal static Color Coral { get { return SystemInformation.HighContrast ? SystemColors.HotTrack : Color.FromArgb(239, 109, 99); } }
        internal static Color Ink { get { return SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(8, 20, 29); } }
        internal static Color Muted { get { return SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(66, 88, 117); } }
        internal static Color Green { get { return SystemInformation.HighContrast ? SystemColors.HotTrack : Color.FromArgb(0, 143, 83); } }
        internal static Color Line { get { return SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(209, 228, 231); } }
        internal static Color Field { get { return SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(198,213,223); } }
        internal static Color Soft { get { return SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(243,248,249); } }
    }

    internal enum MintIconKind
    {
        None,
        Paw,
        Shield,
        Stack,
        Display,
        Keyboard,
        Language,
        Clock
    }

    // Static artwork geometry: repaint only when the host really invalidates.
    internal static class MintLandscape
    {
        internal static void Draw(Graphics g, Rectangle bounds, bool header)
        {
            if (SystemInformation.HighContrast || bounds.Width < 2 || bounds.Height < 2) return;
            GraphicsState saved=g.Save();
            try {
                g.SmoothingMode=SmoothingMode.AntiAlias;
                g.TranslateTransform(bounds.Left,bounds.Top);
                float w=bounds.Width,h=bounds.Height, depth=header ? h*.26F : Math.Min(h*.09F,w*.06F);
                using(var back=new GraphicsPath()) {
                    float y=h-depth;
                    back.StartFigure(); back.AddBezier(0,y,w*.08F,y-depth*.8F,w*.14F,y-depth*.15F,w*.22F,y+depth*.2F);
                    back.AddBezier(w*.22F,y+depth*.2F,w*.32F,y-depth*.6F,w*.34F,y-depth*.6F,w*.43F,y+depth*.15F);
                    back.AddBezier(w*.43F,y+depth*.15F,w*.59F,y-depth*.8F,w*.67F,y+depth*.35F,w*.77F,y-depth*.25F);
                    back.AddBezier(w*.77F,y-depth*.25F,w*.90F,y-depth*.8F,w*.96F,y-depth*.35F,w,y-depth*.1F);
                    back.AddLine(back.GetLastPoint(),new PointF(w,h));back.AddLine(new PointF(w,h),new PointF(0,h));back.CloseFigure();
                    using(var brush=new LinearGradientBrush(new RectangleF(0,h-depth*2,w,Math.Max(1,depth*2)),Color.FromArgb(132,206,236,216),Color.FromArgb(178,204,233,210),90F)) g.FillPath(brush,back);
                }
                using(var front=new GraphicsPath()) {
                    float y=h-depth*.48F;
                    front.StartFigure();front.AddBezier(0,y,w*.12F,y+depth*.50F,w*.26F,y-depth*.7F,w*.39F,y-depth*.05F);
                    front.AddBezier(w*.39F,y-depth*.05F,w*.60F,y+depth*.65F,w*.74F,y-depth*.65F,w,y-depth*.12F);
                    front.AddLine(front.GetLastPoint(),new PointF(w,h));front.AddLine(new PointF(w,h),new PointF(0,h));front.CloseFigure();
                    using(var brush=new SolidBrush(Color.FromArgb(135,219,242,214))) g.FillPath(brush,front);
                }
            } finally { g.Restore(saved); }
        }
    }

    internal sealed class MintGradientPanel : Panel
    {
        internal string Title;
        internal string Subtitle;
        internal Font TitleFont;
        internal Font SubtitleFont;
        internal Color SubtitleColor = MintTheme.Muted;
        internal Rectangle TitleBounds;
        internal MintGradientPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (ClientSize.Width <= 2 || ClientSize.Height <= 2) {
                e.Graphics.Clear(SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(250,255,252));
                return;
            }
            if (SystemInformation.HighContrast) {
                e.Graphics.Clear(SystemColors.Control);
                return;
            }
            using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                ClientRectangle, Color.FromArgb(242,248,247), Color.FromArgb(233,247,238), 90F))
                e.Graphics.FillRectangle(brush, ClientRectangle);
            using(var glow=new GraphicsPath()) {
                glow.AddEllipse(Width*.39F,-Height*.5F,Width*.4F,Height*2);
                using(var light=new PathGradientBrush(glow)) {
                    light.CenterColor=Color.FromArgb(130,255,248,220);light.SurroundColors=new[]{Color.FromArgb(0,255,248,220)};
                    e.Graphics.FillPath(light,glow);
                }
            }
            MintLandscape.Draw(e.Graphics,ClientRectangle,true);

            using (var path = new System.Drawing.Drawing2D.GraphicsPath()) {
                float y = Height * .84F;
                path.StartFigure();
                path.AddBezier(0,y,Width*.24F,y-Height*.10F,Width*.43F,y+Height*.08F,Width*.66F,y-Height*.03F);
                path.AddBezier(Width*.66F,y-Height*.03F,Width*.82F,y-Height*.11F,Width*.94F,y+Height*.06F,Width,y-Height*.03F);
                path.AddLine(new PointF(Width,Height),new PointF(0,Height)); path.CloseFigure();
                using (var brush = new SolidBrush(Color.FromArgb(150,197,230,208))) e.Graphics.FillPath(brush,path);
            }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (String.IsNullOrEmpty(Title) || TitleFont == null || TitleBounds.Width <= 0) return;
            try {
                int titleHeight = Math.Max(TitleFont.Height,TitleBounds.Height/2);
                TextRenderer.DrawText(e.Graphics,Title,TitleFont,
                    new Rectangle(TitleBounds.Left,TitleBounds.Top,TitleBounds.Width,titleHeight),
                    MintTheme.Ink,TextFormatFlags.NoPadding|TextFormatFlags.Left|TextFormatFlags.SingleLine);
                if (!String.IsNullOrEmpty(Subtitle) && SubtitleFont != null)
                    TextRenderer.DrawText(e.Graphics,Subtitle,SubtitleFont,
                        new Rectangle(TitleBounds.Left,TitleBounds.Top+titleHeight,TitleBounds.Width,TitleBounds.Height-titleHeight),
                        SubtitleColor,TextFormatFlags.NoPadding|TextFormatFlags.Left|TextFormatFlags.SingleLine);
            } catch (ArgumentException) {
                // A closing or context-invalidated HWND may have already released its font DC.
            }
        }
    }

    internal sealed class MintArtControl : Control
    {
        private Bitmap picture;
        private readonly bool cardBackground;
        internal MintArtControl(string resourceName,bool cardBackground = false)
        {
            this.cardBackground = cardBackground;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = SystemInformation.HighContrast ? SystemColors.Control : Color.Transparent;
            if (SystemInformation.HighContrast) return;
            using (Stream stream = typeof(MintArtControl).Assembly.GetManifestResourceStream(resourceName)) {
                if (stream == null) throw new InvalidOperationException("Missing theme art: " + resourceName);
                using (Image image = Image.FromStream(stream)) picture = new Bitmap(image);
            }
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (picture == null || Width <= 0 || Height <= 0) return;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.DrawImage(picture,ClientRectangle,0,0,picture.Width,picture.Height,GraphicsUnit.Pixel);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && picture != null) { picture.Dispose(); picture = null; }
            base.Dispose(disposing);
        }
    }

    internal class MintCardLayoutPanel : TableLayoutPanel
    {
        private float visualScale = 1F;
        private string artResource;
        private Bitmap art;
        private Bitmap backgroundCanvas;
        private Size backgroundCanvasSize;
        private float backgroundCanvasScale;
        private Color backgroundCanvasParentColor;
        private string backgroundCanvasArt;
        private bool backgroundCanvasUnavailable;
        private const long MaximumCanvasBytes=4L*1024*1024;
        internal bool RoundedSurface = true;
        internal int ReferenceMinimumHeight;
        internal MintCardLayoutPanel(int columns)
        {
            ColumnCount = columns;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = SystemInformation.HighContrast ? SystemColors.Control : MintTheme.Card;
        }
        internal string ArtResource
        {
            get { return artResource; }
            set {
                if (artResource == value) return;
                artResource = value;
                backgroundCanvasUnavailable=false;
                ReleaseBackgroundCanvas();
                if (art != null) { art.Dispose(); art = null; }
                if (!SystemInformation.HighContrast && !String.IsNullOrEmpty(value)) {
                    using (Stream stream = typeof(MintCardLayoutPanel).Assembly.GetManifestResourceStream(value)) {
                        if (stream == null) throw new InvalidOperationException("Missing card art: " + value);
                        using (Image image = Image.FromStream(stream)) art = new Bitmap(image);
                    }
                }
                Invalidate();
            }
        }
        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        {
            backgroundCanvasUnavailable=false;
            ReleaseBackgroundCanvas();
            visualScale *= factor.Height; base.ScaleControl(factor,specified);
        }
        protected override void OnSizeChanged(EventArgs e)
        {
            backgroundCanvasUnavailable=false;
            ReleaseBackgroundCanvas();base.OnSizeChanged(e);
        }
        protected override void OnVisibleChanged(EventArgs e)
        {
            backgroundCanvasUnavailable=false;
            if(!Visible) ReleaseBackgroundCanvas();base.OnVisibleChanged(e);
        }
        private void ReleaseBackgroundCanvas()
        {
            if(backgroundCanvas!=null) { backgroundCanvas.Dispose();backgroundCanvas=null; }
        }
        public override Size GetPreferredSize(Size proposedSize)
        {
            Size result=base.GetPreferredSize(proposedSize);
            if(ReferenceMinimumHeight>0) result.Height=Math.Max(result.Height,ReferenceMinimumHeight);
            return result;
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Transparent descendants replay their parent's background. Reuse
            // only the static scene, never text, input fields or application data.
            Color parent=Parent==null ? MintTheme.Page : Parent.BackColor;
            if(parent==Color.Transparent)parent=MintTheme.Page;
            if(backgroundCanvasUnavailable || !RoundedSurface || SystemInformation.HighContrast || !Visible || Width<2 || Height<2 ||
                (long)Width*Height*4>MaximumCanvasBytes) {
                ReleaseBackgroundCanvas();PaintBackgroundScene(e);return;
            }
            if(backgroundCanvas==null || backgroundCanvasSize!=ClientSize || backgroundCanvasScale!=visualScale ||
                backgroundCanvasParentColor!=parent || backgroundCanvasArt!=artResource) {
                ReleaseBackgroundCanvas();
                Bitmap next=null;
                try {
                    next=new Bitmap(Width,Height,System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                    using(var graphics=Graphics.FromImage(next)) PaintBackgroundScene(new PaintEventArgs(graphics,ClientRectangle));
                    backgroundCanvas=next;next=null;
                    backgroundCanvasSize=ClientSize;backgroundCanvasScale=visualScale;
                    backgroundCanvasParentColor=parent;backgroundCanvasArt=artResource;
                } catch(OutOfMemoryException) {
                    backgroundCanvasUnavailable=true;
                    // The original drawing path remains usable without a cache.
                } finally { if(next!=null)next.Dispose(); }
            }
            // The cache contains device pixels. Parent-background replay can use
            // a different DPI/DC, so specify both rectangles and source units.
            if(backgroundCanvas!=null) e.Graphics.DrawImage(backgroundCanvas,ClientRectangle,0,0,backgroundCanvas.Width,backgroundCanvas.Height,GraphicsUnit.Pixel);
            else PaintBackgroundScene(e);
        }
        private void PaintBackgroundScene(PaintEventArgs e)
        {
            Color parentColor = Parent == null ? MintTheme.Page : Parent.BackColor;
            if (parentColor == Color.Transparent) parentColor = MintTheme.Page;
            try {
                if (ClientSize.Width <= 2 || ClientSize.Height <= 2) {
                    e.Graphics.Clear(parentColor);
                    return;
                }
                if (SystemInformation.HighContrast) {
                    e.Graphics.Clear(SystemColors.Control);
                    return;
                }
                if (!RoundedSurface) {
                    if(Parent!=null) {
                        var saved=e.Graphics.Save();
                        try { e.Graphics.TranslateTransform(-Left,-Top);InvokePaintBackground(Parent,new PaintEventArgs(e.Graphics,Bounds)); }
                        finally { e.Graphics.Restore(saved); }
                    } else e.Graphics.Clear(parentColor);
                    if (art != null) DrawArt(e.Graphics,ClientRectangle);
                    return;
                }
                e.Graphics.Clear(parentColor);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int inset = Math.Max(1,(int)Math.Round(visualScale));
                using (var path = SurfaceButton.Outline(Rectangle.Inflate(ClientRectangle,-inset,-inset),Math.Max(10,(int)Math.Round(28*visualScale)))) {
                    using (var brush = new LinearGradientBrush(Rectangle.Inflate(ClientRectangle,-inset,-inset),Color.FromArgb(249,251,250),Color.FromArgb(245,250,247),90F))
                        e.Graphics.FillPath(brush,path);
                    var clip=e.Graphics.Save();
                    e.Graphics.SetClip(path,CombineMode.Intersect);
                    MintLandscape.Draw(e.Graphics,ClientRectangle,false);
                    if (art != null) DrawArt(e.Graphics,Rectangle.Inflate(ClientRectangle,-inset,-inset));
                    e.Graphics.Restore(clip);
                    using (var pen = new Pen(Color.White,Math.Max(1.5F,visualScale*2))) e.Graphics.DrawPath(pen,path);
                    using(var inner=SurfaceButton.Outline(Rectangle.Inflate(ClientRectangle,-(int)Math.Round(8*visualScale),-(int)Math.Round(8*visualScale)),Math.Max(10,(int)Math.Round(24*visualScale))))
                    using(var pen=new Pen(Color.FromArgb(210,255,255,255),Math.Max(1F,visualScale))) e.Graphics.DrawPath(pen,inner);
                }
            } catch (ArgumentException) {
                // A closing HWND can invalidate its paint DC while erase is queued.
            }
        }
        private void DrawArt(Graphics graphics,Rectangle bounds)
        {
            bool footer = artResource != null && artResource.EndsWith("footer-flowers.png",StringComparison.Ordinal);
            bool leftCard = artResource != null && artResource.EndsWith("card-left-scene.png",StringComparison.Ordinal);
            float widthFactor = leftCard ? .40F : footer ? .19F : .20F;
            int width = Math.Max(1,(int)Math.Round(bounds.Width*widthFactor));
            int left = footer ? bounds.Left+(bounds.Width-width)/2 : bounds.Right-width;
            if (leftCard) left-=(int)Math.Round(32*visualScale);
            if (footer) {
                if (ColumnCount < 3) return;
                int[] columns = GetColumnWidths();
                if (columns == null || columns.Length < 3) return;
                // Flowers sit below the copy in the reference, spanning the free
                // center area. Transparent text tables let their lower leaves show.
                left=(int)Math.Round(bounds.Width*.46F);
                width=Math.Min(width,Math.Max(1,bounds.Width-columns[2]-left-8));
            }
            int height = Math.Max(1,(int)Math.Round(width*(art.Height/(float)art.Width)));
            int maximumHeight=Math.Max(1,(int)Math.Round(bounds.Height*(footer ? 1F : .48F)));
            if(height>maximumHeight) {
                height=maximumHeight;
                width=Math.Max(1,(int)Math.Round(height*art.Width/(float)art.Height));
            }
            int top = bounds.Bottom-height-(leftCard ? (int)Math.Round(12*visualScale) : 0);
            graphics.DrawImage(art,new Rectangle(left,top,width,height),0,0,art.Width,art.Height,GraphicsUnit.Pixel);
        }
        protected override void Dispose(bool disposing)
        {
            if(disposing) ReleaseBackgroundCanvas();
            if (disposing && art != null) { art.Dispose(); art = null; }
            base.Dispose(disposing);
        }
    }

    // Drawn glyphs use the preview's stroke language; the surrounding controls
    // keep their native focus, selection, notifications and accessible roles.
    internal class MintLabel : Label
    {
        internal MintLabel() { BackColor=Color.Transparent; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak;
            if (TextAlign == ContentAlignment.MiddleLeft || TextAlign == ContentAlignment.MiddleCenter) flags |= TextFormatFlags.VerticalCenter;
            if (TextAlign == ContentAlignment.TopCenter || TextAlign == ContentAlignment.MiddleCenter) flags |= TextFormatFlags.HorizontalCenter;
            if (!UseMnemonic) flags |= TextFormatFlags.NoPrefix;
            else if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
            if (AutoEllipsis) flags |= TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(e.Graphics,Text,Font,Rectangle.FromLTRB(Padding.Left,Padding.Top,Width-Padding.Right,Height-Padding.Bottom),
                Enabled ? ForeColor : SystemColors.GrayText,flags);
        }
    }

    internal static class MintGlyph
    {
        internal static void Draw(Graphics graphics, MintIconKind kind, Rectangle bounds, Color color)
        {
            if (kind == MintIconKind.None || bounds.Width < 4 || bounds.Height < 4) return;
            GraphicsState state = graphics.Save();
            try {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.TranslateTransform(bounds.Left,bounds.Top);
                float scale = Math.Min(bounds.Width,bounds.Height)/24F;
                graphics.ScaleTransform(scale,scale);
                using (var pen = new Pen(color,1.8F)) {
                    pen.StartCap = pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                    if (kind == MintIconKind.Paw) {
                        using (var brush = new SolidBrush(color)) {
                            graphics.FillEllipse(brush,3,8,7,7); graphics.FillEllipse(brush,14,8,7,7);
                            graphics.FillEllipse(brush,7,3,5,6); graphics.FillEllipse(brush,12,3,5,6);
                            graphics.FillEllipse(brush,9,12,7,8);
                        }
                    } else if (kind == MintIconKind.Shield) {
                        using (var path = new GraphicsPath()) {
                            path.AddLines(new[] {new PointF(12,2),new PointF(21,6),new PointF(19,16),
                                new PointF(12,22),new PointF(5,16),new PointF(3,6),new PointF(12,2)});
                            using (var brush = new SolidBrush(Color.FromArgb(255,226,122))) graphics.FillPath(brush,path);
                            using(var light=new SolidBrush(Color.FromArgb(255,248,209))) graphics.FillRectangle(light,8,6,4,11);
                            graphics.DrawPath(pen,path);
                        }
                    } else if (kind == MintIconKind.Stack) {
                        graphics.DrawPolygon(pen,new[] {new PointF(12,3),new PointF(21,8),new PointF(12,13),new PointF(3,8)});
                        graphics.DrawLines(pen,new[] {new PointF(3,12),new PointF(12,17),new PointF(21,12)});
                        graphics.DrawLines(pen,new[] {new PointF(3,16),new PointF(12,21),new PointF(21,16)});
                    } else if (kind == MintIconKind.Display) {
                        graphics.DrawRectangle(pen,3,4,18,12);
                        graphics.DrawLine(pen,9,20,15,20); graphics.DrawLine(pen,12,16,12,20);
                    } else if (kind == MintIconKind.Keyboard) {
                        graphics.DrawRectangle(pen,2,5,20,14);
                        using(var brush=new SolidBrush(color)) for (int x=5;x<=18;x+=4) { graphics.FillRectangle(brush,x,8,2,2);graphics.FillRectangle(brush,x,12,2,2); }
                        graphics.DrawLine(pen,6,15,18,15);
                    } else if (kind == MintIconKind.Language) {
                        graphics.DrawLines(pen,new[]{new PointF(13,4),new PointF(3,4),new PointF(2,19),new PointF(11,19)});
                        using(var font=new Font("Segoe UI",13,FontStyle.Bold,GraphicsUnit.Pixel)) {
                            TextRenderer.DrawText(graphics,"A",font,new Rectangle(3,5,12,16),color,TextFormatFlags.NoPadding);
                        }
                        graphics.DrawLine(pen,14,9,22,9);graphics.DrawLine(pen,18,6,18,9);
                        graphics.DrawLines(pen,new[]{new PointF(21,9),new PointF(18,15),new PointF(13,20)});
                        graphics.DrawLine(pen,14,12,22,20);
                    } else if (kind == MintIconKind.Clock) {
                        graphics.DrawEllipse(pen,3,3,18,18);
                        graphics.DrawLine(pen,12,7,12,12); graphics.DrawLine(pen,12,12,16,14);
                    }
                }
            } finally {
                try { graphics.Restore(state); }
                catch (ArgumentException) { /* A closing HWND can invalidate paint state after drawing. */ }
            }
        }
    }

    internal sealed class MintIconLabel : MintLabel
    {
        private MintIconKind iconKind;
        private float visualScale=1F;
        internal MintIconKind IconKind
        {
            get { return iconKind; }
            set {
                iconKind = value;
                Padding = new Padding((int)Math.Round((value == MintIconKind.None ? 0 : value == MintIconKind.Paw || value == MintIconKind.Shield ? 58 : 40)*visualScale),0,0,0);
                Invalidate();
            }
        }
        internal MintIconLabel()
        {
            MinimumSize = new Size(0,28);
            TextAlign = ContentAlignment.MiddleLeft;
            BackColor = Color.Transparent;
        }
        public override Size GetPreferredSize(Size proposedSize)
        {
            Size preferred = base.GetPreferredSize(proposedSize);
            preferred.Height = Math.Max(preferred.Height,(int)Math.Round((IconKind==MintIconKind.Paw || IconKind==MintIconKind.Shield ? 38 : 28)*visualScale));
            return preferred;
        }
        protected override void ScaleControl(SizeF factor,BoundsSpecified specified)
        { visualScale*=factor.Height; base.ScaleControl(factor,specified); }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (!SystemInformation.HighContrast && IconKind != MintIconKind.None) {
                bool title=IconKind==MintIconKind.Paw || IconKind==MintIconKind.Shield;
                int size=(int)Math.Round(28*visualScale);
                var badge = new Rectangle(0,(Height-(int)(38*visualScale))/2,(int)(52*visualScale),(int)(38*visualScale));
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                if(title) using (var brush = new SolidBrush(MintTheme.MintWash)) e.Graphics.FillEllipse(brush,badge);
                MintGlyph.Draw(e.Graphics,IconKind,new Rectangle(title ? (badge.Width-size)/2 : 0,(Height-size)/2,size,size),
                    IconKind==MintIconKind.Stack ? MintTheme.Ink : MintTheme.Green);
            }
            base.OnPaint(e);
        }
    }

    internal sealed class MintStatusLabel : MintLabel
    {
        private float visualScale = 1F;
        private bool adjustingHeight;
        internal MintStatusLabel()
        {
            AutoSize = false;
            Height = 34;
            Padding = new Padding(48,0,10,0);
            TextAlign = ContentAlignment.MiddleLeft;
            BackColor = Color.Transparent;
        }
        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        {
            visualScale *= factor.Height;
            base.ScaleControl(factor,specified);
            RefreshHeight();
        }
        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            RefreshHeight();
        }
        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            RefreshHeight();
        }
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            RefreshHeight();
        }
        private void RefreshHeight()
        {
            if (adjustingHeight || IsDisposed || Disposing || Width <= Padding.Horizontal || Font == null) return;
            Size measured = TextRenderer.MeasureText(Text ?? "",Font,
                new Size(Math.Max(1,Width-Padding.Horizontal),0),
                TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
            int minimum = Math.Max(1,(int)Math.Round(36*visualScale));
            int next = Math.Max(minimum,measured.Height+(int)Math.Round(8*visualScale));
            if (Height == next) return;
            adjustingHeight = true;
            try { Height = next; } finally { adjustingHeight = false; }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (SystemInformation.HighContrast) { base.OnPaint(e); return; }
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = SurfaceButton.Outline(ClientRectangle,8))
            using (var brush = new SolidBrush(Color.FromArgb(225,249,226))) e.Graphics.FillPath(brush,path);
            using (var pen = new Pen(MintTheme.Green,2F*visualScale)) {
                pen.StartCap = pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                e.Graphics.DrawLines(pen,new[] {new PointF(16*visualScale,Height*.50F),new PointF(21*visualScale,Height*.65F),new PointF(31*visualScale,Height*.35F)});
            }
            base.OnPaint(e);
        }
    }
    internal sealed class MintInfoMark : Control
    {
        internal MintInfoMark() {
            SetStyle(ControlStyles.SupportsTransparentBackColor,true);
            BackColor=Color.Transparent;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float unit = Math.Min(Width,Height)/24F;
            using (var pen = new Pen(MintTheme.Muted,1.5F*unit)) {
                e.Graphics.DrawEllipse(pen,2*unit,2*unit,20*unit,20*unit);
                e.Graphics.DrawLine(pen,12*unit,11*unit,12*unit,17*unit);
            }
            using (var brush = new SolidBrush(MintTheme.Muted)) e.Graphics.FillEllipse(brush,11*unit,6*unit,2*unit,2*unit);
        }
    }
    internal sealed class MintRefreshLink : LinkLabel
    {
        private float visualScale=1F;
        internal MintRefreshLink() { Padding = new Padding(36,0,0,0); MinimumSize = new Size(0,34); TextAlign = ContentAlignment.MiddleLeft; BackColor = Color.Transparent; }
        protected override void ScaleControl(SizeF factor,BoundsSpecified specified) { visualScale *= factor.Height; base.ScaleControl(factor,specified); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float unit=24*visualScale/24F, top=(Height-24*visualScale)/2;
            using (var pen=new Pen(Enabled ? LinkColor : SystemColors.GrayText,2F*visualScale)) {
                pen.StartCap=pen.EndCap=System.Drawing.Drawing2D.LineCap.Round;
                pen.LineJoin=System.Drawing.Drawing2D.LineJoin.Round;
                e.Graphics.DrawArc(pen,3*unit,top+3*unit,18*unit,18*unit,200,145);
                e.Graphics.DrawArc(pen,3*unit,top+3*unit,18*unit,18*unit,20,145);
                e.Graphics.DrawLines(pen,new[] {new PointF(21*unit,top+3*unit),new PointF(21*unit,top+9*unit),new PointF(15*unit,top+9*unit)});
                e.Graphics.DrawLines(pen,new[] {new PointF(3*unit,top+21*unit),new PointF(3*unit,top+15*unit),new PointF(9*unit,top+15*unit)});
            }
        }
    }

    internal sealed class MintStatusMark : Control
    {
        internal bool Ready;
        internal MintStatusMark() { SetStyle(ControlStyles.SupportsTransparentBackColor,true);BackColor=Color.Transparent; }
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
        internal MintCheckBox() {
            BackColor=Color.Transparent;
            SetStyle(ControlStyles.OptimizedDoubleBuffer|ControlStyles.AllPaintingInWmPaint,true);
        }
        private float visualScale = 1F;
        private int BoxSize { get { return Math.Max(Font.Height+2,(int)Math.Round(22*visualScale)); } }
        protected override void ScaleControl(SizeF factor,BoundsSpecified specified) { visualScale *= factor.Height; base.ScaleControl(factor,specified); }
        public override Size GetPreferredSize(Size proposedSize)
        {
            if(SystemInformation.HighContrast) return base.GetPreferredSize(proposedSize);
            int maximum = MaximumSize.Width > 0 ? MaximumSize.Width : Int32.MaxValue;
            int available = Math.Max(1,maximum-BoxSize-10);
            Size text=TextRenderer.MeasureText(Text,Font,new Size(available,0),TextFormatFlags.WordBreak|TextFormatFlags.NoPadding);
            return new Size(Math.Max(MinimumSize.Width,Math.Min(maximum,BoxSize+10+text.Width)),Math.Max(MinimumSize.Height,Math.Max(BoxSize,text.Height)+4));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            if(SystemInformation.HighContrast) { base.OnPaint(e); return; }
            if(Parent!=null) {
                var saved=e.Graphics.Save();
                try { e.Graphics.TranslateTransform(-Left,-Top);InvokePaintBackground(Parent,new PaintEventArgs(e.Graphics,Bounds)); }
                finally { e.Graphics.Restore(saved); }
            } else e.Graphics.Clear(MintTheme.Card);
            e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int box=BoxSize; var bounds=new Rectangle(0,(Height-box)/2,box,box);
            using(var path=SurfaceButton.Outline(bounds,4)) {
                using(var brush=new SolidBrush(Checked ? MintTheme.Green : MintTheme.Paper)) e.Graphics.FillPath(brush,path);
                if(!Checked) using(var pen=new Pen(MintTheme.Line)) e.Graphics.DrawPath(pen,path);
            }
            if(Checked) using(var pen=new Pen(Enabled ? Color.White : SystemColors.GrayText,Math.Max(1.5F,box/12F))) {
                e.Graphics.DrawLines(pen,new[] { new PointF(box*.23F,bounds.Top+box*.5F),new PointF(box*.43F,bounds.Top+box*.7F),new PointF(box*.78F,bounds.Top+box*.28F) });
            }
            var flags=TextFormatFlags.NoPadding|TextFormatFlags.VerticalCenter|TextFormatFlags.WordBreak;
            if(!ShowKeyboardCues) flags|=TextFormatFlags.HidePrefix;
            TextRenderer.DrawText(e.Graphics,Text,Font,new Rectangle(box+10,0,Math.Max(1,Width-box-10),Height),Enabled ? ForeColor : SystemColors.GrayText,flags);
            if(Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(ClientRectangle,-1,-1));
        }
    }
    internal sealed class MintComboBox : ComboBox
    {
        private float itemScale=1F;
        private Image selectedImage;
        // Borrowed from ApplicationIconCache. The cache, never this control, owns it.
        internal Image SelectedImage { get { return selectedImage; } set { if(!Object.ReferenceEquals(selectedImage,value)) { selectedImage=value;Invalidate(); } } }
        internal MintComboBox() { AutoSize=true; DrawMode=DrawMode.OwnerDrawFixed; RefreshItemHeight(); }
        public override Size GetPreferredSize(Size proposedSize)
        {
            Size preferred=base.GetPreferredSize(proposedSize);
            // OwnerDrawFixed reserves one item plus the native six-pixel frame.
            // Avoid measuring the explicit pixel font again through a point DC.
            preferred.Height=ItemHeight+6;
            return preferred;
        }
        private void RefreshItemHeight() {
            // Framework's owner-drawn DropDownList adds six pixels around the item.
            ItemHeight=Math.Max(Font.Height,(int)Math.Round(39*itemScale)-6);
        }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); RefreshItemHeight(); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); RefreshItemHeight(); }
        protected override void ScaleControl(SizeF factor,BoundsSpecified specified) {
            itemScale*=factor.Height; base.ScaleControl(factor,specified); RefreshItemHeight();
        }
        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void CreateHandle()
        {
            using(SettingsDpiContext.EnterUi()) {
                // A combo can acquire an orphan HWND while items/fonts initialize.
                // Once attached, ensure the actual parent exists before creation.
                if(Parent != null && FindForm() != null) { IntPtr parentHandle=Parent.Handle; }
                base.CreateHandle();
            }
        }
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if(Visible && Parent != null && FindForm() != null && IsHandleCreated && !SettingsDpiContext.IsUiWindow(Handle)) {
                // Framework's DPI-unaware parking window can lend its context to
                // an orphan ComboBox. Recreate only that HWND after attachment.
                using(SettingsDpiContext.EnterUi()) { RecreateHandle(); Parent.PerformLayout(); }
            }
        }
        protected override void WndProc(ref Message message)
        {
            using(SettingsDpiContext.EnterUi()) {
                if(!SystemInformation.HighContrast && (message.Msg==0x000F || message.Msg==0x0318)) {
                    if(message.Msg==0x0318) {
                        if(message.WParam!=IntPtr.Zero) using(var graphics=Graphics.FromHdc(message.WParam)) DrawClosedFace(graphics);
                    } else {
                        PaintState state;IntPtr dc=BeginPaint(Handle,out state);
                        try { if(dc!=IntPtr.Zero) using(var graphics=Graphics.FromHdc(dc)) DrawClosedFace(graphics); }
                        finally { EndPaint(Handle,ref state); }
                    }
                    message.Result=IntPtr.Zero;return;
                }
                base.WndProc(ref message);
            }
        }
        private void DrawClosedFace(Graphics graphics)
        {
            if(Width<3 || Height<3) return;
            // One paint DC and the existing native ComboBox HWND: popup, keyboard,
            // selection, accessibility and notifications remain native.
            var saved=graphics.Save();
            try {
                if(Parent!=null) {
                    graphics.TranslateTransform(-Left,-Top);
                    InvokePaintBackground(Parent,new PaintEventArgs(graphics,Bounds));
                    graphics.TranslateTransform(Left,Top);
                } else graphics.Clear(MintTheme.Card);
                graphics.SmoothingMode=SmoothingMode.AntiAlias;
                var bounds=Rectangle.Inflate(ClientRectangle,-1,-1);
                using(var path=SurfaceButton.Outline(bounds,(int)Math.Round(8*itemScale))) {
                    using(var brush=new LinearGradientBrush(bounds,Color.White,Color.FromArgb(246,249,250),90F)) graphics.FillPath(brush,path);
                    using(var pen=new Pen(Focused ? MintTheme.Green : MintTheme.Field,Math.Max(1,itemScale))) graphics.DrawPath(pen,path);
                }
                int pad=(int)Math.Round(13*itemScale), arrow=(int)Math.Round(29*itemScale);
                var caption=new Rectangle(pad,0,Math.Max(1,Width-pad-arrow),Height);
                if(selectedImage!=null) {
                    int size=(int)Math.Round(26*itemScale);
                    graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(selectedImage,new Rectangle(pad,(Height-size)/2,size,size));
                    caption.X+=size+(int)Math.Round(15*itemScale);caption.Width-=size+(int)Math.Round(15*itemScale);
                }
                DrawCaption(graphics,SelectedIndex<0 ? Text : GetItemText(SelectedItem),Font,caption,Enabled ? ForeColor : SystemColors.GrayText);
                float x=Width-17*itemScale,y=Height/2F,r=5*itemScale;
                using(var pen=new Pen(Enabled ? MintTheme.Ink : SystemColors.GrayText,2*itemScale)) {
                    pen.StartCap=pen.EndCap=LineCap.Round;
                    graphics.DrawLines(pen,new[]{new PointF(x-r,y-r/2),new PointF(x,y+r/2),new PointF(x+r,y-r/2)});
                }
                if(Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(graphics,Rectangle.Inflate(ClientRectangle,-4,-4));
            } finally { graphics.Restore(saved); }
        }
        [StructLayout(LayoutKind.Sequential)] private struct PaintState {
            internal IntPtr Hdc;internal int Erase;internal RectangleFields Bounds;internal int Restore,Update;
            [MarshalAs(UnmanagedType.ByValArray,SizeConst=32)] internal byte[] Reserved;
        }
        [StructLayout(LayoutKind.Sequential)] private struct RectangleFields { internal int Left,Top,Right,Bottom; }
        [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd,out PaintState state);
        [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd,ref PaintState state);
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            bool selected=(e.State & DrawItemState.Selected)!=0 && (e.State & DrawItemState.ComboBoxEdit)==0;
            Color background=selected ? SystemColors.Highlight : BackColor;
            Color foreground=!Enabled ? SystemColors.GrayText : selected ? SystemColors.HighlightText : ForeColor;
            using(var brush=new SolidBrush(background)) e.Graphics.FillRectangle(brush,e.Bounds);
            string caption=e.Index>=0 && e.Index<Items.Count ? GetItemText(Items[e.Index]) : Text;
            Rectangle bounds=e.Bounds; bounds.Inflate(-(int)Math.Round(13*itemScale),0);
            DrawCaption(e.Graphics,caption,e.Font,bounds,foreground);
            e.DrawFocusRectangle(); base.OnDrawItem(e);
        }
        private static void DrawCaption(Graphics graphics,string caption,Font font,Rectangle bounds,Color color)
        {
            // GDI TextRenderer converts a pixel font back through its cached
            // point-font DC. GDI+ respects its explicit pixel size on this HWND.
            using(var format=new StringFormat(StringFormat.GenericTypographic))
            using(var brush=new SolidBrush(color)) {
                format.LineAlignment=StringAlignment.Center;
                format.FormatFlags|=StringFormatFlags.NoWrap;
                format.Trimming=StringTrimming.EllipsisCharacter;
                graphics.DrawString(caption,font,brush,bounds,format);
            }
        }
        internal int MeasureItemWidth(string caption)
        {
            using(var graphics=CreateGraphics())
            using(var format=new StringFormat(StringFormat.GenericTypographic)) {
                format.FormatFlags|=StringFormatFlags.NoWrap;
                return (int)Math.Ceiling(graphics.MeasureString(caption,Font,Int32.MaxValue,format).Width)
                    + (int)Math.Ceiling(26*itemScale);
            }
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
            minus.Enter += delegate { Invalidate(); }; minus.Leave += delegate { Invalidate(); };
            plus.Enter += delegate { Invalidate(); }; plus.Leave += delegate { Invalidate(); };
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); UpdateButtons();
        }
        private void UpdateButtons() { minus.Enabled = number.Enabled && number.Valid && number.Value > number.Minimum; plus.Enabled = number.Enabled && number.Valid && number.Value < number.Maximum; }
        protected override void ScaleControl(SizeF factor,BoundsSpecified specified) { visualScale *= factor.Height; base.ScaleControl(factor,specified); }
        protected override void OnLayout(LayoutEventArgs e) {
            base.OnLayout(e); if (number == null || minus == null || plus == null) return;
            // Opaque child HWNDs remain inside the rounded stroke, including at focus.
            // Reserve the full focused stroke at every DPI, even when unfocused.
            // Child HWNDs must not overpaint its inner half or change bounds on Tab.
            int inset = Units(2)+1, innerHeight = Math.Max(1,Height-inset*2);
            int edge = Units(Width < Units(120) ? 30 : Width < Units(150) ? 35 : 41);
            minus.SetBounds(inset+Units(3),inset,edge-inset-Units(3),innerHeight); plus.SetBounds(Width-edge,inset,edge-inset-Units(3),innerHeight);
            number.SetBounds(edge+Units(2), Math.Max(inset,(Height-number.PreferredHeight)/2),
                Math.Max(1,Width-(edge+Units(2))*2),number.PreferredHeight);
        }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e); e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = SurfaceButton.Outline(ClientRectangle,Units(5)))
            using (var brush = new SolidBrush(MintTheme.Soft)) e.Graphics.FillPath(brush,path);
            int edge = Units(Width < Units(120) ? 30 : Width < Units(150) ? 35 : 41);
            int childInset = Units(2)+1;
            using (var brush = new SolidBrush(Enabled ? MintTheme.Paper : MintTheme.Soft)) e.Graphics.FillRectangle(brush,edge,childInset,Math.Max(1,Width-edge*2),Math.Max(1,Height-childInset*2));
            float stroke = (ContainsFocus ? 2F : 1F)*visualScale;
            int inset = Math.Max(1,(int)Math.Ceiling(stroke/2));
            using (var path = SurfaceButton.Outline(Rectangle.Inflate(ClientRectangle,-inset+1,-inset+1),Units(6)))
            using (var pen = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : ContainsFocus ? MintTheme.Green : MintTheme.Field,stroke)) e.Graphics.DrawPath(pen,path);
            using (var pen = new Pen(MintTheme.Field)) {
                pen.Width = Math.Max(1F,visualScale);
                e.Graphics.DrawLine(pen,minus.Right+Units(1),childInset,minus.Right+Units(1),Height-childInset);
                e.Graphics.DrawLine(pen,plus.Left-Units(1),childInset,plus.Left-Units(1),Height-childInset);
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

