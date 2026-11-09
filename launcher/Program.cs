using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClassManager.Launcher;

/// <summary>
/// 安装目录根部的启动器（原生 AOT 编译，单文件、无运行时依赖）：
///   class-manager\
///     class-manager.exe          ← 本程序，双击即启动
///     desktop\                 ← 桌面程序本体与 .NET 依赖
///     class-manager-system\      ← 后端（Python 运行时、模板、数据）
/// 只负责拉起 desktop 里的主程序，然后自己退出，
/// 这样任务管理器里看到的仍是主程序 class-manager。
/// </summary>
internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_ICONERROR = 0x00000010;

    private static void Error(string text) =>
        MessageBoxW(IntPtr.Zero, text, "class-manager", MB_ICONERROR);

    [STAThread]
    private static int Main(string[] args)
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var core = Path.Combine(baseDir, "desktop", "class-manager.exe");

        if (!File.Exists(core))
        {
            Error("找不到主程序：\n" + core +
                  "\n\n请确认 desktop 文件夹没有丢失或被杀毒软件隔离。");
            return 1;
        }

        var startInfo = new ProcessStartInfo(core)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.Combine(baseDir, "desktop")
        };

        // 默认把安装目录当根目录传下去；用户显式给了 --root 就保持原样
        var hasRoot = false;
        foreach (var a in args)
        {
            if (a is "--root" or "-r") { hasRoot = true; break; }
        }

        if (!hasRoot)
        {
            startInfo.ArgumentList.Add("--root");
            startInfo.ArgumentList.Add(baseDir);
        }

        foreach (var a in args)
        {
            startInfo.ArgumentList.Add(a);
        }

        try
        {
            Process.Start(startInfo);
            return 0;
        }
        catch (Exception ex)
        {
            Error("启动失败：" + ex.Message);
            return 1;
        }
    }
}
