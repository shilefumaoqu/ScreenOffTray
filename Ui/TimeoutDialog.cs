using System;
using System.Drawing;
using System.Windows.Forms;

namespace ScreenOffTray.Ui
{
    internal sealed class TimeoutDialog : Form
    {
        private readonly NumericUpDown input;
        internal int Minutes { get { return (int)input.Value; } }
        internal TimeoutDialog(UiText text, int minutes)
        {
            Text = text.Pick("自定义关闭时间", "Custom timeout");
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F); FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen; MinimizeBox = MaximizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(380, 146); Padding = new Padding(18);
            var label = new Label { Text = text.Pick("关闭时间（分钟，1–1440）", "Timeout (minutes, 1–1440)"), AutoSize = true, Location = new Point(18, 20) };
            input = new NumericUpDown { Minimum = 1, Maximum = 1440, Value = minutes, Location = new Point(18, 48), Width = 340, TabIndex = 0 };
            var ok = new Button { Text = text.Pick("确定", "OK"), DialogResult = DialogResult.OK, Location = new Point(192, 100), Size = new Size(78, 28), TabIndex = 1 };
            var cancel = new Button { Text = text.Pick("取消", "Cancel"), DialogResult = DialogResult.Cancel, Location = new Point(278, 100), Size = new Size(80, 28), TabIndex = 2 };
            Controls.AddRange(new Control[] { label, input, ok, cancel }); AcceptButton = ok; CancelButton = cancel;
            Shown += delegate { input.Focus(); input.Select(0, input.Text.Length); };
        }
    }
}
