using System.IO;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Restore;
using WinOldRecovery.Core.Safety;

namespace WinOldRecovery.Core.Tests.Restore;

public sealed class PublishedRestoreProbeTests
{
    [Fact]
    public async Task RunAsync_CopiesASmallTreeAndReportsPassed()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinOldRecovery-restore-probe-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "Windows.old", "Desktop");
        string dest = Path.Combine(root, "Recovered", "Desktop");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "note.txt"), "hello");
        SourceGuard guard = new();
        SafeFs safeFs = new(guard);
        SessionDb database = await SessionDb.OpenAsync(Path.Combine(root, "session.db"), safeFs);
        try
        {
            await database.CreateSessionAsync(
                new SessionRecord("session-1", DateTimeOffset.UtcNow, "Restoring", "0.1.0"));
            string report = await PublishedRestoreProbe.RunAsync(
                database,
                safeFs,
                guard,
                "session-1",
                source,
                dest);
            Assert.Contains("Passed: true", report, StringComparison.Ordinal);
            Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(dest, "note.txt")));

            string again = await PublishedRestoreProbe.RunAsync(
                database,
                safeFs,
                guard,
                "session-1",
                source,
                dest);
            Assert.Contains("Passed: true", again, StringComparison.Ordinal);
            Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(dest, "note.txt")));
        }
        finally
        {
            await database.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
