using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClassManager.KioskAgent
{
    /// <summary>开机自启（写入当前用户 Run 项，带 --tray 参数）。</summary>
    public static class AutoStart
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "ClassManagerKioskAgent";

        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(RunValueName) != null;
            }
            catch
            {
                return false;
            }
        }

        public static bool Set(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
                if (key == null) return false;

                if (enabled)
                {
                    var exe = Application.ExecutablePath;
                    key.SetValue(RunValueName, $"\"{exe}\" --tray");
                }
                else
                {
                    key.DeleteValue(RunValueName, false);
                }

                Log.Write("开机自启: " + (enabled ? "已开启" : "已关闭"));
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("设置开机自启失败: " + ex.Message);
                return false;
            }
        }
    }
}
