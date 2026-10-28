using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ScreenOffTray.Ui
{
    internal sealed class AnimatedCatControl : Control
    {
        private static readonly int[][] Frames = {
            new[] {0,1,2,1,3}, new[] {0,1,2,3,2,3,1,0},
            new[] {0,1,2,1,3,0}, new[] {0,1,2,3,4,5,7} };
        private static readonly int[][] Durations = {
            new[] {180,280,560,280,200}, new[] {160,220,300,220,300,220,260,320},
            new[] {180,280,350,280,180,230}, new[] {160,190,150,120,190,300,220} };
        private static readonly string[] Labels = {"伸懒腰", "舔爪", "歪头", "伸爪轻拍"};
        private static readonly string[] EnglishLabels = {"stretching", "grooming a paw", "tilting its head", "reaching out"};
        private readonly Timer timer = new Timer { Interval = 40 };
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Random random = new Random();
        private Func<bool> motionAllowed = MotionAllowed;
        private readonly int[] bag = new int[4];
        private Bitmap actions, variety;
        private Form form;
        private int bagIndex = 4, lastAction = -1, current = -1, frame;
        private long started, nextIdle, nextBlink, blinkEnd, lastTick;
        private bool firstIdle = true, queued, paw, watching, keyPressed, resourcesDisposed;
        private float lean, targetLean;
        private bool english;
        internal bool English { get { return english; } set { english = value; Describe(current); } }

        private void Describe(int action)
        {
            AccessibleName = english
                ? (action < 0 ? "Cat sitting awake" : "Cat " + EnglishLabels[action]) + "; press Enter or Space to pet"
                : (action < 0 ? "猫咪：醒着坐好" : "猫咪正在" + Labels[action]) + "，按 Enter 或空格摸摸";
        }

        internal AnimatedCatControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
            BackColor = Color.Transparent; Size = new Size(110, 60); TabStop = true;
            AccessibleRole = AccessibleRole.PushButton; Describe(-1);
            actions = LoadImage("cat-actions.png");
            try { variety = LoadImage("cat-variety.png"); }
            catch { actions.Dispose(); timer.Dispose(); throw; }
            timer.Tick += Tick;
        }

        private static Bitmap LoadImage(string name)
        {
            using (Stream stream = typeof(AnimatedCatControl).Assembly.GetManifestResourceStream("ScreenOffTray.Ui.Resources." + name)) {
                if (stream == null) throw new InvalidOperationException("Missing cat resource: " + name);
                using (var image = Image.FromStream(stream)) return new Bitmap(image);
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(int action, int param, out int value, int flags);
        private static bool MotionAllowed() { int enabled; return !SystemInformation.HighContrast && SystemParametersInfo(0x1042, 0, out enabled, 0) && enabled != 0; }
        private bool CanShow() { return IsHandleCreated && !IsDisposed && Visible && form != null && form.IsHandleCreated && form.Visible && form.WindowState != FormWindowState.Minimized; }
        private int Between(int min, int max) { return random.Next(min, max + 1); }
        private void ScheduleIdle(long now) { nextIdle = now + (firstIdle ? Between(6000,9000) : Between(12000,22000)); }
        private void Neutral() { paw = false; frame = 0; Describe(-1); }
        private void SyncMotion()
        {
            if (resourcesDisposed) return;
            timer.Stop(); lastTick = 0; current = -1; queued = false; blinkEnd = 0; lean = targetLean = 0; Neutral();
            if (CanShow() && motionAllowed()) { long now = clock.ElapsedMilliseconds; ScheduleIdle(now); nextBlink = now + Between(4000,8000); timer.Start(); }
            if (!IsDisposed) Invalidate();
        }
        private void BindForm()
        {
            Form next = FindForm();
            if (form == next) return;
            if (form != null) { form.Resize -= FormChanged; form.VisibleChanged -= FormChanged; form.HandleDestroyed -= FormHandleDestroyed; form.HandleCreated -= FormChanged; }
            form = next;
            if (form != null) { form.Resize += FormChanged; form.VisibleChanged += FormChanged; form.HandleDestroyed += FormHandleDestroyed; form.HandleCreated += FormChanged; }
        }
        private void FormChanged(object sender, EventArgs e) { SyncMotion(); }
        private void FormHandleDestroyed(object sender, EventArgs e) { if (!resourcesDisposed) timer.Stop(); }
        private void PreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (!IsHandleCreated || IsDisposed) return;
            try { BeginInvoke((MethodInvoker)delegate { if (!IsDisposed && IsHandleCreated) SyncMotion(); }); }
            catch (InvalidOperationException) { }
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e); BindForm();
            if (!watching) { SystemEvents.UserPreferenceChanged += PreferenceChanged; watching = true; }
            SyncMotion();
        }
        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (!resourcesDisposed) timer.Stop();
            if (watching) { SystemEvents.UserPreferenceChanged -= PreferenceChanged; watching = false; }
            base.OnHandleDestroyed(e);
        }
        protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); if (!resourcesDisposed) { BindForm(); if (actions != null) SyncMotion(); } }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (actions != null) SyncMotion(); }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !resourcesDisposed) {
                resourcesDisposed = true;
                timer.Stop(); timer.Tick -= Tick; timer.Dispose();
                if (watching) { SystemEvents.UserPreferenceChanged -= PreferenceChanged; watching = false; }
                if (form != null) { form.Resize -= FormChanged; form.VisibleChanged -= FormChanged; form.HandleDestroyed -= FormHandleDestroyed; form.HandleCreated -= FormChanged; form = null; }
                if (actions != null) { actions.Dispose(); actions = null; }
                if (variety != null) { variety.Dispose(); variety = null; }
            }
            base.Dispose(disposing);
        }
        private int ChooseAction()
        {
            if (bagIndex == 4) {
                for (int i = 0; i < 4; i++) bag[i] = i;
                for (int i = 3; i > 0; i--) { int j = random.Next(i + 1), t = bag[i]; bag[i] = bag[j]; bag[j] = t; }
                if (bag[0] == lastAction) { int t = bag[0]; bag[0] = bag[1]; bag[1] = t; }
                bagIndex = 0;
            }
            lastAction = bag[bagIndex++]; return lastAction;
        }
        private void ShowFrame(int action, int n) { paw = action == 3 && n != 0; frame = n == 0 ? 0 : paw ? n : action * 4 + n; }
        private void StartAction(long now)
        {
            current = ChooseAction(); started = now; blinkEnd = 0; ShowFrame(current, 0);
            Describe(current);
        }
        private void Advance(long now)
        {
            long dt = lastTick == 0 ? 0 : Math.Min(100, Math.Max(0, now - lastTick)); lastTick = now;
            lean += (targetLean - lean) * (float)(1 - Math.Exp(-dt / 120.0));
            if (current >= 0) {
                long elapsed = now - started; int i = 0;
                while (i < Durations[current].Length && elapsed >= Durations[current][i]) elapsed -= Durations[current][i++];
                if (i == Durations[current].Length) {
                    current = -1; Neutral(); nextBlink = now + Between(3500,9500); firstIdle = false; ScheduleIdle(now);
                    if (queued) { queued = false; StartAction(now); }
                } else ShowFrame(current, Frames[current][i]);
            } else if (blinkEnd != 0) {
                if (now >= blinkEnd) { blinkEnd = 0; frame = 0; nextBlink = now + Between(3500,9500); }
            } else if (now >= nextIdle) StartAction(now);
            else if (now >= nextBlink) { paw = false; frame = 11; blinkEnd = now + Between(120,130); }
        }
        private void Tick(object sender, EventArgs e)
        {
            if (!CanShow() || !motionAllowed()) { SyncMotion(); return; }
            Advance(clock.ElapsedMilliseconds); Invalidate();
        }
        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e); if (!CanShow()) return;
            if (!motionAllowed()) { int action = ChooseAction(); ShowFrame(action, action == 3 ? 4 : 2); Describe(action); Invalidate(); return; }
            if (current >= 0) queued = true; else StartAction(clock.ElapsedMilliseconds);
            Invalidate();
        }
        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return ((keyData & Keys.Modifiers) == Keys.None && (key == Keys.Enter || key == Keys.Space)) || base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { if (!keyPressed) { keyPressed = true; OnClick(EventArgs.Empty); } e.Handled = true; e.SuppressKeyPress = true; }
            base.OnKeyDown(e);
        }
        protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) keyPressed = false; base.OnKeyUp(e); }
        protected override void OnLostFocus(EventArgs e) { keyPressed = false; base.OnLostFocus(e); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); targetLean = Width > 0 ? Math.Max(-5, Math.Min(5, (e.X - Width / 2F) / Width * 10)) : 0; }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); targetLean = 0; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); if (actions == null || variety == null || Width <= 0 || Height <= 0) return;
            float scale = Math.Min(Width / 110F, Height / 60F), cx = Width / 2F + lean * scale;
            float bottom = Height - (Height - 60 * scale) / 2F - 4.5F * scale;
            float breathe = timer.Enabled ? (float)Math.Sin(clock.ElapsedMilliseconds / 1250.0) * .35F * scale : 0;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            if (!paw) {
                int row = frame / 4, top = new[] {348,330,309}[row] - 300;
                float size = 66F * scale, s = size / 362F;
                e.Graphics.DrawImage(variety, new RectangleF(cx-size/2,bottom-300*s-breathe,size,308*s), new RectangleF((frame%4)*362,row*362+top,362,308),GraphicsUnit.Pixel);
            } else {
                float size = 55F * scale, w = actions.Width / 4F, h = actions.Height / 2F;
                e.Graphics.DrawImage(actions, new RectangleF(cx-size/2,bottom-(426F/443F)*size-breathe,size,size), new RectangleF((frame%4)*w,(frame/4)*h,w,h),GraphicsUnit.Pixel);
            }
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle,-1,-1));
        }
    }
}









