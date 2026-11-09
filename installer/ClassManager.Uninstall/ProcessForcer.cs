using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClassManager.Uninstall;

/// <summary>
/// 结束进程的兜底工具：
/// 普通 Kill 失败（被自身安全描述符保护 / 权限不足）时，先清掉进程 DACL 再杀。
/// 一体机端会给自己加「拒绝终止」的保护，卸载时必须先绕过它。
/// </summary>
internal static class ProcessForcer
{
    private const uint WRITE_DAC = 0x00040000;
    private const uint DACL_SECURITY_INFORMATION = 0x00000004;
    private const int SE_KERNEL_OBJECT = 6;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int SetSecurityInfo(IntPtr handle, int objectType, uint securityInfo,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    /// <summary>结束进程；返回是否确认已退出。</summary>
    public static bool TryKill(Process proc, int waitMs = 4000)
    {
        try
        {
            if (proc.HasExited) return true;
        }
        catch { return true; }

        try
        {
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(waitMs);
            if (proc.HasExited) return true;
        }
        catch { }

        // 被进程自身安全描述符挡住 → 先放开 DACL
        if (Unlock(proc.Id))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(waitMs);
                if (proc.HasExited) return true;
            }
            catch { }
        }

        return false;
    }

    /// <summary>把进程的 DACL 置空（等于放开所有权限），仅用于结束被保护的进程。</summary>
    public static bool Unlock(int pid)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(WRITE_DAC, false, pid);
            if (handle == IntPtr.Zero) return false;

            return SetSecurityInfo(handle, SE_KERNEL_OBJECT, DACL_SECURITY_INFORMATION,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (handle != IntPtr.Zero) CloseHandle(handle);
        }
    }
}
