using System.ComponentModel;
using System.Security.AccessControl;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Native;

namespace WinOldRecovery.Core.IO;

public sealed class SafeFs
{
    private readonly SourceGuard sourceGuard;

    public SafeFs(SourceGuard sourceGuard)
    {
        this.sourceGuard = sourceGuard ?? throw new ArgumentNullException(nameof(sourceGuard));
    }

    internal bool FailNextWriteAsDiskFull { get; set; }

    public FileStream OpenRead(string path)
    {
        string normalizedPath = PathCanonicalizer.NormalizeLexically(path);
        return BackupFile.OpenRead(normalizedPath);
    }

    public bool TryReadSizes(string path, out long fileSize, out long allocatedSize)
    {
        fileSize = 0;
        allocatedSize = 0;
        try
        {
            using FileStream stream = OpenRead(path);
            (fileSize, allocatedSize) = FileSizes.Read(stream.SafeFileHandle);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return false;
        }
    }

    internal string GetValidatedWritePath(string path, PurgeToken? purgeToken = null)
    {
        return sourceGuard.GetValidatedWritePath(path, purgeToken);
    }

    public DirectoryInfo CreateDirectory(string path, PurgeToken? purgeToken = null)
    {
        string validatedPath = sourceGuard.GetValidatedWritePath(path, purgeToken);
        return Directory.CreateDirectory(validatedPath);
    }

    public FileStream OpenWrite(
        string path,
        FileMode mode,
        PurgeToken? purgeToken = null,
        FileShare share = FileShare.None)
    {
        string validatedPath = sourceGuard.GetValidatedWritePath(path, purgeToken);
        if (FailNextWriteAsDiskFull)
        {
            FailNextWriteAsDiskFull = false;
            throw new IOException(
                "There is not enough space on the disk.",
                new Win32Exception(112));
        }

        return new FileStream(validatedPath, mode, FileAccess.Write, share);
    }

    public FileStream OpenShareRead(string path)
    {
        string validatedPath = sourceGuard.GetValidatedWritePath(path, purgeToken: null);
        return new FileStream(validatedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }

    public void MoveFile(
        string sourcePath,
        string destinationPath,
        bool overwrite = false,
        PurgeToken? purgeToken = null)
    {
        string validatedSource = sourceGuard.GetValidatedWritePath(sourcePath, purgeToken);
        string validatedDestination =
            sourceGuard.GetValidatedWritePath(destinationPath, purgeToken);
        NativeFile.Move(validatedSource, validatedDestination, overwrite);
    }

    public void DeleteFile(string path, PurgeToken? purgeToken = null)
    {
        string validatedPath = sourceGuard.GetValidatedWritePath(path, purgeToken);
        NativeFile.Delete(validatedPath);
    }

    public void DeleteDirectory(
        string path,
        bool recursive = false,
        PurgeToken? purgeToken = null)
    {
        string validatedPath = sourceGuard.GetValidatedWritePath(path, purgeToken);
        FileAttributes attributes = File.GetAttributes(validatedPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            NativeFile.RemoveDirectory(validatedPath);
            return;
        }

        Directory.Delete(validatedPath, recursive);
    }

    public void SetCreationTimeUtc(
        string path,
        DateTime creationTimeUtc,
        PurgeToken? purgeToken = null)
    {
        string validatedPath = sourceGuard.GetValidatedWritePath(path, purgeToken);
        File.SetCreationTimeUtc(validatedPath, creationTimeUtc);
    }

    public void SetLastWriteTimeUtc(
        string path,
        DateTime lastWriteTimeUtc,
        PurgeToken? purgeToken = null)
    {
        string validatedPath = sourceGuard.GetValidatedWritePath(path, purgeToken);
        File.SetLastWriteTimeUtc(validatedPath, lastWriteTimeUtc);
    }

    public void SetAttributes(
        string path,
        FileAttributes attributes,
        PurgeToken? purgeToken = null)
    {
        string validatedPath = sourceGuard.GetValidatedWritePath(path, purgeToken);
        File.SetAttributes(validatedPath, attributes);
    }

    public void SetAccessControl(
        string path,
        FileSecurity security,
        PurgeToken? purgeToken = null)
    {
        ArgumentNullException.ThrowIfNull(security);
        string validatedPath = sourceGuard.GetValidatedWritePath(path, purgeToken);
        new FileInfo(validatedPath).SetAccessControl(security);
    }

    public FileSystemInfo CreateSymbolicLink(
        string path,
        string targetPath,
        bool isDirectory,
        PurgeToken? purgeToken = null)
    {
        string validatedPath = sourceGuard.GetValidatedWritePath(path, purgeToken);
        return isDirectory
            ? Directory.CreateSymbolicLink(validatedPath, targetPath)
            : File.CreateSymbolicLink(validatedPath, targetPath);
    }

    public bool FileExists(string path)
    {
        string normalizedPath = PathCanonicalizer.NormalizeLexically(path);
        return File.Exists(normalizedPath);
    }

    public bool DirectoryExists(string path)
    {
        string normalizedPath = PathCanonicalizer.NormalizeLexically(path);
        return Directory.Exists(normalizedPath);
    }

    public IReadOnlyList<string> EnumerateFileSystemEntries(string path)
    {
        string normalizedPath = PathCanonicalizer.NormalizeLexically(path);
        if (!Directory.Exists(normalizedPath))
        {
            return [];
        }

        return Directory.EnumerateFileSystemEntries(normalizedPath).ToArray();
    }

    public IReadOnlyList<string> EnumerateDirectories(string path)
    {
        string normalizedPath = PathCanonicalizer.NormalizeLexically(path);
        if (!Directory.Exists(normalizedPath))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateDirectories(normalizedPath).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public string ReadAllText(string path)
    {
        using FileStream stream = OpenRead(path);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    public byte[] ReadAllBytes(string path)
    {
        using FileStream stream = OpenRead(path);
        using MemoryStream memory = new();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public void WriteAllText(string path, string contents, PurgeToken? purgeToken = null)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            CreateDirectory(directory, purgeToken);
        }

        using FileStream stream = OpenWrite(path, FileMode.Create, purgeToken);
        using StreamWriter writer = new(stream);
        writer.Write(contents);
    }

    public void CopyReadToWrite(string sourcePath, string destinationPath, PurgeToken? purgeToken = null)
    {
        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            CreateDirectory(directory, purgeToken);
        }

        using FileStream input = OpenRead(sourcePath);
        using FileStream output = OpenWrite(destinationPath, FileMode.Create, purgeToken);
        input.CopyTo(output);
    }
}
