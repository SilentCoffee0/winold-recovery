using System.Diagnostics;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Registry;
using WinOldRecovery.Core.Safety;

namespace WinOldRecovery.FixtureGen;

public static class BundleSelfTest
{
    public static async Task<BundleSelfTestResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        SafeFs safeFs = new(new SourceGuard());
        string root = Path.Combine(
            Path.GetTempPath(),
            $"WinOldRecovery-BundleSelfTest-{Guid.NewGuid():N}");
        safeFs.CreateDirectory(root);

        try
        {
            string defaultHive = Path.Combine(
                Directory.GetParent(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))?.FullName
                    ?? string.Empty,
                "Default",
                "NTUSER.DAT");
            if (!File.Exists(defaultHive))
            {
                throw new FileNotFoundException(
                    "The clean Default profile hive needed by the bundle self-test was not found.",
                    defaultHive);
            }

            OfflineRegistryHive hive = await OfflineRegistryHive.OpenCopyAsync(
                defaultHive,
                Path.Combine(root, "hive"),
                safeFs,
                cancellationToken);
            bool registryParsed = hive.ContainsKey("Software");
            if (!registryParsed)
            {
                throw new InvalidDataException(
                    "The Registry package opened the hive but did not find its Software key.");
            }

            await using SessionDb database = await SessionDb.OpenAsync(
                Path.Combine(root, "session.db"),
                safeFs,
                cancellationToken);
            await database.CreateSessionAsync(
                new SessionRecord(
                    "bundle-self-test",
                    DateTimeOffset.UtcNow,
                    "Test",
                    "0.1.0"),
                cancellationToken);

            using Microsoft.Data.Sqlite.SqliteConnection reader =
                database.OpenReadConnection();
            await using Microsoft.Data.Sqlite.SqliteCommand command =
                reader.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM sessions WHERE id = 'bundle-self-test';";
            long sessionCount = (long)(await command
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("SQLite returned no row count."));
            if (sessionCount != 1)
            {
                throw new InvalidDataException(
                    "SQLite loaded but did not round-trip the bundle self-test row.");
            }

            stopwatch.Stop();
            return new BundleSelfTestResult(
                RegistryParsed: true,
                SqliteLoaded: true,
                ElapsedMilliseconds: stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            safeFs.DeleteDirectory(root, recursive: true);
        }
    }
}

public sealed record BundleSelfTestResult(
    bool RegistryParsed,
    bool SqliteLoaded,
    long ElapsedMilliseconds);
