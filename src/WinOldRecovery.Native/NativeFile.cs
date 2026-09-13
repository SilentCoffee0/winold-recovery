using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinOldRecovery.Native;

public static class DiskSpace
{
    public static long GetFreeBytes(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (!GetDiskFreeSpaceEx(directoryPath, out ulong freeForCaller, out _, out _))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not read free space for '{directoryPath}'.");
        }

        return checked((long)freeForCaller);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);
}

public static class NativeFile
{
    private const uint MoveFileReplaceExisting = 0x00000001;

    public static void Move(string sourcePath, string destinationPath, bool replaceExisting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        uint flags = replaceExisting ? MoveFileReplaceExisting : 0;
        if (!MoveFileEx(sourcePath, destinationPath, flags))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not move '{sourcePath}' to '{destinationPath}'.");
        }
    }

    public static void RemoveDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!RemoveDirectoryW(path))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not remove directory '{path}'.");
        }
    }

    public static void Delete(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!DeleteFileW(path))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not delete '{path}'.");
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "RemoveDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveDirectoryW(string pathName);

    [DllImport("kernel32.dll", EntryPoint = "DeleteFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFileW(string fileName);
}
