using System.Diagnostics;
using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Core.Scan;

namespace WinOldRecovery.Core.Tests.Scan;

public sealed class FileSystemWalkerTests
{
    [Fact]
    public async Task I11_WalkDoesNotFollowJunctionsAndClassifiesHazards()
    {
        await using WalkerTestContext context = await WalkerTestContext.CreateAsync();
        string live = Path.Combine(context.Root, "live");
        Directory.CreateDirectory(live);
        await File.WriteAllTextAsync(Path.Combine(live, "live-only.txt"), "must never be scanned");

        string source = Path.Combine(context.Root, "Windows.old");
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice"));
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "note.txt"),
            "alice");
        CreateJunction(
            Path.Combine(source, "Users", "Alice", "Application Data"),
            live);
        CreateJunction(
            Path.Combine(source, "Users", "Alice", "Loop"),
            Path.Combine(source, "Users", "Alice"));

        string cloud = Path.Combine(source, "Users", "Alice", "Cloud", "offline-placeholder.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(cloud)!);
        await File.WriteAllTextAsync(cloud, string.Empty);
        File.SetAttributes(cloud, FileAttributes.Offline);

        string invalid = Path.Combine(source, "Users", "Alice", "InvalidNames", "trailing-space. ");
        Directory.CreateDirectory(Path.GetDirectoryName(invalid)!);
        await File.WriteAllTextAsync(PathCanonicalizer.ToExtendedPath(invalid), "invalid");

        DateTime sourceWrite = File.GetLastWriteTimeUtc(
            Path.Combine(source, "Users", "Alice", "note.txt"));

        FileSystemWalker walker = new(context.Database);
        WalkResult result = await walker.WalkAsync(new WalkRequest("session-1", source));

        Dictionary<string, NodeRow> nodes = LoadNodes(context.Database);
        Assert.True(result.Completed);
        Assert.False(nodes.ContainsKey(@"live-only.txt"));
        Assert.DoesNotContain(nodes.Keys, key => key.Contains("live-only", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(NodeKind.Junction, nodes[@"Users\Alice\Application Data"].Kind);
        Assert.Equal(NodeKind.Junction, nodes[@"Users\Alice\Loop"].Kind);
        Assert.Equal(0, CountChildren(context.Database, nodes[@"Users\Alice\Application Data"].Id));
        Assert.Equal(0, CountChildren(context.Database, nodes[@"Users\Alice\Loop"].Id));
        Assert.Equal(NodeKind.CloudPlaceholder, nodes[@"Users\Alice\Cloud\offline-placeholder.txt"].Kind);
        Assert.Equal(NodeProblem.CloudOnly, nodes[@"Users\Alice\Cloud\offline-placeholder.txt"].Problem);
        Assert.Equal(NodeProblem.InvalidDestName, nodes[@"Users\Alice\InvalidNames\trailing-space. "].Problem);
        Assert.Contains(
            LoadBadges(context.Database),
            badge => badge.Contains("Application Data", StringComparison.OrdinalIgnoreCase)
                || badge.Contains("0xA0000003", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            sourceWrite,
            File.GetLastWriteTimeUtc(Path.Combine(source, "Users", "Alice", "note.txt")));
        Assert.True(File.Exists(Path.Combine(live, "live-only.txt")));
        Assert.True(result.JunctionsSkipped >= 2, "Junctions should be counted as skipped leaves.");
        Assert.True(result.CloudSkipped >= 1, "Cloud placeholders should be counted as skipped.");
    }

    [Fact]
    public async Task Walk_ComputesPostOrderAggregates()
    {
        await using WalkerTestContext context = await WalkerTestContext.CreateAsync();
        string source = Path.Combine(context.Root, "tree");
        string nested = Path.Combine(source, "Projects", "app", "node_modules");
        Directory.CreateDirectory(nested);
        for (int index = 0; index < 25; index++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(nested, $"package-{index:D2}.tmp"),
                new string('a', 10));
        }

        FileSystemWalker walker = new(context.Database);
        WalkProgress? last = null;
        await walker.WalkAsync(
            new WalkRequest("session-1", source),
            new SyncProgress(progress => last = progress));

        Dictionary<string, NodeRow> nodes = LoadNodes(context.Database);
        Assert.Equal(25, nodes[@"Projects\app\node_modules"].AggFiles);
        Assert.Equal(250, nodes[@"Projects\app\node_modules"].AggSize);
        Assert.Equal(25, nodes[string.Empty].AggFiles);
        Assert.Equal(250, nodes[string.Empty].AggSize);
        Assert.NotNull(last);
        Assert.Equal(25, last.FilesSeen);
        Assert.Equal(4, last.FoldersSeen);
    }

    [Fact]
    public async Task Walk_SkipsFolderAggregatesWhenDisabled()
    {
        await using WalkerTestContext context = await WalkerTestContext.CreateAsync();
        string source = Path.Combine(context.Root, "tree");
        string nested = Path.Combine(source, "Projects", "app", "node_modules");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "package-00.tmp"), new string('a', 10));

        FileSystemWalker walker = new(context.Database);
        await walker.WalkAsync(new WalkRequest("session-1", source, ComputeFolderSizes: false));

        Dictionary<string, NodeRow> nodes = LoadNodes(context.Database);
        Assert.Equal(0, nodes[@"Projects\app\node_modules"].AggFiles);
        Assert.Equal(0, nodes[@"Projects\app\node_modules"].AggSize);
        Assert.Equal(0, nodes[string.Empty].AggFiles);
        Assert.Contains(nodes.Keys, key => key.EndsWith("package-00.tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Walk_ResumeSkipsCompletedTopLevelDirectories()
    {
        await using WalkerTestContext context = await WalkerTestContext.CreateAsync();
        string source = Path.Combine(context.Root, "source");
        foreach (string name in new[] { "Alpha", "Bravo", "Charlie" })
        {
            Directory.CreateDirectory(Path.Combine(source, name));
            await File.WriteAllTextAsync(Path.Combine(source, name, "file.txt"), name);
        }

        CancellationTokenSource cts = new();
        SyncProgress progress = new(report =>
        {
            if (report.CompletedTopLevelDirectories.Count >= 1)
            {
                cts.Cancel();
            }
        });

        FileSystemWalker walker = new(context.Database);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => walker.WalkAsync(new WalkRequest("session-1", source), progress, cts.Token));

        WalkResult resumed = await walker.WalkAsync(
            new WalkRequest("session-1", source, Resume: true));

        Dictionary<string, NodeRow> nodes = LoadNodes(context.Database);
        Assert.True(resumed.Completed);
        Assert.Equal(3, nodes[string.Empty].AggFiles);
        Assert.Contains(@"Alpha\file.txt", nodes.Keys);
        Assert.Contains(@"Bravo\file.txt", nodes.Keys);
        Assert.Contains(@"Charlie\file.txt", nodes.Keys);
        Assert.Equal(nodes.Count, nodes.Values.Select(node => node.RelPath).Distinct().Count());
    }

    private static Dictionary<string, NodeRow> LoadNodes(SessionDb database)
    {
        using SqliteConnection connection = database.OpenReadConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, rel_path, kind, problem, agg_size, agg_files
            FROM nodes;
            """;
        Dictionary<string, NodeRow> nodes = new(StringComparer.OrdinalIgnoreCase);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            NodeRow row = new(
                reader.GetInt64(0),
                reader.GetString(1),
                Enum.Parse<NodeKind>(reader.GetString(2)),
                Enum.Parse<NodeProblem>(reader.GetString(3)),
                reader.GetInt64(4),
                reader.GetInt64(5));
            nodes[row.RelPath] = row;
        }

        return nodes;
    }

    private static List<string> LoadBadges(SessionDb database)
    {
        using SqliteConnection connection = database.OpenReadConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT kind || ':' || detail FROM badges;";
        List<string> badges = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            badges.Add(reader.GetString(0));
        }

        return badges;
    }

    private static int CountChildren(SessionDb database, long parentId)
    {
        using SqliteConnection connection = database.OpenReadConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM nodes WHERE parent_id = $id;";
        command.Parameters.AddWithValue("$id", parentId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void CreateJunction(string junction, string target)
    {
        Directory.CreateDirectory(target);
        ProcessStartInfo startInfo = new(
            Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junction);
        startInfo.ArgumentList.Add(target);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start mklink.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed record NodeRow(
        long Id,
        string RelPath,
        NodeKind Kind,
        NodeProblem Problem,
        long AggSize,
        long AggFiles);

    private sealed class SyncProgress(Action<WalkProgress> onReport) : IProgress<WalkProgress>
    {
        public void Report(WalkProgress value) => onReport(value);
    }

    private sealed class WalkerTestContext : IAsyncDisposable
    {
        private WalkerTestContext(string root, SessionDb database)
        {
            Root = root;
            Database = database;
        }

        public string Root { get; }

        public SessionDb Database { get; }

        public static async Task<WalkerTestContext> CreateAsync()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                $"WinOldRecovery-Walker-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            SessionDb database = await SessionDb.OpenAsync(
                Path.Combine(root, "session.db"),
                new SafeFs(new SourceGuard()));
            await database.CreateSessionAsync(
                new SessionRecord("session-1", DateTimeOffset.UtcNow, "Scanning", "0.1.0"));
            return new WalkerTestContext(root, database);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            SqliteConnection.ClearAllPools();

            string alice = Path.Combine(Root, "Windows.old", "Users", "Alice");
            foreach (string link in new[]
            {
                Path.Combine(alice, "Application Data"),
                Path.Combine(alice, "Loop"),
            })
            {
                if (Directory.Exists(link))
                {
                    Directory.Delete(link);
                }
            }

            Directory.Delete(Root, recursive: true);
        }
    }
}
