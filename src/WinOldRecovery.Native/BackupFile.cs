using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinOldRecovery.Native;

public static class BackupFile
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenNoRecall = 0x00100000;
    private const uint InvalidFileAttributes = 0xFFFFFFFF;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileAttributeOffline = 0x00001000;
    private const uint FileAttributeEncrypted = 0x00004000;
    private const uint FileAttributeRecallOnOpen = 0x00040000;
    private const uint FileAttributeRecallOnDataAccess = 0x00400000;

    public static FileStream OpenRead(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        uint attributes = GetFileAttributes(path);
        if (attributes == InvalidFileAttributes)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not inspect '{path}' before opening it.");
        }

        const uint unsafeAttributes =
            FileAttributeReparsePoint |
            FileAttributeOffline |
            FileAttributeEncrypted |
            FileAttributeRecallOnOpen |
            FileAttributeRecallOnDataAccess;

        if ((attributes & unsafeAttributes) != 0)
        {
            throw new IOException(
                $"Refused to open '{path}' because it is reparse-backed, offline, or encrypted.");
        }

        SafeFileHandle handle = CreateFile(
            path,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics |
            FileFlagSequentialScan |
            FileFlagOpenReparsePoint |
            FileFlagOpenNoRecall,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Could not open '{path}' for a backup-mode read.");
        }

        return new FileStream(handle, FileAccess.Read);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileAttributesW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributes(string fileName);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}
