using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Core.Scan;

namespace WinOldRecovery.Core.Tests.Scan;

public sealed class PublishedScanProbeTests
{
    [Fact]
    public async Task RunAsync_ScansASmallScaleTreeAndPagesChildren()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinOldRecovery-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Users", "Alice", "Scale", "d0000"));
        await File.WriteAllTextAsync(Path.Combine(root, "Users", "Alice", "Scale", "d0000", "f0000.txt"), "x");
        SourceGuard guard = new();
        SafeFs safeFs = new(guard);
        SessionDb database = await SessionDb.OpenAsync(Path.Combine(root, "session.db"), safeFs);
        try
        {
            await database.CreateSessionAsync(
                new SessionRecord("session-1", DateTimeOffset.UtcNow, "Scanning", "0.1.0"));
            Directory.CreateDirectory(Path.Combine(root, "tmp"));
            string report = await PublishedScanProbe.RunAsync(
                database,
                safeFs,
                guard,
                "session-1",
                root,
                Path.Combine(root, "tmp"));
            Assert.Contains("Passed: true", report, StringComparison.Ordinal);
            Assert.Contains("WalkSeconds:", report, StringComparison.Ordinal);
            Assert.Contains("ClassifySeconds:", report, StringComparison.Ordinal);
            Assert.Contains("TreePageRows:", report, StringComparison.Ordinal);
        }
        finally
        {
            await database.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
