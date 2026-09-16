using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Sessions;

namespace WinOldRecovery.Core.Purge;

public static class SupportBundle
{
    public static string Create(SafeFs safeFs, SessionWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(safeFs);
        ArgumentNullException.ThrowIfNull(workspace);

        string bundleDirectory = Path.Combine(workspace.RootPath, "support-bundle");
        safeFs.CreateDirectory(bundleDirectory);
        CopyIfExists(safeFs, workspace.LogPath, Path.Combine(bundleDirectory, "log.txt"));
        string manifest = Path.Combine(workspace.RootPath, "purge-manifest.txt");
        CopyIfExists(safeFs, manifest, Path.Combine(bundleDirectory, "purge-manifest.txt"));
        string zipPath = Path.Combine(workspace.RootPath, "support-bundle.zip");
        if (safeFs.FileExists(zipPath))
        {
            safeFs.DeleteFile(zipPath);
        }

        safeFs.CreateZipFromDirectory(bundleDirectory, zipPath);
        return zipPath;
    }

    private static void CopyIfExists(SafeFs safeFs, string source, string destination)
    {
        if (!safeFs.FileExists(source))
        {
            return;
        }

        safeFs.CopyReadToWrite(source, destination);
    }
}
