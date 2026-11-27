using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace DSHVoiceSystem
{
    public class Config
    {
        public string WebUrl { get; set; }
        public bool Polish { get; set; }
        public int MaxSeconds { get; set; }

        public Config()
        {
            WebUrl = "";
            Polish = true;
            MaxSeconds = 120;
        }

        public static string DataDir
        {
            get
            {
                string[] candidates = new string[]
                {
                    Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "DSHVoiceSystem"),
                    Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "DSHVoiceSystem"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DSHVoiceSystem"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSHVoiceSystem"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data")
                };
                Exception last = null;
                foreach (string candidate in candidates)
                {
                    if (string.IsNullOrEmpty(candidate) || !Path.IsPathRooted(candidate)) continue;
                    try
                    {
                        Directory.CreateDirectory(candidate);
                        return candidate;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                    }
                }
                throw new IOException("无法创建配置目录", last);
            }
        }

        public static string ConfigPath
        {
            get { return Path.Combine(DataDir, "config.json"); }
        }

        public static string LogPath
        {
            get { return Path.Combine(DataDir, "system.log"); }
        }

        public static Config Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                    Config cfg = new JavaScriptSerializer().Deserialize<Config>(json);
                    if (cfg != null)
                    {
                        if (cfg.WebUrl == null) cfg.WebUrl = "";
                        if (cfg.MaxSeconds < 5) cfg.MaxSeconds = 5;
                        if (cfg.MaxSeconds > 600) cfg.MaxSeconds = 600;
                        return cfg;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("config load failed: " + ex.Message);
            }
            return new Config();
        }

        public void Save()
        {
            try
            {
                string json = new JavaScriptSerializer().Serialize(this);
                File.WriteAllText(ConfigPath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log.Write("config save failed: " + ex.Message);
            }
        }
    }

    public static class Log
    {
        private static readonly object Gate = new object();

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine;
                    File.AppendAllText(Config.LogPath, line, Encoding.UTF8);
                }
            }
            catch
            {
                try
                {
                    string fallback = Path.Combine(Path.GetTempPath(), "DSHVoiceSystem.log");
                    File.AppendAllText(fallback, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // Logging must never take the app down.
                }
            }
        }
    }
}
