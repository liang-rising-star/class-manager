using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ClassManager.KioskAgent
{
    public class ExecResult
    {
        public bool Ok { get; set; }
        public int ExitCode { get; set; }
        public bool TimedOut { get; set; }
        public string Output { get; set; } = "";
        public string Error { get; set; } = "";
    }

    /// <summary>
    /// 远程指令执行：仅在「授权远程执行」开关打开且访问密钥校验通过时可用。
    /// </summary>
    public static class RemoteExecService
    {
        private const int MaxOutputChars = 100_000;

        public static bool IsAllowed(string command, List<string> allowlist)
        {
            if (allowlist == null || allowlist.Count == 0) return true;
            foreach (var prefix in allowlist)
            {
                if (string.IsNullOrWhiteSpace(prefix)) continue;
                if (command.TrimStart().StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetOEMCP();

        public static ExecResult Run(string command, int timeoutSeconds, List<string> allowlist)
        {
            var result = new ExecResult();
            if (string.IsNullOrWhiteSpace(command))
            {
                result.Error = "指令为空";
                return result;
            }

            if (!IsAllowed(command, allowlist))
            {
                result.Error = "该指令不在允许的白名单内";
                return result;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    // 不再强制 chcp 65001：让 cmd 用系统默认代码页输出，
                    // 否则 GBK 环境下的中文会被当成 UTF-8 处理成乱七八糟的字。
                    Arguments = "/d /s /c \"" + command.Replace("\"", "\\\"") + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                };

                using var proc = new Process { StartInfo = psi };

                // 直接读原始字节，自己判断编码（UTF-8 还是 GBK）
                var outBuf = new MemoryStream();
                var errBuf = new MemoryStream();

                proc.Start();

                var tOut = Task.Run(() => { try { proc.StandardOutput.BaseStream.CopyTo(outBuf); } catch { } });
                var tErr = Task.Run(() => { try { proc.StandardError.BaseStream.CopyTo(errBuf); } catch { } });

                if (!proc.WaitForExit(Math.Max(1, timeoutSeconds) * 1000))
                {
                    result.TimedOut = true;
                    try { proc.Kill(true); } catch { }
                }

                try { Task.WaitAll(new[] { tOut, tErr }, 2000); } catch { }

                result.Ok = !result.TimedOut;
                result.ExitCode = result.TimedOut ? -1 : proc.ExitCode;
                result.Output = Truncate(DecodeSmart(outBuf.ToArray()));
                result.Error = Truncate(DecodeSmart(errBuf.ToArray()));

                Log.Write($"远程指令执行: {command} (exit={result.ExitCode}, timeout={result.TimedOut})");
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                Log.Write("远程指令失败: " + ex.Message);
                return result;
            }
        }

        /// <summary>
        /// 先按严格 UTF-8 试一次；不合法就退回系统 OEM 代码页（简体中文 936/GBK）。
        /// 这样 cmd 原生的 GBK 输出和 powershell 等 UTF-8 输出都能正确显示。
        /// </summary>
        private static string DecodeSmart(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";

            // UTF-8 BOM
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

            // UTF-16 LE BOM
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch { }

            try
            {
                uint cp = GetOEMCP();
                if (cp > 0)
                {
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    return Encoding.GetEncoding((int)cp).GetString(bytes);
                }
            }
            catch { }

            return Encoding.Default.GetString(bytes);
        }

        private static string Truncate(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.Length <= MaxOutputChars) return text;
            return text.Substring(0, MaxOutputChars) + "\n…（输出已截断）";
        }
    }
}
