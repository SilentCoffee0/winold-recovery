using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Core.Scan;

namespace WinOldRecovery.Core.Tests.Scan;

[Collection("Scale")]
public sealed class FileSystemWalkerScaleTests
{
    private const int DirectoryCount = 1000;
    private const int FilesPerDirectory = 1000;
    private const int FileCount = DirectoryCount * FilesPerDirectory;
    private const int ExpectedNodes = FileCount + DirectoryCount + 1;
    private const long MemoryCeilingBytes = 1_500L * 1024 * 1024;

    [SkippableFact(Timeout = 900_000)]
    public async Task OneMillionOnDiskEntries_ScanUnderSixtySecondsAndStayUnderMemoryCeiling()
    {
        Skip.If(
            string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase)
            && Environment.GetEnvironmentVariable("WOR_SCALE_1M") != "1",
            "Creating and deleting 1,000,000 on-disk files exceeds windows-latest. Published --scan covers 8.2; set WOR_SCALE_1M=1 to force.");

        await using ScaleContext context = await ScaleContext.CreateAsync();
        string source = Path.Combine(context.Root, "Windows.old");
        CreateMillionFileTree(source);

        long peakWorkingSet = Process.GetCurrentProcess().WorkingSet64;
        FileSystemWalker walker = new(context.Database);
        Stopwatch walk = Stopwatch.StartNew();
        WalkResult result = await walker.WalkAsync(
            new WalkRequest("session-1", source),
            new SyncProgress(progress =>
            {
                long workingSet = Process.GetCurrentProcess().WorkingSet64;
                if (workingSet > peakWorkingSet)
                {
                    peakWorkingSet = workingSet;
                }
            }));
        walk.Stop();
        peakWorkingSet = Math.Max(peakWorkingSet, Process.GetCurrentProcess().WorkingSet64);

        Assert.True(result.Completed);
        Assert.Equal(ExpectedNodes, result.NodesVisited);
        Assert.True(
            walk.Elapsed < TimeSpan.FromSeconds(60),
            "Scanning 1M on-disk entries took " +
            walk.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) +
            " s.");
        Assert.True(
            peakWorkingSet < MemoryCeilingBytes,
            "Working set " + peakWorkingSet + " exceeded 1.5 GB while scanning 1M on-disk entries.");
    }

    private static void CreateMillionFileTree(string source)
    {
        Directory.CreateDirectory(source);
        Parallel.For(
            0,
            DirectoryCount,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            directory =>
            {
                string folder = Path.Combine(
                    source,
                    "d" + directory.ToString("D4", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(folder);
                for (int file = 0; file < FilesPerDirectory; file++)
                {
                    string path = Path.Combine(
                        folder,
                        "f" + file.ToString("D4", CultureInfo.InvariantCulture) + ".txt");
                    File.Create(path).Dispose();
                }
            });
    }

    private sealed class SyncProgress(Action<WalkProgress> onReport) : IProgress<WalkProgress>
    {
        public void Report(WalkProgress value) => onReport(value);
    }

    private sealed class ScaleContext : IAsyncDisposable
    {
        private ScaleContext(string root, SessionDb database)
        {
            Root = root;
            Database = database;
        }

        public string Root { get; }

        public SessionDb Database { get; }

        public static async Task<ScaleContext> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), $"WinOldRecovery-WalkScale-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            SessionDb database = await SessionDb.OpenAsync(
                Path.Combine(root, "session.db"),
                new SafeFs(new SourceGuard()));
            await database.CreateSessionAsync(
                new SessionRecord("session-1", DateTimeOffset.UtcNow, "Scanning", "0.1.0"));
            return new ScaleContext(root, database);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
