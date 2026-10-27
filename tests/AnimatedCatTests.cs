using System;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using ScreenOffTray.Ui;

internal static class AnimatedCatTests
{
    static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static object Get(object o, string name) { return o.GetType().GetField(name,Flags).GetValue(o); }
    static void Set(object o, string name, object value) { o.GetType().GetField(name,Flags).SetValue(o,value); }
    static object Call(object o, string name, params object[] args) { return o.GetType().GetMethod(name,Flags).Invoke(o,args); }
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    [STAThread] static void Main() { try { Run(); } catch (Exception ex) { Console.WriteLine("FAIL: " + ex.Message + " / " + (ex.InnerException == null ? "" : ex.InnerException.Message)); Environment.Exit(1); } }
    static void Run()
    {
        var c = new AnimatedCatControl();
        var actionImage = (Bitmap)Get(c,"actions"); var varietyImage = (Bitmap)Get(c,"variety");
        Check(actionImage.Width == 1774 && actionImage.Height == 887 && varietyImage.Width == 1448 && varietyImage.Height == 1086,"resources");
        Set(c,"firstIdle",true); Call(c,"ScheduleIdle",1000L); Check((long)Get(c,"nextIdle") >= 7000 && (long)Get(c,"nextIdle") <= 10000,"first idle range");
        Set(c,"firstIdle",false); Call(c,"ScheduleIdle",1000L); Check((long)Get(c,"nextIdle") >= 13000 && (long)Get(c,"nextIdle") <= 23000,"later idle range");
        int previous = -1, mask = 0;
        for (int i = 0; i < 400; i++) {
            int choice = (int)Call(c,"ChooseAction"); Check(choice != previous,"adjacent repeated"); previous = choice; mask |= 1 << choice;
            if (i % 4 == 3) { Check(mask == 15,"shuffle bag"); mask = 0; }
        }
        int[][] expectedDurations = { new[] {180,280,560,280,200}, new[] {160,220,300,220,300,220,260,320}, new[] {180,280,350,280,180,230}, new[] {160,190,150,120,190,300,220} };
        int[][] frames = (int[][])typeof(AnimatedCatControl).GetField("Frames",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
        int[][] durations = (int[][])typeof(AnimatedCatControl).GetField("Durations",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
        for (int a = 0; a < 4; a++) {
            Set(c,"current",a); Set(c,"started",1000L); Set(c,"queued",false);
            long t = 1000;
            for (int i = 0; i < frames[a].Length; i++) {
                Call(c,"Advance",t); int n = frames[a][i], expected = n == 0 ? 0 : a == 3 ? n : a * 4 + n;
                Check((int)Get(c,"frame") == expected && (bool)Get(c,"paw") == (a == 3 && n != 0),"sequence " + a + "/" + i);
                Check(durations[a][i] == expectedDurations[a][i], "duration"); t += durations[a][i];
            }
            Call(c,"Advance",t); Check((int)Get(c,"current") == -1,"sequence ends");
        }
        var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-3000,-3000), ClientSize = new Size(200,100) };
        form.Controls.Add(c); form.Show(); Application.DoEvents();
        bool motion = ((System.Windows.Forms.Timer)Get(c,"timer")).Enabled;
        if (motion) {
            Call(c,"OnKeyDown",new KeyEventArgs(Keys.Enter)); Check((int)Get(c,"current") >= 0,"enter"); Call(c,"OnKeyUp",new KeyEventArgs(Keys.Enter));
            Set(c,"current",-1); Call(c,"OnKeyDown",new KeyEventArgs(Keys.Space)); Check((int)Get(c,"current") >= 0,"space"); Call(c,"OnKeyUp",new KeyEventArgs(Keys.Space));
            typeof(Control).GetMethod("RecreateHandle",Flags).Invoke(c,null); Application.DoEvents(); Check(((System.Windows.Forms.Timer)Get(c,"timer")).Enabled,"handle recreation");
            Set(c,"current",0); Set(c,"started",0L); Set(c,"queued",false);
            Call(c,"OnClick",EventArgs.Empty); Call(c,"OnClick",EventArgs.Empty);
            Check((bool)Get(c,"queued"),"queue once");
            Call(c,"Advance",100000L); Check(!(bool)Get(c,"queued") && (int)Get(c,"current") >= 0,"one queued action");
            Call(c,"Advance",200000L); Check((int)Get(c,"current") == -1,"queue drained");
            c.Visible = false; Check(!((System.Windows.Forms.Timer)Get(c,"timer")).Enabled,"hidden timer");
            c.Visible = true; Application.DoEvents(); Check(((System.Windows.Forms.Timer)Get(c,"timer")).Enabled,"visible timer");
            form.WindowState = FormWindowState.Minimized; Application.DoEvents(); Check(!((System.Windows.Forms.Timer)Get(c,"timer")).Enabled,"minimized timer");
            form.WindowState = FormWindowState.Normal; Application.DoEvents(); Check(((System.Windows.Forms.Timer)Get(c,"timer")).Enabled,"restored timer");
            Set(c,"nextIdle", ((System.Diagnostics.Stopwatch)Get(c,"clock")).ElapsedMilliseconds + 250L);
            var until = DateTime.UtcNow.AddMilliseconds(600); bool sawAction = false;
            while (DateTime.UtcNow < until) { Application.DoEvents(); if ((int)Get(c,"current") >= 0) sawAction = true; Thread.Sleep(10); }
            Check(sawAction,"natural timer tick");
        }
        var originalMotion = (Func<bool>)Get(c,"motionAllowed"); Set(c,"motionAllowed",(Func<bool>)(() => false)); Call(c,"SyncMotion");
        Check(!((System.Windows.Forms.Timer)Get(c,"timer")).Enabled,"disabled motion timer"); Call(c,"OnClick",EventArgs.Empty); Check((int)Get(c,"frame") != 0,"disabled motion click feedback");
        Set(c,"motionAllowed",originalMotion); Call(c,"SyncMotion");
        foreach (var size in new[] {new Size(110,60),new Size(165,90),new Size(220,120)}) {
            c.Size = size; using (var image = new Bitmap(size.Width,size.Height)) { c.DrawToBitmap(image,new Rectangle(Point.Empty,size)); }
        }
        form.Close(); Application.DoEvents(); Check(!((System.Windows.Forms.Timer)Get(c,"timer")).Enabled,"closed timer");
        c.Dispose(); form.Dispose();
        Check(Get(c,"actions") == null && Get(c,"variety") == null,"bitmap release");
        var natural = new AnimatedCatControl(); var window = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-3000,-3000) };
        window.Controls.Add(natural); window.Show(); Application.DoEvents();
        bool naturalMotion = ((System.Windows.Forms.Timer)Get(natural,"timer")).Enabled;
        if (naturalMotion) {
            var watch = System.Diagnostics.Stopwatch.StartNew(); bool started = false;
            while (watch.ElapsedMilliseconds < 9300) { Application.DoEvents(); if ((int)Get(natural,"current") >= 0) { started = true; break; } Thread.Sleep(10); }
            Check(started && watch.ElapsedMilliseconds <= 9300 && ((System.Diagnostics.Stopwatch)Get(natural,"clock")).ElapsedMilliseconds >= (long)Get(natural,"nextIdle"),"first natural idle"); Console.WriteLine("Natural first idle: " + watch.ElapsedMilliseconds + "ms");
        }
        window.Close(); window.Dispose(); natural.Dispose();
        Console.WriteLine("PASS: resources, 400 shuffle choices, 4 sequences, sizes 100/150/200%, close/dispose; motion=" + motion + (motion ? ", queue, visibility, minimize, natural timer" : " (system animation disabled: motion checks skipped)"));
    }
}






