using System;
using System.Drawing;
using System.Windows.Forms;

namespace DSHVoiceSystem
{
    public sealed class SettingsForm : Form
    {
        private readonly TextBox _url;
        private readonly CheckBox _polish;
        private readonly NumericUpDown _maxSeconds;

        public SettingsForm(Config config)
        {
            Text = "DSH 全域语音输入 — 设置";
            Width = 520;
            Height = 270;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);

            Label urlLabel = new Label();
            urlLabel.Text = "DSH 地址（留空自动查找）";
            urlLabel.SetBounds(18, 18, 220, 22);
            Controls.Add(urlLabel);

            _url = new TextBox();
            _url.SetBounds(18, 42, 470, 26);
            _url.Text = config.WebUrl;
            Controls.Add(_url);

            Label hint = new Label();
            hint.Text = "例如 http://127.0.0.1:9937；留空时自动从环境变量、日志和本机监听端口查找。";
            hint.ForeColor = Color.Gray;
            hint.SetBounds(18, 72, 470, 36);
            Controls.Add(hint);

            _polish = new CheckBox();
            _polish.Text = "使用 DSH 模型润色（推荐）";
            _polish.SetBounds(18, 114, 300, 24);
            _polish.Checked = config.Polish;
            Controls.Add(_polish);

            Label maxLabel = new Label();
            maxLabel.Text = "最长录音（秒）";
            maxLabel.SetBounds(18, 150, 140, 22);
            Controls.Add(maxLabel);

            _maxSeconds = new NumericUpDown();
            _maxSeconds.SetBounds(160, 148, 80, 26);
            _maxSeconds.Minimum = 5;
            _maxSeconds.Maximum = 600;
            _maxSeconds.Value = config.MaxSeconds;
            Controls.Add(_maxSeconds);

            Button test = new Button();
            test.Text = "测试连接";
            test.SetBounds(18, 194, 100, 30);
            test.Click += delegate
            {
                Cursor = Cursors.WaitCursor;
                try
                {
                    string url = _url.Text.Trim();
                    if (url.Length == 0)
                    {
                        DshClient.Discover();
                        MessageBox.Show(this, "已找到 DSH：" + DshClient.CachedBaseUrl, "连接成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        bool ok = DshClient.Probe(url, 2500);
                        MessageBox.Show(this, ok ? "连接成功：" + url : "连接失败，请检查地址和 DSH 是否在运行。", "测试连接", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "连接失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally
                {
                    Cursor = Cursors.Default;
                }
            };
            Controls.Add(test);

            Button save = new Button();
            save.Text = "保存";
            save.SetBounds(300, 194, 90, 30);
            save.Click += delegate
            {
                config.WebUrl = _url.Text.Trim();
                config.Polish = _polish.Checked;
                config.MaxSeconds = (int)_maxSeconds.Value;
                config.Save();
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(save);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.SetBounds(398, 194, 90, 30);
            cancel.Click += delegate
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };
            Controls.Add(cancel);

            AcceptButton = save;
            CancelButton = cancel;
        }
    }
}
