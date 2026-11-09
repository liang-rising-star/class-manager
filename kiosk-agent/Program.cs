using System;
using System.Threading;
using System.Windows.Forms;
using ClassManager.KioskAgent;

namespace ClassManager.KioskAgent
{
    internal static class Program
    {
        private static int ParseWaitPid(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i].Equals("--wait-pid", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(args[i + 1], out var pid))
                    return pid;
            return 0;
        }

        private const string MutexName = @"Local\ClassManager.KioskAgent.SingleInstance";

        [STAThread]
        private static void Main(string[] args)
        {
            // 「重启」时新进程会带 --wait-pid：先等旧进程真正退出，再抢单实例锁
            int waitPid = ParseWaitPid(args);
            if (waitPid > 0)
            {
                try
                {
                    var old = System.Diagnostics.Process.GetProcessById(waitPid);
                    old.WaitForExit(8000);
                }
                catch { }
                Thread.Sleep(400);
            }

            // 严格单实例：第二个进程直接退出，不唤起任何界面
            using var mutex = new Mutex(true, MutexName, out bool isFirst);
            if (!isFirst)
            {
                Log.Write("已有实例在运行，本次启动直接退出");
                return;
            }

            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Log.Write("UI 异常: " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("未处理异常: " + e.ExceptionObject);

            // 启动后始终只在托盘；要打开界面只能右键托盘菜单
            var app = new TrayApp();
            if (Array.Exists(args, a => a.Equals("--preview-menu", StringComparison.OrdinalIgnoreCase)))
                app.ShowMenuPreview();
            if (Array.Exists(args, a => a.Equals("--show", StringComparison.OrdinalIgnoreCase)))
                app.ShowWindowOnStart();
            if (Array.Exists(args, a => a.Equals("--preview-dialog", StringComparison.OrdinalIgnoreCase)))
                app.ShowPasswordDialogPreview();
            Application.Run(app);
        }
    }
}
