using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
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
                    // 65001 保证中文输出不乱码
                    Arguments = "/d /s /c \"chcp 65001>nul & " + command.Replace("\"", "\\\"") + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                };

                using var proc = new Process { StartInfo = psi };
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) Append(stdout, e.Data + "\n"); };
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) Append(stderr, e.Data + "\n"); };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                if (!proc.WaitForExit(Math.Max(1, timeoutSeconds) * 1000))
                {
                    result.TimedOut = true;
                    try { proc.Kill(true); } catch { }
                }

                result.Ok = !result.TimedOut;
                result.ExitCode = result.TimedOut ? -1 : proc.ExitCode;
                result.Output = stdout.ToString();
                result.Error = stderr.ToString();
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

        private static void Append(StringBuilder sb, string text)
        {
            if (sb.Length >= MaxOutputChars) return;
            if (sb.Length + text.Length > MaxOutputChars)
            {
                sb.Append(text, 0, MaxOutputChars - sb.Length);
                sb.Append("\n…（输出已截断）");
                return;
            }
            sb.Append(text);
        }
    }
}
