using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using ScreenOffTray.Ui;

internal static class AnimatedCatFocusedTests
{
    const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    static void Check(bool value,string text) { if (!value) throw new Exception(text); }
    static void Set(object o,string field,object value) { o.GetType().GetField(field,F).SetValue(o,value); }
    static object Get(object o,string field) { return o.GetType().GetField(field,F).GetValue(o); }
    static Rectangle Render(AnimatedCatControl c, int n, bool paw)
    {
        Set(c,"frame",n); Set(c,"paw",paw);
        using (var image = new Bitmap(150,100)) using (var g = Graphics.FromImage(image)) {
            g.Clear(Color.Transparent); g.TranslateTransform(20,20);
            typeof(AnimatedCatControl).GetMethod("OnPaint",F).Invoke(c,new object[]{new PaintEventArgs(g,new Rectangle(-20,-20,150,100))});
            int left=150, top=100, right=-1, bottom=-1;
            for(int y=0;y<100;y++) for(int x=0;x<150;x++) if(image.GetPixel(x,y).A>10) {
                left=Math.Min(left,x); right=Math.Max(right,x); top=Math.Min(top,y); bottom=Math.Max(bottom,y);
            }
            Check(right>=left && left>20 && right<129 && top>20 && bottom<79,"clipped pose "+paw+":"+n+" bounds="+left+","+top+","+right+","+bottom);
            return new Rectangle(left-20,top-20,right-left+1,bottom-top+1);
        }
    }
    [STAThread] static void Main()
    {
        try { Run(); } catch(Exception ex) { Console.WriteLine("FAIL: " + ex.Message + " " + (ex.InnerException == null ? "" : ex.InnerException.Message)); Environment.Exit(1); }
    }
    static void Run()
    {
        var cat=new AnimatedCatControl(); cat.Size=new Size(110,60);
        Rectangle idle=Render(cat,0,false);
        Check(idle.Height>=49 && idle.Height<=54,"idle outline height " + idle.Height);
        for(int i=0;i<12;i++) Render(cat,i,false);
        for(int i=1;i<8;i++) Render(cat,i,true);
        Console.WriteLine("110x60 idle outline: " + idle + "; 19 poses: no edge clipping");
        int saved=0;
        var form=new Form { ShowInTaskbar=false, StartPosition=FormStartPosition.Manual, Location=new Point(-3000,-3000) };
        var save=new Button { Text="Save" }; save.Click += delegate { saved++; };
        form.Controls.Add(cat); form.Controls.Add(save); form.AcceptButton=save;
        form.Show(); cat.Focus(); Application.DoEvents(); Check(cat.Focused,"cat focus");
        Message enter=Message.Create(cat.Handle,0x100,new IntPtr((int)Keys.Enter),IntPtr.Zero);
        Check(!cat.PreProcessMessage(ref enter),"Enter must reach OnKeyDown");
        Message tab=Message.Create(cat.Handle,0x100,new IntPtr((int)Keys.Tab),IntPtr.Zero);
        Check(cat.PreProcessMessage(ref tab),"Tab must retain dialog navigation"); cat.Focus(); Application.DoEvents();
        Set(cat,"motionAllowed",(Func<bool>)(()=>false)); typeof(AnimatedCatControl).GetMethod("SyncMotion",F).Invoke(cat,null);
        Set(cat,"frame",0); Set(cat,"paw",false);
        Check(PostMessage(cat.Handle,0x100,new IntPtr((int)Keys.Enter),IntPtr.Zero),"post Enter");
        Check(PostMessage(cat.Handle,0x101,new IntPtr((int)Keys.Enter),IntPtr.Zero),"post Enter up");
        for(int i=0;i<20;i++) { Application.DoEvents(); Thread.Sleep(5); }
        Check(saved==0 && (int)Get(cat,"frame")!=0,"Enter must act without save");
        Console.WriteLine("focused dialog Enter: cat action, save=0; Tab navigation retained");
        cat.Dispose(); cat.Dispose(); form.Dispose(); Console.WriteLine("repeat Dispose with live parent: passed");
    }
}


