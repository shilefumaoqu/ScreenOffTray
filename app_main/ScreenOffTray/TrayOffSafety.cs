using System;
using System.Drawing;
using System.Windows.Forms;
using OledPrototype;
using ScreenOffTray.Ui;

namespace ScreenOffTray
{
    internal sealed partial class TrayApplicationContext
    {
        private readonly UnexpectedDisplayRetry _displayRetry = new UnexpectedDisplayRetry();
        private DisplayOutputNotice _outputNotice;
        private bool _retryOffSession, _explicitPreparation;
        private long _nextPreparationNotice;

        private void CheckOffOutput()
        {
            OffOutputReading? reading = _session.PollOffOutput();
            if (reading == OffOutputReading.Unknown)
                OledPrototype.Program.Log("off_output_check_unconfirmed");
            if (reading != OffOutputReading.PhysicalActive) return;

            OledPrototype.Program.Log("off_output_check_original_physical_active");
            uint activity = _session.PhysicalInputVersion;
            try {
                if (_outputNotice == null) {
                    _outputNotice = new DisplayOutputNotice(_text);
                    _outputNotice.FormClosed += delegate { _outputNotice = null; StopDisplayRetry(); };
                }
                _outputNotice.DetectedAt(DateTime.Now);
                ScheduleDisplayRetry(activity);
                if (!_outputNotice.Visible) _outputNotice.Show();
            } catch (Exception ex) {
                _displayRetry.Cancel();
                OledPrototype.Program.Log("off_output_notice_failed: " + ex);
            }
            _session.RequestEnd(false);
        }

        private void ScheduleDisplayRetry(uint activity)
        {
            if (_paused || SettingsOpen) {
                _displayRetry.Cancel();
                UpdateOutputNotice("正在恢复显示控制。当前自动关屏已暂停。",
                    "Restoring display control. Automatic screen-off is paused.");
                return;
            }
            UpdateOutputNotice("正在恢复显示控制。随后 30 秒无人操作会尝试重新关屏。",
                "Restoring display control. Screen-off will be attempted after 30 seconds without input.");
            _displayRetry.Arm(_autoClock.ElapsedMilliseconds, activity);
        }

        private void PollDisplayRetry()
        {
            if (!_displayRetry.Pending) return;
            switch (_displayRetry.Poll(_autoClock.ElapsedMilliseconds, _session.PhysicalInputVersion,
                _session.CanStartSession, _setup.IsReady, _setup.Checking)) {
                case DisplayRetryAction.CountingDown:
                    UpdateOutputNotice("30 秒无人操作会尝试重新关屏。动一下键鼠即可保持亮屏。",
                        "Screen-off will be attempted after 30 seconds without input. Use the keyboard or mouse to keep it on.");
                    break;
                case DisplayRetryAction.Cancelled:
                    UpdateOutputNotice("已检测到你的操作，本次自动重关已取消。",
                        "Input detected. Automatic screen-off for this incident was cancelled.");
                    break;
                case DisplayRetryAction.Unavailable:
                    UpdateOutputNotice("显示条件尚未核验通过。本次未重新关屏，可打开设置检查。",
                        "Display conditions could not be verified. No new screen-off was attempted. Open Settings to check.");
                    break;
                case DisplayRetryAction.Retry:
                    _retryOffSession = true;
                    if (TryStartDisplayOffSession(true)) {
                        _triggeredForIdlePeriod = true;
                        UpdateOutputNotice("30 秒未检测到操作，正在尝试重新关屏。",
                            "No input for 30 seconds. Trying to turn the screen off again.");
                    } else {
                        _retryOffSession = false;
                        UpdateOutputNotice("当前条件暂不允许关屏，请从托盘或设置查看状态。",
                            "Current conditions prevent screen-off. See the tray or Settings for status.");
                    }
                    break;
            }
        }

        private void ObserveRetryResult()
        {
            if (!_retryOffSession) return;
            if (_session.VerifiedOff) {
                _retryOffSession = false;
                UpdateOutputNotice("已重新关屏。此提示会保留，方便你查看。",
                    "The screen was turned off again. This notice stays open for inspection.");
            } else if (!_session.IsBusy) {
                _retryOffSession = false;
                UpdateOutputNotice("本次重关已取消或未完成。可在设置中查看显示状态。",
                    "Screen-off was cancelled or did not complete. Check display status in Settings.");
            }
        }

        private void CancelDisplayRetry(string zh, string en)
        {
            if (StopDisplayRetry()) UpdateOutputNotice(zh, en);
        }

        private bool StopDisplayRetry()
        {
            bool pending = _displayRetry.Pending;
            _displayRetry.Cancel();
            if (_retryOffSession && _session.TryCancelAutomaticOff()) {
                _retryOffSession = false;
                return true;
            }
            return pending;
        }

        private void UpdateOutputNotice(string zh, string en)
        {
            if (_outputNotice != null) _outputNotice.Status = _text.Pick(zh, en);
        }

        private void ObserveExplicitPreparation()
        {
            if (_setup.IsReady) _nextPreparationNotice = 0;
            if (!_explicitPreparation || _setup.Preparing) return;
            _explicitPreparation = false;
            if (!_setup.HasVerifiedProfile) NotifyPreparationRequired(true);
        }

        private void NotifyPreparationRequired(bool explicitAttempt)
        {
            long now = _autoClock.ElapsedMilliseconds;
            if (!explicitAttempt && now < _nextPreparationNotice) return;
            _nextPreparationNotice = now + OffOutputCheck.Interval;
            _trayIcon.ShowBalloonTip(5000, _text.Title,
                _text.Pick("显示准备未完成，未关屏。请打开设置查看原因。",
                    "Display preparation is incomplete. Open Settings to see why the screen stayed on."),
                ToolTipIcon.Warning);
        }
    }

    internal sealed class DisplayOutputNotice : Form
    {
        private readonly Label detected = new Label { AutoSize = true };
        private readonly Label status = new Label { AutoSize = true, MaximumSize = new Size(340, 0) };

        internal DisplayOutputNotice(UiText text)
        {
            Text = text.Pick("检测到意外亮屏", "Unexpected display output");
            AutoScaleMode = AutoScaleMode.Font;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ShowIcon = false;
            var content = new FlowLayoutPanel {
                AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, Padding = new Padding(16), MinimumSize = new Size(372, 0)
            };
            var message = new Label { AutoSize = true,
                Text = text.Pick("关屏期间，原实体屏恢复了输出。", "The original display resumed output while it was off.") };
            var dismiss = new Button { AutoSize = true, Text = text.Pick("关闭提示", "Dismiss"), Margin = new Padding(3, 12, 3, 3) };
            dismiss.Click += delegate { Close(); };
            CancelButton = dismiss;
            content.Controls.AddRange(new Control[] { message, detected, status, dismiss });
            Controls.Add(content);
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        internal void DetectedAt(DateTime time) { detected.Text = time.ToString("yyyy-MM-dd HH:mm:ss"); }
        internal string Status { set { status.Text = value; } }
    }
}
