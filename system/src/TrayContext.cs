using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DSHVoiceSystem
{
    public sealed class TrayContext : ApplicationContext
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "DSHVoiceSystem";

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        private readonly NotifyIcon _tray;
        private readonly OverlayForm _overlay;
        private readonly HotkeyWindow _hotkey;
        private readonly Timer _maxTimer;
        private readonly ToolStripMenuItem _toggleItem;
        private readonly ToolStripMenuItem _startupItem;
        private readonly ToolStripMenuItem _learnItem;
        private readonly Icon _idleIcon;
        private readonly Icon _recordingIcon;
        private Recorder _recorder;
        private bool _recording;
        private bool _processing;
        private string _lastRaw = "";
        private string _lastFinal = "";

        public TrayContext()
        {
            Program.Config = Config.Load();
            _idleIcon = CreateIcon(Color.FromArgb(94, 106, 210));
            _recordingIcon = CreateIcon(Color.FromArgb(229, 72, 77));

            _overlay = new OverlayForm();
            _overlay.Show();
            _overlay.Hide();

            _toggleItem = new ToolStripMenuItem("开始听写");
            _toggleItem.ShortcutKeyDisplayString = "Ctrl+Alt+Space";
            _toggleItem.Click += delegate { Toggle(); };

            ToolStripMenuItem settingsItem = new ToolStripMenuItem("设置…");
            settingsItem.Click += delegate { OpenSettings(); };

            _learnItem = new ToolStripMenuItem("记住上一条修正…");
            _learnItem.Enabled = false;
            _learnItem.Click += delegate { OpenLearn(); };

            _startupItem = new ToolStripMenuItem("开机启动");
            _startupItem.CheckOnClick = true;
            _startupItem.Checked = IsAutoStart();
            _startupItem.Click += delegate { SetAutoStart(_startupItem.Checked); };

            ToolStripMenuItem logItem = new ToolStripMenuItem("打开日志");
            logItem.Click += delegate
            {
                try { System.Diagnostics.Process.Start("notepad.exe", Config.LogPath); } catch { }
            };

            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += delegate { ExitThread(); };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(_toggleItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(settingsItem);
            menu.Items.Add(_learnItem);
            menu.Items.Add(_startupItem);
            menu.Items.Add(logItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            _tray = new NotifyIcon();
            _tray.Icon = _idleIcon;
            _tray.Text = "DSH 全域语音输入（Ctrl+Alt+Space）";
            _tray.ContextMenuStrip = menu;
            _tray.Visible = true;
            _tray.DoubleClick += delegate { Toggle(); };

            _maxTimer = new Timer();
            _maxTimer.Interval = 500;
            _maxTimer.Tick += delegate
            {
                if (_recording && _recorder != null && _recorder.Duration.TotalSeconds >= Program.Config.MaxSeconds)
                {
                    _maxTimer.Stop();
                    StopAndTranscribe();
                }
            };

            _hotkey = new HotkeyWindow(Toggle);
            Application.ApplicationExit += delegate { Cleanup(); };
            _tray.ShowBalloonTip(3000, "DSH 全域语音输入", "按 Ctrl+Alt+Space 开始/结束听写。识别、润色和记忆都复用 DSH。", ToolTipIcon.Info);
            Log.Write("started");
        }

        private void Toggle()
        {
            Log.Write("toggle recording=" + _recording + " processing=" + _processing);
            if (_processing)
            {
                _tray.ShowBalloonTip(1500, "正在处理", "上一次听写还在转写/润色，请稍候。", ToolTipIcon.Info);
                return;
            }
            if (_recording) StopAndTranscribe();
            else StartRecording();
        }

        private void StartRecording()
        {
            try
            {
                Log.Write("start begin");
                _recorder = new Recorder();
                _recorder.Start();
                _recording = true;
                _tray.Icon = _recordingIcon;
                _toggleItem.Text = "结束听写";
                _overlay.ShowStatus("🎙 录音中… 再按 Ctrl+Alt+Space 结束", Color.FromArgb(120, 255, 160), true);
                _maxTimer.Start();
                Log.Write("start ok");
            }
            catch (Exception ex)
            {
                _recording = false;
                if (_recorder != null) { _recorder.Dispose(); _recorder = null; }
                _tray.Icon = _idleIcon;
                _toggleItem.Text = "开始听写";
                _overlay.ShowStatus("❌ " + ex.Message, Color.FromArgb(255, 130, 130), false);
                Log.Write("start recording: " + ex);
            }
        }

        private void StopAndTranscribe()
        {
            if (!_recording || _recorder == null) return;
            Log.Write("stop begin");
            _maxTimer.Stop();
            _recording = false;
            _tray.Icon = _idleIcon;
            _toggleItem.Text = "开始听写";
            float[] samples;
            try
            {
                samples = _recorder.Stop();
                Log.Write("stop samples=" + samples.Length);
            }
            catch (Exception ex)
            {
                samples = new float[0];
                Log.Write("stop recording: " + ex.Message);
            }
            finally
            {
                try { _recorder.Dispose(); } catch { }
                _recorder = null;
            }

            if (samples.Length < 1600)
            {
                _overlay.ShowStatus("录音太短，未转写", Color.FromArgb(255, 210, 120), false);
                return;
            }
            double rms = Recorder.Rms(samples);
            if (rms < 0.004)
            {
                _overlay.ShowStatus("未检测到语音（请靠近麦克风再说一次）", Color.FromArgb(255, 210, 120), false);
                return;
            }

            _processing = true;
            _overlay.ShowStatus("⏳ 转写中…（本地 SenseVoice）", Color.FromArgb(180, 220, 255), true);
            Task.Run(new Action(delegate { Process(samples); }));
        }

        private void Process(float[] samples)
        {
            try
            {
                string text = DshClient.Transcribe(samples);
                string finalText = DshClient.Polish(text);
                if (string.IsNullOrEmpty(finalText)) finalText = text;
                Injector.TypeText(finalText);
                _lastRaw = text;
                _lastFinal = finalText;
                SetLearnEnabled(true);
                _overlay.ShowStatus("✅ 已输入", Color.FromArgb(120, 255, 160), false);
                Log.Write("inserted " + finalText.Length + " chars");
            }
            catch (Exception ex)
            {
                _overlay.ShowStatus("❌ " + ex.Message, Color.FromArgb(255, 130, 130), true);
                Balloon("听写失败", ex.Message, ToolTipIcon.Error);
                Log.Write("process: " + ex.Message);
            }
            finally
            {
                _processing = false;
            }
        }

        private void SetLearnEnabled(bool enabled)
        {
            if (_learnItem == null || _tray == null || _tray.ContextMenuStrip == null) return;
            try
            {
                _tray.ContextMenuStrip.BeginInvoke(new Action(delegate { _learnItem.Enabled = enabled; }));
            }
            catch
            {
            }
        }

        private void Balloon(string title, string text, ToolTipIcon icon)
        {
            if (_tray == null) return;
            try
            {
                _tray.ShowBalloonTip(2500, title, text, icon);
            }
            catch
            {
            }
        }

        private void OpenSettings()
        {
            using (SettingsForm form = new SettingsForm(Program.Config))
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    DshClient.ResetCache();
                    _tray.ShowBalloonTip(1600, "设置已保存", "新的设置会用于下一次听写。", ToolTipIcon.Info);
                }
            }
        }

        private void OpenLearn()
        {
            if (string.IsNullOrEmpty(_lastRaw) && string.IsNullOrEmpty(_lastFinal))
            {
                _tray.ShowBalloonTip(1600, "没有可修正的内容", "先听写一条，再回来修正。", ToolTipIcon.Info);
                return;
            }
            using (LearnForm form = new LearnForm(_lastRaw, _lastFinal))
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    bool ok = DshClient.Learn(form.RawText, form.FinalText);
                    _tray.ShowBalloonTip(2000, ok ? "已记住这次修正" : "修正未保存", ok ? "下一次转写会使用它。" : "请检查 DSH 是否在运行。", ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
                }
            }
        }

        private static bool IsAutoStart()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;
                    string value = key.GetValue(RunValue) as string;
                    return !string.IsNullOrEmpty(value) && value.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        private void SetAutoStart(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return;
                    if (enabled) key.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                    else key.DeleteValue(RunValue, false);
                }
                _startupItem.Checked = enabled;
            }
            catch (Exception ex)
            {
                _startupItem.Checked = !enabled;
                _tray.ShowBalloonTip(2000, "开机启动设置失败", ex.Message, ToolTipIcon.Warning);
            }
        }

        private static Icon CreateIcon(Color accent)
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush brush = new SolidBrush(accent)) g.FillEllipse(brush, 1, 1, 30, 30);
                    using (Pen pen = new Pen(Color.White, 2.2f))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        g.DrawEllipse(pen, 11, 6, 10, 15);
                        g.DrawArc(pen, 7, 13, 18, 12, 0, 180);
                        g.DrawLine(pen, 16, 25, 16, 29);
                        g.DrawLine(pen, 11, 29, 21, 29);
                    }
                }
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon temp = Icon.FromHandle(handle))
                        return (Icon)temp.Clone();
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }

        private void Cleanup()
        {
            try { _maxTimer.Stop(); } catch { }
            if (_recorder != null) { try { _recorder.Abort(); } catch { } _recorder = null; }
            if (_hotkey != null) _hotkey.Dispose();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            if (_overlay != null) _overlay.Dispose();
            if (_idleIcon != null) _idleIcon.Dispose();
            if (_recordingIcon != null) _recordingIcon.Dispose();
            Log.Write("stopped");
        }
    }

    public sealed class LearnForm : Form
    {
        private readonly TextBox _raw;
        private readonly TextBox _final;

        public string RawText { get { return _raw.Text; } }
        public string FinalText { get { return _final.Text; } }

        public LearnForm(string raw, string finalText)
        {
            Text = "记住这次修正";
            Width = 620;
            Height = 360;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);

            Label rawLabel = new Label();
            rawLabel.Text = "识别原文";
            rawLabel.SetBounds(16, 12, 200, 20);
            Controls.Add(rawLabel);

            _raw = new TextBox();
            _raw.Multiline = true;
            _raw.ScrollBars = ScrollBars.Vertical;
            _raw.SetBounds(16, 34, 580, 90);
            _raw.Text = raw;
            Controls.Add(_raw);

            Label finalLabel = new Label();
            finalLabel.Text = "我实际想要的内容";
            finalLabel.SetBounds(16, 134, 200, 20);
            Controls.Add(finalLabel);

            _final = new TextBox();
            _final.Multiline = true;
            _final.ScrollBars = ScrollBars.Vertical;
            _final.SetBounds(16, 156, 580, 100);
            _final.Text = finalText;
            Controls.Add(_final);

            Button ok = new Button();
            ok.Text = "记住";
            ok.SetBounds(400, 274, 90, 30);
            ok.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.SetBounds(500, 274, 90, 30);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}
