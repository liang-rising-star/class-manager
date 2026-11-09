using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace ClassManager.KioskAgent
{
    /// <summary>
    /// 进程保护：给当前进程的安全描述符加一条「拒绝终止」的规则，
    /// 这样任务管理器（普通权限）/ taskkill 会提示拒绝访问，只能从托盘「退出」走正常流程。
    ///
    /// 注意：
    ///   - 管理员权限的任务管理器仍然能结束进程（不装驱动/服务无法彻底阻止）；
    ///   - 退出程序前必须调用 Unprotect()，否则自己也退不掉。
    /// </summary>
    public static class ProcessGuard
    {
        private const int SE_KERNEL_OBJECT = 6;
        private const uint DACL_SECURITY_INFORMATION = 0x00000004;
        private const uint READ_CONTROL = 0x00020000;
        private const uint WRITE_DAC = 0x00040000;

        private const int PROCESS_TERMINATE = 0x0001;
        private const int DENY_ACCESS = 3;
        private const int TRUSTEE_IS_SID = 0;
        private const int TRUSTEE_IS_WELL_KNOWN_GROUP = 5;

        /// <summary>Everyone 的 SID：S-1-1-0</summary>
        private static readonly byte[] EveryoneSid =
        {
            0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x00
        };

        private static IntPtr _originalSd = IntPtr.Zero;   // 原始安全描述符（用于还原）
        private static IntPtr _originalDacl = IntPtr.Zero;
        private static IntPtr _denyAcl = IntPtr.Zero;
        private static bool _active;

        [StructLayout(LayoutKind.Sequential)]
        private struct EXPLICIT_ACCESS
        {
            public int grfAccessPermissions;
            public int grfAccessMode;
            public int grfInheritance;
            public TRUSTEE Trustee;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TRUSTEE
        {
            public IntPtr pMultipleTrustee;
            public int MultipleTrusteeOperation;
            public int TrusteeForm;
            public int TrusteeType;
            public IntPtr ptstrName;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern int GetSecurityInfo(IntPtr handle, int objectType, uint securityInfo,
            out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr sd);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern int SetSecurityInfo(IntPtr handle, int objectType, uint securityInfo,
            IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int SetEntriesInAclW(int count, [In] EXPLICIT_ACCESS[] entries,
            IntPtr oldAcl, out IntPtr newAcl);

        [DllImport("advapi32.dll")]
        private static extern IntPtr LocalFree(IntPtr mem);

        public static bool IsActive => _active;

        /// <summary>开启保护。失败只记日志，不影响程序运行。</summary>
        public static bool Protect()
        {
            if (_active) return true;

            IntPtr handle = IntPtr.Zero;
            try
            {
                int pid = Process.GetCurrentProcess().Id;

                handle = OpenProcess(READ_CONTROL | WRITE_DAC, false, pid);
                if (handle == IntPtr.Zero)
                {
                    Log.Write("进程保护：无法打开自身进程（" + Marshal.GetLastWin32Error() + "）");
                    return false;
                }

                int rc = GetSecurityInfo(handle, SE_KERNEL_OBJECT, DACL_SECURITY_INFORMATION,
                    out _, out _, out IntPtr dacl, out _, out IntPtr sd);
                if (rc != 0)
                {
                    Log.Write("进程保护：读取安全描述符失败（" + rc + "）");
                    return false;
                }

                _originalSd = sd;
                _originalDacl = dacl;

                // 构造「Everyone 拒绝 PROCESS_TERMINATE」
                IntPtr sidBuf = Marshal.AllocHGlobal(EveryoneSid.Length);
                Marshal.Copy(EveryoneSid, 0, sidBuf, EveryoneSid.Length);

                var ea = new EXPLICIT_ACCESS
                {
                    grfAccessPermissions = PROCESS_TERMINATE,
                    grfAccessMode = DENY_ACCESS,
                    grfInheritance = 0,
                    Trustee = new TRUSTEE
                    {
                        pMultipleTrustee = IntPtr.Zero,
                        MultipleTrusteeOperation = 0,
                        TrusteeForm = TRUSTEE_IS_SID,
                        TrusteeType = TRUSTEE_IS_WELL_KNOWN_GROUP,
                        ptstrName = sidBuf
                    }
                };

                rc = SetEntriesInAclW(1, new[] { ea }, dacl, out IntPtr newAcl);
                Marshal.FreeHGlobal(sidBuf);

                if (rc != 0)
                {
                    Log.Write("进程保护：生成 ACL 失败（" + rc + "）");
                    return false;
                }

                _denyAcl = newAcl;

                rc = SetSecurityInfo(handle, SE_KERNEL_OBJECT, DACL_SECURITY_INFORMATION,
                    IntPtr.Zero, IntPtr.Zero, newAcl, IntPtr.Zero);

                if (rc != 0)
                {
                    Log.Write("进程保护：写入 ACL 失败（" + rc + "）");
                    return false;
                }

                _active = true;
                Log.Write("进程保护已开启：任务管理器无法直接结束本进程");
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("进程保护异常: " + ex.Message);
                return false;
            }
            finally
            {
                if (handle != IntPtr.Zero) CloseHandle(handle);
            }
        }

        /// <summary>退出前还原，否则自己也结束不掉。</summary>
        public static void Unprotect()
        {
            if (!_active) return;

            IntPtr handle = IntPtr.Zero;
            try
            {
                int pid = Process.GetCurrentProcess().Id;
                handle = OpenProcess(READ_CONTROL | WRITE_DAC, false, pid);
                if (handle == IntPtr.Zero) return;

                int rc = SetSecurityInfo(handle, SE_KERNEL_OBJECT, DACL_SECURITY_INFORMATION,
                    IntPtr.Zero, IntPtr.Zero, _originalDacl, IntPtr.Zero);

                if (rc == 0)
                {
                    _active = false;
                    Log.Write("进程保护已关闭");
                }
            }
            catch (Exception ex)
            {
                Log.Write("关闭进程保护失败: " + ex.Message);
            }
            finally
            {
                if (handle != IntPtr.Zero) CloseHandle(handle);
            }
        }
    }
}
