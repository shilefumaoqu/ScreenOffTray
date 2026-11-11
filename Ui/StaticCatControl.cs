using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace ScreenOffTray.Ui
{
    // A static avatar cropped from the approved neutral-pose artwork.
    // No sprite sheets, timers, input actions or global event subscriptions.
    internal sealed class StaticCatControl : Control
    {
        private Bitmap picture;
        private bool english;

        internal bool English
        {
            get { return english; }
            set { english = value; AccessibleName = value ? "Cat" : "小猫"; }
        }

        internal StaticCatControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = SystemInformation.HighContrast ? SystemColors.Control : Color.Transparent;
            Size = new Size(162, 87);
            TabStop = false;
            AccessibleRole = AccessibleRole.Graphic;
            English = false;
            using (Stream stream = typeof(StaticCatControl).Assembly.GetManifestResourceStream(
                "ScreenOffTray.Ui.Resources.theme.header-cat.png")) {
                if (stream == null) throw new InvalidOperationException("Missing static cat resource.");
                using (Image image = Image.FromStream(stream)) picture = new Bitmap(image);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (picture == null || Width <= 0 || Height <= 0) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using(var brush=new SolidBrush(Color.FromArgb(180,194,233,211))) e.Graphics.FillEllipse(brush,new Rectangle(0,Height/10,Width*9/10,Height*9/10));
            e.Graphics.DrawImage(picture,ClientRectangle,0,0,picture.Width,picture.Height,GraphicsUnit.Pixel);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && picture != null) {
                Bitmap owned = picture;
                picture = null;
                owned.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
