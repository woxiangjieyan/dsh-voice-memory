using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace DSHVoiceSystem
{
    /// <summary>
    /// Talks to the running DSH host's /voice-memory route. The route already
    /// owns SenseVoice, hotwords, learned corrections, polish and memory, so the
    /// system-wide helper never duplicates the model or the settings.
    /// </summary>
    public static class DshClient
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();
        private static string _cachedBaseUrl;
        private static string _polishProvider;
        private static string _polishModel;

        public static string CachedBaseUrl
        {
            get { return _cachedBaseUrl; }
        }

        public static void ResetCache()
        {
            _cachedBaseUrl = null;
            _polishProvider = null;
            _polishModel = null;
        }

        public static string Discover()
        {
            if (!string.IsNullOrEmpty(_cachedBaseUrl) && Probe(_cachedBaseUrl, 1200))
                return _cachedBaseUrl;

            List<string> candidates = new List<string>();
            AddCandidate(candidates, Program.Config.WebUrl);

            string env = Environment.GetEnvironmentVariable("DSH_WEB_URL");
            AddCandidate(candidates, env);

            foreach (string log in RecentLogs())
            {
                try
                {
                    string text = File.ReadAllText(log);
                    foreach (Match m in Regex.Matches(text, @"https?://127\.0\.0\.1:\d+"))
                        AddCandidate(candidates, m.Value);
                }
                catch
                {
                }
            }

            foreach (int port in ListeningLoopbackPorts())
                AddCandidate(candidates, "http://127.0.0.1:" + port.ToString());

            foreach (string candidate in candidates)
            {
                if (Probe(candidate, 900))
                {
                    _cachedBaseUrl = candidate;
                    Log.Write("DSH found at " + candidate);
                    return candidate;
                }
            }

            throw new InvalidOperationException("未找到正在运行的 DeepSeek Harness。请先启动 DSH，或在设置里填写它的地址。");
        }

        private static void AddCandidate(List<string> list, string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            url = url.Trim().TrimEnd('/');
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            if (!list.Contains(url)) list.Add(url);
        }

        private static IEnumerable<string> RecentLogs()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "logs");
            if (!Directory.Exists(dir)) yield break;
            string[] files = Directory.GetFiles(dir, "startup-*.log");
            Array.Sort(files, delegate(string a, string b) { return File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)); });
            for (int i = 0; i < files.Length && i < 3; i++) yield return files[i];
        }

        private static List<int> ListeningLoopbackPorts()
        {
            List<int> ports = new List<int>();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netstat", "-ano");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    foreach (string line in output.Split('\n'))
                    {
                        if (line.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        Match m = Regex.Match(line, @"(?:127\.0\.0\.1|\[::1\]):(\d+)\s");
                        if (!m.Success) continue;
                        int port;
                        if (!int.TryParse(m.Groups[1].Value, out port)) continue;
                        if (port > 0 && !ports.Contains(port)) ports.Add(port);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("netstat failed: " + ex.Message);
            }
            return ports;
        }

        public static bool Probe(string baseUrl, int timeoutMs)
        {
            try
            {
                Dictionary<string, object> body = new Dictionary<string, object>();
                body["action"] = "local-status";
                Dictionary<string, object> response = Post(baseUrl, body, timeoutMs);
                if (!GetBool(response, "ok")) return false;
                object value;
                if (response.TryGetValue("value", out value) && value is Dictionary<string, object>)
                    return true;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string Transcribe(float[] samples)
        {
            string baseUrl = Discover();
            byte[] bytes = new byte[samples.Length * 4];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["action"] = "local-transcribe";
            body["audio"] = Convert.ToBase64String(bytes);
            body["sampleRate"] = 16000;
            body["sessionId"] = "system";
            Dictionary<string, object> response = Post(baseUrl, body, 120000);
            if (!GetBool(response, "ok")) throw new InvalidOperationException(ErrorOf(response));
            string text = GetString(response, "text").Trim();
            if (text.Length == 0) throw new InvalidOperationException("未识别到文字，请靠近麦克风再说一次。");
            return text;
        }

        public static string Polish(string text)
        {
            if (!Program.Config.Polish || string.IsNullOrEmpty(text)) return text;
            try
            {
                string baseUrl = Discover();
                if (string.IsNullOrEmpty(_polishProvider) || string.IsNullOrEmpty(_polishModel))
                {
                    Dictionary<string, object> listBody = new Dictionary<string, object>();
                    listBody["action"] = "list-models";
                    Dictionary<string, object> list = Post(baseUrl, listBody, 20000);
                    if (!GetBool(list, "ok")) return text;
                    object value;
                    if (!list.TryGetValue("value", out value) || !(value is object[])) return text;
                    object[] routes = (object[])value;
                    if (routes.Length == 0) return text;
                    Dictionary<string, object> first = routes[0] as Dictionary<string, object>;
                    if (first == null) return text;
                    _polishProvider = GetString(first, "provider");
                    _polishModel = GetString(first, "model");
                }
                if (string.IsNullOrEmpty(_polishProvider) || string.IsNullOrEmpty(_polishModel)) return text;

                Dictionary<string, object> body = new Dictionary<string, object>();
                body["action"] = "polish";
                body["text"] = text;
                body["provider"] = _polishProvider;
                body["model"] = _polishModel;
                body["sessionId"] = "system";
                Dictionary<string, object> response = Post(baseUrl, body, 60000);
                if (!GetBool(response, "ok")) return text;
                string polished = GetString(response, "text").Trim();
                return polished.Length == 0 ? text : polished;
            }
            catch (Exception ex)
            {
                Log.Write("polish failed: " + ex.Message);
                return text;
            }
        }

        public static bool Learn(string raw, string finalText)
        {
            try
            {
                string baseUrl = Discover();
                Dictionary<string, object> body = new Dictionary<string, object>();
                body["action"] = "memory-learn";
                body["raw"] = raw == null ? "" : raw;
                body["final"] = finalText == null ? "" : finalText;
                body["sessionId"] = "system";
                Dictionary<string, object> response = Post(baseUrl, body, 20000);
                return GetBool(response, "ok");
            }
            catch (Exception ex)
            {
                Log.Write("learn failed: " + ex.Message);
                return false;
            }
        }

        public static Dictionary<string, object> Post(string baseUrl, Dictionary<string, object> body, int timeoutMs)
        {
            string json = Serializer.Serialize(body);
            using (HttpClientHandler handler = new HttpClientHandler())
            {
                handler.UseProxy = false;
                handler.AllowAutoRedirect = false;
                using (HttpClient client = new HttpClient(handler))
                {
                    client.Timeout = TimeSpan.FromMilliseconds(timeoutMs);
                    using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/voice-memory"))
                    {
                        request.Headers.TryAddWithoutValidation("Origin", baseUrl.TrimEnd('/'));
                        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                        using (HttpResponseMessage response = client.SendAsync(request).Result)
                        {
                            string text = response.Content.ReadAsStringAsync().Result;
                            object parsed = Serializer.DeserializeObject(text);
                            Dictionary<string, object> dict = parsed as Dictionary<string, object>;
                            if (dict == null) throw new InvalidOperationException("DSH 返回了无法解析的内容。");
                            return dict;
                        }
                    }
                }
            }
        }

        public static string GetString(Dictionary<string, object> dict, string key)
        {
            object value;
            if (dict != null && dict.TryGetValue(key, out value) && value != null) return value.ToString();
            return "";
        }

        public static bool GetBool(Dictionary<string, object> dict, string key)
        {
            object value;
            if (dict != null && dict.TryGetValue(key, out value) && value != null)
            {
                if (value is bool) return (bool)value;
                bool parsed;
                if (bool.TryParse(value.ToString(), out parsed)) return parsed;
            }
            return false;
        }

        private static string ErrorOf(Dictionary<string, object> response)
        {
            object error;
            if (response != null && response.TryGetValue("error", out error))
            {
                Dictionary<string, object> dict = error as Dictionary<string, object>;
                if (dict != null)
                {
                    string message = GetString(dict, "message");
                    if (!string.IsNullOrEmpty(message)) return message;
                }
                if (error != null) return error.ToString();
            }
            return "语音识别失败。";
        }
    }
}
