using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinOldRecovery.Native;

public static class Privileges
{
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const int ErrorNotAllAssigned = 1300;

    public static void EnableBackupAndRestore()
    {
        if (!OpenProcessToken(
                GetCurrentProcess(),
                TokenAdjustPrivileges | TokenQuery,
                out SafeAccessTokenHandle token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the process token.");
        }

        using (token)
        {
            EnablePrivilege(token, "SeBackupPrivilege");
            EnablePrivilege(token, "SeRestorePrivilege");
        }
    }

    private static void EnablePrivilege(SafeAccessTokenHandle token, string privilegeName)
    {
        if (!LookupPrivilegeValue(null, privilegeName, out Luid luid))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not resolve the {privilegeName} privilege.");
        }

        TokenPrivileges privileges = new()
        {
            PrivilegeCount = 1,
            Privileges = new LuidAndAttributes
            {
                Luid = luid,
                Attributes = SePrivilegeEnabled,
            },
        };

        if (!AdjustTokenPrivileges(
                token,
                disableAllPrivileges: false,
                ref privileges,
                bufferLength: 0,
                previousState: IntPtr.Zero,
                returnLength: IntPtr.Zero))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not enable the {privilegeName} privilege.");
        }

        int error = Marshal.GetLastWin32Error();
        if (error == ErrorNotAllAssigned)
        {
            throw new Win32Exception(
                error,
                $"The process token does not contain the {privilegeName} privilege. Run elevated.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privileges;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(
        string? systemName,
        string name,
        out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        SafeAccessTokenHandle tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);
}
