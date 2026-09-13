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
        File.Move(validatedSource, validatedDestination, overwrite);
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
}
