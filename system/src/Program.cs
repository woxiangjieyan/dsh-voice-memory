using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DSHVoiceSystem
{
    public static class Program
    {
        private static Mutex _mutex;

        public static Config Config;

        [STAThread]
        public static void Main(string[] args)
        {
            Log.Write("main args=" + string.Join("|", args ?? new string[0]));
            bool created;
            _mutex = new Mutex(true, "Local\\DSHVoiceSystemTray", out created);
            if (!created)
            {
                MessageBox.Show("DSH 全域语音输入已经在运行。\n按 Ctrl+Alt+Space 开始/结束听写。", "DSH 全域语音输入", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args != null && args.Length > 0)
            {
                if (string.Equals(args[0], "--http-test", StringComparison.OrdinalIgnoreCase))
                {
                    RunHttpTest();
                    return;
                }
                if (string.Equals(args[0], "--http-test-quiet", StringComparison.OrdinalIgnoreCase))
                {
                    RunHttpTestQuiet();
                    return;
                }
                if (string.Equals(args[0], "--inject", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
                {
                    RunInjectTest(args[1]);
                    return;
                }
                if (string.Equals(args[0], "--record-test", StringComparison.OrdinalIgnoreCase))
                {
                    int seconds = 5;
                    if (args.Length > 1) int.TryParse(args[1], out seconds);
                    RunRecordTest(seconds);
                    return;
                }
            }

            Config = Config.Load();
            try
            {
                Application.Run(new TrayContext());
            }
            catch (Exception ex)
            {
                Log.Write("fatal: " + ex);
                MessageBox.Show(ex.Message, "DSH 全域语音输入启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
            }
        }

        private static void RunRecordTest(int seconds)
        {
            string report = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "record-test.txt");
            Config = Config.Load();
            Recorder recorder = new Recorder();
            try
            {
                recorder.Start();
                Thread.Sleep(Math.Max(1, seconds) * 1000);
                float[] samples = recorder.Stop();
                double rms = Recorder.Rms(samples);
                string text = "";
                string error = "";
                if (samples.Length > 0)
                {
                    try
                    {
                        text = DshClient.Transcribe(samples);
                        text = DshClient.Polish(text);
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                    }
                }
                string result = "samples=" + samples.Length + Environment.NewLine +
                                "rms=" + rms.ToString("0.000000") + Environment.NewLine +
                                "text=" + text + Environment.NewLine +
                                "error=" + error + Environment.NewLine;
                File.WriteAllText(report, result, Encoding.UTF8);
                Log.Write("record test: " + result.Replace(Environment.NewLine, " | "));
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(report, "FAILED: " + ex, Encoding.UTF8); } catch { }
                Log.Write("record test failed: " + ex);
            }
            finally
            {
                recorder.Dispose();
            }
        }

        private static void RunHttpTest()
        {
            Config = Config.Load();
            try
            {
                string baseUrl = DshClient.Discover();
                string message = "DSH: " + baseUrl + "\n";
                System.Collections.Generic.Dictionary<string, object> body = new System.Collections.Generic.Dictionary<string, object>();
                body["action"] = "local-status";
                System.Collections.Generic.Dictionary<string, object> response = DshClient.Post(baseUrl, body, 10000);
                message += new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(response);
                Log.Write("HTTP TEST\n" + message);
                MessageBox.Show(message, "HTTP test", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log.Write("HTTP TEST FAILED: " + ex);
                MessageBox.Show(ex.Message, "HTTP test failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void RunHttpTestQuiet()
        {
            string report = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "http-test.txt");
            try
            {
                Config = Config.Load();
                string baseUrl = DshClient.Discover();
                System.Collections.Generic.Dictionary<string, object> body = new System.Collections.Generic.Dictionary<string, object>();
                body["action"] = "local-status";
                System.Collections.Generic.Dictionary<string, object> response = DshClient.Post(baseUrl, body, 10000);
                File.WriteAllText(report, "URL: " + baseUrl + Environment.NewLine + new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(response), Encoding.UTF8);
                Log.Write("HTTP TEST QUIET OK " + baseUrl);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(report, "FAILED: " + ex, Encoding.UTF8); } catch { }
                Log.Write("HTTP TEST QUIET FAILED: " + ex);
            }
        }

        private static void RunInjectTest(string text)
        {
            Log.Write("inject start size=" + Injector.StructureSize + " text=" + text);
            Thread.Sleep(900);
            try
            {
                Injector.TypeText(text);
                Log.Write("inject test ok: " + text);
            }
            catch (Exception ex)
            {
                Log.Write("inject test failed: " + ex.Message);
            }
        }
    }
}
