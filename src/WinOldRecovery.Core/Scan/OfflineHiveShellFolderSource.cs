using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Registry;

namespace WinOldRecovery.Core.Scan;

public sealed class OfflineHiveShellFolderSource : IShellFolderValueSource
{
    public const string UserShellFoldersKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";

    private readonly SafeFs safeFs;
    private readonly string sessionTemporaryDirectory;

    public OfflineHiveShellFolderSource(SafeFs safeFs, string sessionTemporaryDirectory)
    {
        this.safeFs = safeFs ?? throw new ArgumentNullException(nameof(safeFs));
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionTemporaryDirectory);
        this.sessionTemporaryDirectory = sessionTemporaryDirectory;
    }

    public IReadOnlyDictionary<string, string> GetValues(string profileRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileRoot);

        string hivePath = Path.Combine(profileRoot, "NTUSER.DAT");
        if (!File.Exists(PathCanonicalizer.ToExtendedPath(hivePath)) && !File.Exists(hivePath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            OfflineRegistryHive hive = OfflineRegistryHive.OpenCopyAsync(
                    hivePath,
                    sessionTemporaryDirectory,
                    safeFs)
                .GetAwaiter()
                .GetResult();
            return hive.GetStringValues(UserShellFoldersKey);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not StackOverflowException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
