using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using ScreenOffTray.Ui;

internal static class StaticCatTests
{
    private const BindingFlags Fields = BindingFlags.NonPublic | BindingFlags.Instance;
    private static int checks;
    private static string output;

    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException(name);
        checks++;
        Console.WriteLine("PASS " + name);
    }

    private static Bitmap Render(StaticCatControl cat)
    {
        Bitmap image = new Bitmap(cat.Width, cat.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(image)) {
            graphics.Clear(Color.Transparent);
            typeof(StaticCatControl).GetMethod("OnPaint", Fields).Invoke(cat,
                new object[] { new PaintEventArgs(graphics, cat.ClientRectangle) });
        }
        return image;
    }

    private static Rectangle Ink(Bitmap image)
    {
        int left = image.Width, top = image.Height, right = -1, bottom = -1;
        for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++) {
            if (image.GetPixel(x,y).A <= 24) continue;
            left = Math.Min(left,x); top = Math.Min(top,y);
            right = Math.Max(right,x); bottom = Math.Max(bottom,y);
        }
        return right < left ? Rectangle.Empty : Rectangle.FromLTRB(left,top,right+1,bottom+1);
    }

    private static bool SamePixels(Bitmap first, Bitmap second)
    {
        if (first.Size != second.Size) return false;
        for (int y = 0; y < first.Height; y++) for (int x = 0; x < first.Width; x++)
            if (first.GetPixel(x,y) != second.GetPixel(x,y)) return false;
        return true;
    }

    [STAThread] private static int Main(string[] args)
    {
        try {
            output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            string[] resources = typeof(StaticCatControl).Assembly.GetManifestResourceNames();
            Check(resources.Length == 1 && resources[0].EndsWith("theme.header-cat.png"), "only the reference-derived cat avatar is embedded");
            using (var cat = new StaticCatControl()) {
                Check(cat.AccessibleRole == AccessibleRole.Graphic && !cat.TabStop, "decorative cat has no button semantics or tab stop");
                Check(cat.AccessibleName == "小猫", "Chinese image name");
                cat.English = true;
                Check(cat.AccessibleName == "Cat", "English image name");
                FieldInfo[] fields = typeof(StaticCatControl).GetFields(Fields);
                Check(!fields.Any(f => f.FieldType == typeof(System.Windows.Forms.Timer) || f.FieldType == typeof(System.Threading.Timer)), "no animation timer is retained");
                Bitmap owned = (Bitmap)typeof(StaticCatControl).GetField("picture",Fields).GetValue(cat);
                Check(owned.Size == new Size(202,194) && owned.Width * owned.Height * 4 < 256 * 1024, "one static image has less than 256 KiB of pixel storage");
                foreach (Size size in new[] {new Size(162,87),new Size(100,70),new Size(243,131),new Size(324,174),new Size(486,261)}) {
                    cat.Size = size;
                    using (Bitmap first = Render(cat)) using (Bitmap second = Render(cat)) {
                        Rectangle ink = Ink(first);
                        Check(!ink.IsEmpty && ink.Left >= 0 && ink.Top >= 0 && ink.Right <= size.Width && ink.Bottom <= size.Height,
                            "complete neutral pose stays inside canvas " + size);
                        Check(SamePixels(first,second), "repeated painting is static " + size);
                        first.Save(Path.Combine(output,"cat-" + size.Width + "x" + size.Height + ".png"),ImageFormat.Png);
                    }
                }
                cat.Size = new Size(162,87);
                using (Bitmap first = Render(cat)) {
                    typeof(Control).GetMethod("OnClick",Fields).Invoke(cat,new object[] {EventArgs.Empty});
                    typeof(Control).GetMethod("OnMouseMove",Fields).Invoke(cat,new object[] {new MouseEventArgs(MouseButtons.None,0,150,40,0)});
                    Thread.Sleep(250); Application.DoEvents();
                    using (Bitmap second = Render(cat)) Check(SamePixels(first,second), "click, hover and elapsed time do not change the pose");
                }
                Check(!cat.IsHandleCreated, "pure drawing creates no native window");
                cat.Dispose(); cat.Dispose();
                Check(typeof(StaticCatControl).GetField("picture",Fields).GetValue(cat) == null, "Dispose releases ownership and is idempotent");
                bool released = false;
                try { int ignored = owned.Width; } catch (ArgumentException) { released = true; }
                Check(released, "owned Bitmap is actually disposed");
            }
            Console.WriteLine("RESULT checks=" + checks + " failures=0; pure control rendering, no product entry, settings, audio or display calls.");
            return 0;
        } catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
}
