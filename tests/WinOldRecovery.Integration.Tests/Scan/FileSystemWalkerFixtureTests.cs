using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Processes;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Core.Scan;
using WinOldRecovery.FixtureGen;
using WinOldRecovery.Integration.Tests.Safety;

namespace WinOldRecovery.Integration.Tests.Scan;

public sealed class FileSystemWalkerFixtureTests
{
    [Fact]
    public async Task Walk_PortableFixture_ClassifiesHazardsAndLeavesLiveProfileUntouched()
    {
        string testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WinOldRecovery-WalkerFixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        string fixtureRoot = Path.Combine(testRoot, "Windows.old");
        string liveProfile = Path.Combine(testRoot, "Windows.old-live-profile");

        try
        {
            FixtureGenerator generator = new(new ProcessRunner());
            FixtureManifest manifest = await generator.GenerateAsync(
                new FixtureOptions(
                    fixtureRoot,
                    NodeModulesFileCount: 48,
                    PortableMode: true));

            DateTime liveWrite = File.GetLastWriteTimeUtc(Path.Combine(liveProfile, "live-only.txt"));
            string notePath = Path.Combine(fixtureRoot, "Users", "Alice", "Desktop", "Alice-document.txt");
            DateTime sourceWrite = File.GetLastWriteTimeUtc(notePath);

            SourceGuard guard = new();
            guard.RegisterSourceRoot(fixtureRoot);
            SafeFs safeFs = new(guard);
            SessionDb database = await SessionDb.OpenAsync(
                Path.Combine(testRoot, "session.db"),
                safeFs);
            try
            {
                await database.CreateSessionAsync(
                    new SessionRecord(
                        "session-1",
                        DateTimeOffset.UtcNow,
                        "Scanning",
                        "0.1.0",
                        fixtureRoot));

                FileSystemWalker walker = new(database);
                using SourceWatchdog watchdog = new(fixtureRoot);
                WalkResult result = await walker.WalkAsync(new WalkRequest("session-1", fixtureRoot));

                using SqliteConnection reader = database.OpenReadConnection();
                Dictionary<string, (string Kind, string Problem, long AggFiles)> nodes = [];
                await using (SqliteCommand command = reader.CreateCommand())
                {
                    command.CommandText = "SELECT rel_path, kind, problem, agg_files FROM nodes;";
                    await using SqliteDataReader rows = await command.ExecuteReaderAsync();
                    while (await rows.ReadAsync())
                    {
                        nodes[rows.GetString(0)] = (
                            rows.GetString(1),
                            rows.GetString(2),
                            rows.GetInt64(3));
                    }
                }

                Assert.True(result.Completed);
                Assert.DoesNotContain(
                    nodes.Keys,
                    path => path.Contains("live-only", StringComparison.OrdinalIgnoreCase));
                Assert.Equal("Junction", nodes[@"Users\Alice\Application Data"].Kind);
                Assert.Equal("Junction", nodes[@"Users\Alice\Loop"].Kind);
                if (manifest.Hazards["directory-symlink"].Status == "Created")
                {
                    Assert.Equal("Symlink", nodes[@"Users\Alice\DirectorySymlink"].Kind);
                }

                if (manifest.Hazards["file-symlink"].Status == "Created")
                {
                    Assert.Equal("Symlink", nodes[@"Users\Alice\file-link.txt"].Kind);
                }
                Assert.Equal("CloudPlaceholder", nodes[@"Users\Alice\Cloud\offline-placeholder.txt"].Kind);
                Assert.Equal("CloudOnly", nodes[@"Users\Alice\Cloud\offline-placeholder.txt"].Problem);
                Assert.Equal("InvalidDestName", nodes[@"Users\Alice\InvalidNames\trailing-space. "].Problem);
                Assert.Contains(nodes.Values, node => node.Problem == "LongPath");
                Assert.Equal(48, nodes[@"Users\Alice\Projects\fixture-app\node_modules"].AggFiles);
                Assert.Equal("Created", manifest.Hazards["legacy-junction"].Status);
                Assert.Equal(liveWrite, File.GetLastWriteTimeUtc(Path.Combine(liveProfile, "live-only.txt")));
                Assert.Equal(sourceWrite, File.GetLastWriteTimeUtc(notePath));
                watchdog.ThrowIfSourceChanged();
            }
            finally
            {
                await database.DisposeAsync();
                SqliteConnection.ClearAllPools();
            }
        }
        finally
        {
            DeleteTestFixture(testRoot);
        }
    }

    private static void DeleteTestFixture(string testRoot)
    {
        string alice = Path.Combine(testRoot, "Windows.old", "Users", "Alice");
        foreach (string link in new[]
        {
            Path.Combine(alice, "Application Data"),
            Path.Combine(alice, "Loop"),
            Path.Combine(alice, "DirectorySymlink"),
        })
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }

        string fileLink = Path.Combine(alice, "file-link.txt");
        if (File.Exists(fileLink))
        {
            File.Delete(fileLink);
        }

        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
}
