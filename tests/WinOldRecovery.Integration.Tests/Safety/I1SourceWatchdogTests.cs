using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Core.Scan;
using WinOldRecovery.Integration.Tests.Safety;

namespace WinOldRecovery.Integration.Tests.Safety;

public sealed class I1SourceWatchdogTests
{
    [Fact]
    public async Task I1_SourceIsNeverWritten()
    {
        string root = Path.Combine(Path.GetTempPath(), $"WinOldRecovery-I1-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "Windows.old");
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice"));
        await File.WriteAllTextAsync(Path.Combine(source, "Users", "Alice", "note.txt"), "keep");
        DateTime stamp = File.GetLastWriteTimeUtc(Path.Combine(source, "Users", "Alice", "note.txt"));

        SourceGuard guard = new();
        guard.RegisterSourceRoot(source);
        SafeFs safeFs = new(guard);
        SessionDb database = await SessionDb.OpenAsync(Path.Combine(root, "session.db"), safeFs);
        try
        {
            await database.CreateSessionAsync(
                new SessionRecord("session-1", DateTimeOffset.UtcNow, "Scanning", "0.1.0", source));
            using SourceWatchdog watchdog = new(source);
            WalkResult result = await new FileSystemWalker(database).WalkAsync(
                new WalkRequest("session-1", source));
            Assert.True(result.Completed);
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(source, "Users", "Alice", "note.txt")));
            watchdog.ThrowIfSourceChanged();
        }
        finally
        {
            await database.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
