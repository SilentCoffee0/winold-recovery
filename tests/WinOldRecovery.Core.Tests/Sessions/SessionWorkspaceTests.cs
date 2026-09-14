using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Core.Sessions;

namespace WinOldRecovery.Core.Tests.Sessions;

public sealed class SessionWorkspaceTests
{
    [Fact]
    public void Create_BuildsRequiredSessionLayout()
    {
        string root = CreateTestRoot();
        try
        {
            SafeFs safeFs = new(new SourceGuard());
            DateTimeOffset timestamp = new(2026, 9, 13, 12, 34, 56, TimeSpan.FromHours(8));

            SessionWorkspace workspace = SessionWorkspace.Create(
                safeFs,
                root,
                timestamp);

            Assert.StartsWith("2026-09-13_123456_", workspace.SessionId);
            Assert.True(Directory.Exists(workspace.RootPath));
            Assert.True(Directory.Exists(workspace.ExportsPath));
            Assert.True(Directory.Exists(workspace.TemporaryPath));
            Assert.Equal(
                Path.Combine(workspace.RootPath, "session.db"),
                workspace.DatabasePath);
            Assert.Equal(Path.Combine(workspace.RootPath, "log.txt"), workspace.LogPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void I1_CreateRefusesSessionFolderInsideSource()
    {
        string root = CreateTestRoot();
        string source = Path.Combine(root, "Windows.old");
        Directory.CreateDirectory(source);
        SourceGuard guard = new();
        guard.RegisterSourceRoot(source);
        SafeFs safeFs = new(guard);

        try
        {
            Assert.Throws<SourceWriteDeniedException>(
                () => SessionWorkspace.Create(safeFs, source));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SessionRecordExport_WritesACopyOutsideTheSource()
    {
        string root = CreateTestRoot();
        try
        {
            SafeFs safeFs = new(new SourceGuard());
            SessionWorkspace workspace = SessionWorkspace.Create(safeFs, root);
            await using SessionDb database = await SessionDb.OpenAsync(workspace.DatabasePath, safeFs);
            await database.CreateSessionAsync(
                new SessionRecord(workspace.SessionId, DateTimeOffset.UtcNow, "Created", "0.1.0"));

            string path = SessionRecordExport.Write(safeFs, workspace, database);
            string text = await File.ReadAllTextAsync(path);

            Assert.Equal(Path.Combine(workspace.RootPath, SessionRecordExport.FileName), path);
            Assert.Contains("sessionId=" + workspace.SessionId, text, StringComparison.Ordinal);
            Assert.Contains("planItems=0", text, StringComparison.Ordinal);
            Assert.Contains("verifyAllOk=False", text, StringComparison.Ordinal);
            Assert.Contains("verifySettled=False", text, StringComparison.Ordinal);
            Assert.Contains("purged=False", text, StringComparison.Ordinal);
            await database.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTestRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"WinOldRecovery-SessionWorkspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
