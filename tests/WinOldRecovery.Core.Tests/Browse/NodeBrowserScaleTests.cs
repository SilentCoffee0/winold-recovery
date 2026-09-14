using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.Browse;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Core.Scan;

namespace WinOldRecovery.Core.Tests.Browse;

[Collection("Scale")]
public sealed class NodeBrowserScaleTests
{
    private const int Million = 1_000_000;
    private const int HundredThousand = 100_000;
    private const long MemoryCeilingBytes = 1_500L * 1024 * 1024;

    [Fact(Timeout = 600_000)]
    public async Task OneMillionSyntheticEntries_StayUnderTheMemoryCeiling()
    {
        await using ScaleContext context = await ScaleContext.CreateAsync();
        await InsertWideTreeAsync(context.Database, Million);

        NodeBrowser browser = new(context.Database, "session-1");
        NodePage page = browser.GetChildren(1);
        Assert.Equal(NodeBrowser.ChildPageSize, page.Rows.Count);
        Assert.Equal(Million, page.TotalCount);
        Assert.True(page.Truncated);
        Assert.All(page.Rows, static row => Assert.Equal(0, row.ChildCount));

        long workingSet = Process.GetCurrentProcess().WorkingSet64;
        Assert.True(
            workingSet < MemoryCeilingBytes,
            "Working set " + workingSet + " exceeded 1.5 GB after paging 1M synthetic nodes.");
    }

    [Fact(Timeout = 120_000)]
    public async Task ExpandingAHundredThousandChildNode_ReturnsTheFirstPageUnder300Ms()
    {
        await using ScaleContext context = await ScaleContext.CreateAsync();
        await InsertWideTreeAsync(context.Database, HundredThousand);
        NodeBrowser browser = new(context.Database, "session-1");
        browser.GetChildren(1);

        Stopwatch expand = Stopwatch.StartNew();
        NodePage page = browser.GetChildren(1);
        expand.Stop();

        Assert.Equal(NodeBrowser.ChildPageSize, page.Rows.Count);
        Assert.Equal(HundredThousand, page.TotalCount);
        Assert.True(page.Truncated);
        Assert.True(
            expand.Elapsed < TimeSpan.FromMilliseconds(300),
            "Expanding a 100k-child node took " + expand.ElapsedMilliseconds + " ms.");
    }

    private static async Task InsertWideTreeAsync(SessionDb database, int childCount)
    {
        await database.InsertNodesAsync(
            [
                Node(1, null, "", "root", NodeKind.Directory),
            ]);

        const int batch = 20_000;
        List<PersistedNode> nodes = new(batch);
        for (int i = 0; i < childCount; i++)
        {
            long id = i + 2;
            string name = "f" + i.ToString("D7", CultureInfo.InvariantCulture) + ".txt";
            nodes.Add(Node(id, 1, name, name, NodeKind.File));
            if (nodes.Count == batch)
            {
                await database.InsertNodesAsync(nodes);
                nodes.Clear();
            }
        }

        if (nodes.Count > 0)
        {
            await database.InsertNodesAsync(nodes);
        }
    }

    private static PersistedNode Node(
        long id,
        long? parentId,
        string relPath,
        string name,
        NodeKind kind)
    {
        return new PersistedNode(
            id,
            "session-1",
            null,
            parentId,
            name,
            relPath,
            kind,
            1,
            1,
            1,
            DateTime.UtcNow,
            0,
            NodeProblem.None);
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
            string root = Path.Combine(Path.GetTempPath(), $"WinOldRecovery-Scale-{Guid.NewGuid():N}");
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
            Directory.Delete(Root, recursive: true);
        }
    }
}

[CollectionDefinition("Scale", DisableParallelization = true)]
public sealed class ScaleCollection;
