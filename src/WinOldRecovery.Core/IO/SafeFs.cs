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

    public FileStream OpenRead(string path)
    {
        string normalizedPath = PathCanonicalizer.NormalizeLexically(path);
        return BackupFile.OpenRead(normalizedPath);
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
        return new FileStream(validatedPath, mode, FileAccess.Write, share);
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
        File.Delete(validatedPath);
    }

    public void DeleteDirectory(
        string path,
        bool recursive = false,
        PurgeToken? purgeToken = null)
    {
        string validatedPath = sourceGuard.GetValidatedWritePath(path, purgeToken);
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

    public string ReadAllText(string path)
    {
        using FileStream stream = OpenRead(path);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
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
}
