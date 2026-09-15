using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinOldRecovery.Native;

public static class FileSizes
{
    private const int FileStandardInfoClass = 1;

    public static (long FileSize, long AllocatedSize) Read(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsInvalid)
        {
            throw new ArgumentException("File handle is invalid.", nameof(handle));
        }

        FileStandardInfo info = default;
        if (!GetFileInformationByHandleEx(
                handle,
                FileStandardInfoClass,
                out info,
                Marshal.SizeOf<FileStandardInfo>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read file allocation size.");
        }

        return (info.EndOfFile, info.AllocationSize);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        out FileStandardInfo fileInformation,
        int bufferSize);
}
