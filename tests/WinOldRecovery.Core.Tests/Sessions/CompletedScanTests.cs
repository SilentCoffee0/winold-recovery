using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Core.Sessions;

namespace WinOldRecovery.Core.Tests.Sessions;

public sealed class CompletedScanTests
{
    [Fact]
    public void TryRead_ReturnsNullWhenTheSourceFolderIsGone()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinOldRecovery-LastScan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SafeFs safeFs = new(new SourceGuard());
            string workspace = Path.Combine(root, "WinOldRecovery", "sessions", "session-1");
            Directory.CreateDirectory(workspace);
            string database = Path.Combine(workspace, "session.db");
            File.WriteAllText(database, "db");
            string pointerPath = CompletedScan.PointerPathFromWorkspace(workspace);
            CompletedScan.Write(
                safeFs,
                new CompletedScanPointer(
                    "session-1",
                    workspace,
                    database,
                    Path.Combine(root, "missing-windows.old"),
                    DateTimeOffset.UtcNow,
                    10,
                    100,
                    1,
                    2),
                pointerPath);

            Assert.Null(CompletedScan.TryRead(safeFs, pointerPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryRead_ReturnsThePointerWhenSourceAndDatabaseExist()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinOldRecovery-LastScan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SafeFs safeFs = new(new SourceGuard());
            string workspace = Path.Combine(root, "WinOldRecovery", "sessions", "session-1");
            Directory.CreateDirectory(workspace);
            string database = Path.Combine(workspace, "session.db");
            File.WriteAllText(database, "db");
            string source = Path.Combine(root, "Windows.old");
            Directory.CreateDirectory(source);
            string pointerPath = CompletedScan.PointerPathFromWorkspace(workspace);
            CompletedScan.Write(
                safeFs,
                new CompletedScanPointer(
                    "session-1",
                    workspace,
                    database,
                    source,
                    DateTimeOffset.UnixEpoch,
                    12,
                    4096,
                    1,
                    3),
                pointerPath);

            CompletedScanPointer? pointer = CompletedScan.TryRead(safeFs, pointerPath);
            Assert.NotNull(pointer);
            Assert.Equal("session-1", pointer.SessionId);
            Assert.Equal(12, pointer.NodesVisited);
            Assert.Equal(source, pointer.SourceRoot);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
