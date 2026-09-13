using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinOldRecovery.Native;

public static class NativePath
{
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenNoRecall = 0x00100000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    public static bool TryGetFinalPath(string path, out string finalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using SafeFileHandle handle = CreateFile(
            path,
            desiredAccess: 0,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenNoRecall,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is not ErrorFileNotFound and not ErrorPathNotFound)
            {
                throw new Win32Exception(error, $"Could not safely resolve '{path}'.");
            }

            finalPath = string.Empty;
            return false;
        }

        int capacity = 512;
        while (true)
        {
            StringBuilder buffer = new(capacity);
            uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Could not resolve the final path for '{path}'.");
            }

            if (length < buffer.Capacity)
            {
                finalPath = buffer.ToString();
                return true;
            }

            capacity = checked((int)length + 1);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);
}
