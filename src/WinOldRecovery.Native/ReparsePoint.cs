using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinOldRecovery.Native;

public static class ReparsePoint
{
    public const uint TagMountPoint = 0xA0000003;
    public const uint TagSymlink = 0xA000000C;
    public const uint TagLxSymlink = 0xA000001D;
    public const uint TagAppExecLink = 0x8000001B;

    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagOpenNoRecall = 0x00100000;
    private const int FileAttributeTagInfoClass = 9;
    private const uint FsctlGetReparsePoint = 0x000900A8;
    private const int MaximumReparseDataBufferSize = 16 * 1024;
    private const int ErrorNotAReparsePoint = 4390;

    public static ReparsePointInfo Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using SafeFileHandle handle = CreateFile(
            path,
            desiredAccess: 0,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint | FileFlagOpenNoRecall,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not open '{path}' as a reparse point.");
        }

        FileAttributeTagInfo tagInfo = default;
        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfoClass,
                out tagInfo,
                Marshal.SizeOf<FileAttributeTagInfo>()))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not read the reparse tag for '{path}'.");
        }

        byte[] buffer = new byte[MaximumReparseDataBufferSize];
        if (!DeviceIoControl(
                handle,
                FsctlGetReparsePoint,
                IntPtr.Zero,
                0,
                buffer,
                buffer.Length,
                out int bytesReturned,
                IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorNotAReparsePoint)
            {
                return new ReparsePointInfo(tagInfo.ReparseTag, string.Empty);
            }

            throw new Win32Exception(error, $"Could not read the reparse payload for '{path}'.");
        }

        string target = ParseTarget(buffer, bytesReturned, tagInfo.ReparseTag);
        return new ReparsePointInfo(tagInfo.ReparseTag, target);
    }

    private static string ParseTarget(byte[] buffer, int bytesReturned, uint tag)
    {
        if (bytesReturned < 8)
        {
            return string.Empty;
        }

        if (tag == TagSymlink)
        {
            return ReadMicrosoftLinkTarget(buffer, includeFlags: true);
        }

        if (tag == TagMountPoint)
        {
            return ReadMicrosoftLinkTarget(buffer, includeFlags: false);
        }

        if (tag == TagLxSymlink)
        {
            return ReadLxSymlinkTarget(buffer, bytesReturned);
        }

        return string.Empty;
    }

    private static string ReadMicrosoftLinkTarget(byte[] buffer, bool includeFlags)
    {
        const int headerSize = 8;
        ushort substituteNameOffset = BitConverter.ToUInt16(buffer, headerSize);
        ushort substituteNameLength = BitConverter.ToUInt16(buffer, headerSize + 2);
        ushort printNameOffset = BitConverter.ToUInt16(buffer, headerSize + 4);
        ushort printNameLength = BitConverter.ToUInt16(buffer, headerSize + 6);
        int pathBufferOffset = headerSize + 8 + (includeFlags ? 4 : 0);

        string printName = ReadUnicode(buffer, pathBufferOffset + printNameOffset, printNameLength);
        if (!string.IsNullOrEmpty(printName))
        {
            return NormalizeWin32Target(printName);
        }

        string substituteName = ReadUnicode(
            buffer,
            pathBufferOffset + substituteNameOffset,
            substituteNameLength);
        return NormalizeWin32Target(substituteName);
    }

    private static string ReadLxSymlinkTarget(byte[] buffer, int bytesReturned)
    {
        const int dataOffset = 12;
        if (bytesReturned <= dataOffset)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(buffer, dataOffset, bytesReturned - dataOffset)
            .TrimEnd('\0');
    }

    private static string ReadUnicode(byte[] buffer, int offset, int byteLength)
    {
        if (byteLength <= 0 || offset < 0 || offset + byteLength > buffer.Length)
        {
            return string.Empty;
        }

        return Encoding.Unicode.GetString(buffer, offset, byteLength);
    }

    private static string NormalizeWin32Target(string target)
    {
        if (target.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            return target[4..];
        }

        return target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        out FileAttributeTagInfo fileInformation,
        int bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle fileHandle,
        uint ioControlCode,
        IntPtr inBuffer,
        int inBufferSize,
        byte[] outBuffer,
        int outBufferSize,
        out int bytesReturned,
        IntPtr overlapped);
}

public readonly record struct ReparsePointInfo(uint Tag, string Target);
