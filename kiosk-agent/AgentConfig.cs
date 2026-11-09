using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassManager.KioskAgent
{
    /// <summary>摄像头设置</summary>
    public class CameraSettings
    {
        /// <summary>设备名（DirectShow 友好名），空表示使用第一个可用设备</summary>
        public string DeviceName { get; set; } = "";
        public int Width { get; set; } = 1280;
        public int Height { get; set; } = 720;
        public int Fps { get; set; } = 30;
        /// <summary>JPEG 质量 30-95</summary>
        public int Quality { get; set; } = 70;
        /// <summary>是否允许教师端拉流</summary>
        public bool Enabled { get; set; } = true;
    }

    /// <summary>麦克风设置</summary>
    public class MicSettings
    {
        public bool Enabled { get; set; } = true;
        public string DeviceName { get; set; } = "";
        /// <summary>音量增益 0.1 - 3.0</summary>
        public double Gain { get; set; } = 1.0;
    }

    /// <summary>远程指令授权</summary>
    public class RemoteSettings
    {
        /// <summary>是否授权教师端远程执行指令（默认关闭）</summary>
        public bool Enabled { get; set; } = false;
        /// <summary>单条指令超时秒数</summary>
        public int TimeoutSeconds { get; set; } = 15;
        /// <summary>允许的前缀白名单，空表示不限制</summary>
        public List<string> AllowedCommands { get; set; } = new List<string>();
    }

    /// <summary>一体机端整体配置，保存在 %LOCALAPPDATA%\ClassManager-KioskAgent\config.json</summary>
    public class AgentConfig
    {
        /// <summary>HTTP（TCP）端口，默认 3457</summary>
        public int Port { get; set; } = 3457;
        /// <summary>视频 UDP 端口，0 表示与 Port 相同</summary>
        public int VideoPort { get; set; } = 0;
        public string BindAddress { get; set; } = "0.0.0.0";
        /// <summary>教室 / 班级名称，教师端用来区分设备</summary>
        public string RoomName { get; set; } = "";
        /// <summary>设备名，默认取计算机名</summary>
        public string DeviceName { get; set; } = Environment.MachineName;

        /// <summary>PBKDF2 口令哈希（base64）</summary>
        public string PasswordHash { get; set; } = "";
        public string PasswordSalt { get; set; } = "";
        /// <summary>教师端对接用的访问密钥</summary>
        public string AccessKey { get; set; } = "";

        public CameraSettings Camera { get; set; } = new CameraSettings();
        public MicSettings Mic { get; set; } = new MicSettings();
        public RemoteSettings Remote { get; set; } = new RemoteSettings();

        [JsonIgnore]
        public bool HasPassword => !string.IsNullOrEmpty(PasswordHash) && !string.IsNullOrEmpty(PasswordSalt);

        // ---------- 持久化 ----------

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static string ConfigDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassManager-KioskAgent");

        public static string ConfigPath => Path.Combine(ConfigDir, "config.json");

        public static AgentConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var cfg = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(ConfigPath, Encoding.UTF8), JsonOpts);
                    if (cfg != null)
                    {
                        cfg.Normalize();
                        return cfg;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("读取配置失败，使用默认值: " + ex.Message);
            }

            var fresh = new AgentConfig();
            fresh.Normalize();
            fresh.Save();
            return fresh;
        }

        public void Normalize()
        {
            Camera ??= new CameraSettings();
            Mic ??= new MicSettings();
            Remote ??= new RemoteSettings();
            Remote.AllowedCommands ??= new List<string>();
            if (string.IsNullOrWhiteSpace(DeviceName)) DeviceName = Environment.MachineName;
            if (Port < 1 || Port > 65535) Port = 3457;
            if (VideoPort < 0 || VideoPort > 65535) VideoPort = 0;
            if (string.IsNullOrWhiteSpace(BindAddress)) BindAddress = "0.0.0.0";
            if (Camera.Quality < 30 || Camera.Quality > 95) Camera.Quality = 70;
            if (Camera.Fps < 1 || Camera.Fps > 30) Camera.Fps = 30;
            if (Mic.Gain < 0.1 || Mic.Gain > 3.0) Mic.Gain = 1.0;
            if (Remote.TimeoutSeconds < 1 || Remote.TimeoutSeconds > 120) Remote.TimeoutSeconds = 15;
            // 没有访问密钥时自动生成一个，避免出现"无密钥即可对接"的空档
            if (string.IsNullOrEmpty(AccessKey)) AccessKey = NewAccessKey();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var tmp = ConfigPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts), new UTF8Encoding(false));
                File.Move(tmp, ConfigPath, true);
            }
            catch (Exception ex)
            {
                Log.Write("保存配置失败: " + ex.Message);
            }
        }

        // ---------- 口令 ----------

        public static string NewAccessKey()
        {
            var bytes = RandomNumberGenerator.GetBytes(16);
            var hex = Convert.ToHexString(bytes).ToLowerInvariant();
            return $"{hex.Substring(0, 8)}-{hex.Substring(8, 8)}-{hex.Substring(16, 8)}-{hex.Substring(24, 8)}";
        }

        public static (string hash, string salt) HashPassword(string password)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password ?? ""), salt, 100_000, HashAlgorithmName.SHA256, 32);
            return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
        }

        public bool VerifyPassword(string password)
        {
            if (!HasPassword) return false;
            try
            {
                var salt = Convert.FromBase64String(PasswordSalt);
                var expect = Convert.FromBase64String(PasswordHash);
                var actual = Rfc2898DeriveBytes.Pbkdf2(
                    Encoding.UTF8.GetBytes(password ?? ""), salt, 100_000, HashAlgorithmName.SHA256, expect.Length);
                return CryptographicOperations.FixedTimeEquals(expect, actual);
            }
            catch
            {
                return false;
            }
        }

        public void SetPassword(string password)
        {
            var (hash, salt) = HashPassword(password);
            PasswordHash = hash;
            PasswordSalt = salt;
            Save();
        }

        public bool VerifyAccessKey(string key)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(AccessKey)) return false;
            var a = Encoding.UTF8.GetBytes(key.Trim());
            var b = Encoding.UTF8.GetBytes(AccessKey);
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        }
    }

    /// <summary>极简日志，写入 %LOCALAPPDATA%\ClassManager-KioskAgent\agent.log</summary>
    public static class Log
    {
        private static readonly object Gate = new object();

        public static string LogPath => Path.Combine(AgentConfig.ConfigDir, "agent.log");

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(AgentConfig.ConfigDir);
                    var info = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
                    if (!File.Exists(LogPath))
                        File.WriteAllText(LogPath, "", new UTF8Encoding(true));
                    File.AppendAllText(LogPath, info, new UTF8Encoding(false));
                }
            }
            catch
            {
                // 日志失败不影响主流程
            }
        }
    }
}
