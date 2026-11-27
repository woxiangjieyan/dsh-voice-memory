using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DSHVoiceSystem
{
    /// <summary>
    /// A tiny no-activate status pill at the bottom of the screen. It must never
    /// steal focus from the application receiving the dictated text.
    /// </summary>
    public sealed class OverlayForm : Form
    {
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int SW_SHOWNOACTIVATE = 4;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private readonly Label _label;
        private readonly Timer _timer;

        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(24, 24, 28);
            Opacity = 0.94;
            Width = 380;
            Height = 56;
            Padding = new Padding(12, 8, 12, 8);

            _label = new Label();
            _label.Dock = DockStyle.Fill;
            _label.TextAlign = ContentAlignment.MiddleCenter;
            _label.ForeColor = Color.White;
            _label.Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Regular);
            _label.AutoSize = false;
            Controls.Add(_label);

            _timer = new Timer();
            _timer.Interval = 1800;
            _timer.Tick += delegate
            {
                _timer.Stop();
                Hide();
            };
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        public void ShowStatus(string text, Color color, bool persistent)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action<string, Color, bool>(ShowStatus), text, color, persistent);
                }
                catch
                {
                }
                return;
            }
            _label.Text = text;
            _label.ForeColor = color;
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Bottom - Height - 88);
            if (!Visible) Show();
            ShowWindow(Handle, SW_SHOWNOACTIVATE);
            BringToFront();
            _timer.Stop();
            if (!persistent) _timer.Start();
        }

        public void HideStatus()
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(HideStatus)); } catch { }
                return;
            }
            _timer.Stop();
            Hide();
        }
    }
}
