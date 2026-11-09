using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClassManager.KioskSetup;

/// <summary>
/// 结束进程的兜底工具：普通 Kill 失败时先清掉进程 DACL 再杀。
/// 一体机端会给自己加「拒绝终止」的保护，卸载/覆盖安装时必须先绕过它。
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
