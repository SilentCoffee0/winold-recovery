using System.Globalization;
using WinOldRecovery.Core.IO;

namespace WinOldRecovery.Core.Sessions;

public sealed record SessionWorkspace(
    string SessionId,
    string RootPath,
    string DatabasePath,
    string LogPath,
    string ExportsPath,
    string TemporaryPath)
{
    public static SessionWorkspace Create(
        SafeFs safeFs,
        string? localApplicationData = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(safeFs);

        string basePath = localApplicationData ??
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(basePath))
        {
            throw new InvalidOperationException(
                "Windows did not provide a Local Application Data directory.");
        }

        DateTimeOffset timestamp = now ?? DateTimeOffset.Now;
        string sessionId =
            timestamp.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture) +
            "_" +
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
        string root = Path.Combine(
            basePath,
            "WinOldRecovery",
            "sessions",
            sessionId);
        string exports = Path.Combine(root, "exports");
        string temporary = Path.Combine(root, "tmp");

        safeFs.CreateDirectory(root);
        safeFs.CreateDirectory(exports);
        safeFs.CreateDirectory(temporary);

        return new SessionWorkspace(
            sessionId,
            root,
            Path.Combine(root, "session.db"),
            Path.Combine(root, "log.txt"),
            exports,
            temporary);
    }

    public static SessionWorkspace Open(string rootPath, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return new SessionWorkspace(
            sessionId,
            rootPath,
            Path.Combine(rootPath, "session.db"),
            Path.Combine(rootPath, "log.txt"),
            Path.Combine(rootPath, "exports"),
            Path.Combine(rootPath, "tmp"));
    }
}
