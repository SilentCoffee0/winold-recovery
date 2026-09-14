using System.Globalization;
using System.Linq;
using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.Browse;
using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Core.Scan;

namespace WinOldRecovery.Core.Tests.Browse;

public sealed class NodeBrowserTests
{
    [Fact]
    public async Task GetChildren_PagesAndLabelsInheritedDecisions()
    {
        await using BrowserContext context = await BrowserContext.CreateAsync();
        await context.InsertAsync(
        [
            Node(1, null, "", "root"),
            Node(2, 1, "A", "A"),
            Node(3, 2, @"A\one.txt", "one.txt"),
            Node(4, 2, @"A\two.txt", "two.txt"),
        ]);
        DecisionEngine engine = new(context.Database, "session-1");
        await engine.SetUserDecisionAsync(2, Decision.Restore);

        NodeBrowser browser = new(context.Database, "session-1");
        NodePage roots = browser.GetChildren(null);
        NodePage children = browser.GetChildren(2);

        Assert.Equal("root", Assert.Single(roots.Rows).Name);
        Assert.Equal(2, children.TotalCount);
        Assert.All(children.Rows, row => Assert.Equal("Restore (inherited)", row.DecisionLabel));
        Assert.False(children.Truncated);
    }

    [Fact]
    public async Task GetProblems_ReturnsOnlyProblemNodes()
    {
        await using BrowserContext context = await BrowserContext.CreateAsync();
        await context.InsertAsync(
        [
            Node(1, null, "", "root"),
            Node(2, 1, "ok.txt", "ok.txt"),
            Node(3, 1, "cloud.txt", "cloud.txt", NodeProblem.CloudOnly),
        ]);

        NodePage problems = new NodeBrowser(context.Database, "session-1").GetProblems();
        TreeNodeRow row = Assert.Single(problems.Rows);
        Assert.Equal("cloud.txt", row.Name);
        Assert.Equal(NodeProblem.CloudOnly, row.Problem);
    }

    [Fact]
    public async Task Search_FindsSubstringMatches()
    {
        await using BrowserContext context = await BrowserContext.CreateAsync();
        await context.InsertAsync(
        [
            Node(1, null, "", "root"),
            Node(2, 1, "notes.kdbx", "notes.kdbx"),
            Node(3, 1, "readme.txt", "readme.txt"),
        ]);

        NodePage page = new NodeBrowser(context.Database, "session-1").Search("kdbx", null);
        Assert.Equal("notes.kdbx", Assert.Single(page.Rows).Name);
    }

    [Fact]
    public async Task FindByRelPath_ReturnsExactNode()
    {
        await using BrowserContext context = await BrowserContext.CreateAsync();
        await context.InsertAsync(
        [
            Node(1, null, "", "root"),
            Node(2, 1, @"Users\Alice\Desktop", "Desktop"),
        ]);

        TreeNodeRow? found = new NodeBrowser(context.Database, "session-1")
            .FindByRelPath(@"Users\Alice\Desktop");
        Assert.NotNull(found);
        Assert.Equal("Desktop", found.Name);
    }

    [Fact]
    public async Task GetChildren_TruncatesAtThePageSize()
    {
        await using BrowserContext context = await BrowserContext.CreateAsync();
        List<PersistedNode> nodes = [Node(1, null, "", "root")];
        for (int i = 0; i < NodeBrowser.ChildPageSize + 1; i++)
        {
            long id = i + 2;
            string name = "f" + i.ToString("D4", CultureInfo.InvariantCulture) + ".txt";
            nodes.Add(Node(id, 1, name, name));
        }

        await context.InsertAsync(nodes);
        NodePage page = new NodeBrowser(context.Database, "session-1").GetChildren(1);
        Assert.Equal(NodeBrowser.ChildPageSize, page.Rows.Count);
        Assert.Equal(NodeBrowser.ChildPageSize + 1, page.TotalCount);
        Assert.True(page.Truncated);
    }

    [Fact]
    public async Task GetChildren_SecondPageStartsAfterTheLimit()
    {
        await using BrowserContext context = await BrowserContext.CreateAsync();
        List<PersistedNode> nodes = [Node(1, null, "", "root")];
        for (int i = 0; i < NodeBrowser.ChildPageSize + 1; i++)
        {
            long id = i + 2;
            string name = "f" + i.ToString("D4", CultureInfo.InvariantCulture) + ".txt";
            nodes.Add(Node(id, 1, name, name));
        }

        await context.InsertAsync(nodes);
        NodeBrowser browser = new(context.Database, "session-1");
        NodePage second = browser.GetChildren(1, offset: NodeBrowser.ChildPageSize);
        Assert.Single(second.Rows);
        Assert.Equal("f2000.txt", second.Rows[0].Name);
        Assert.False(second.Truncated);

        TreeNodeRow? leaf = browser.GetNode(3);
        Assert.NotNull(leaf);
        IReadOnlyList<TreeNodeRow> ancestors = browser.GetAncestors(leaf.Id);
        Assert.Contains(ancestors, row => row.Id == 1);
    }

    private static PersistedNode Node(
        long id,
        long? parentId,
        string relPath,
        string name,
        NodeProblem problem = NodeProblem.None)
    {
        return new PersistedNode(
            id,
            "session-1",
            null,
            parentId,
            name,
            relPath,
            name.Contains('.', StringComparison.Ordinal) ? NodeKind.File : NodeKind.Directory,
            1,
            1,
            1,
            DateTime.UtcNow,
            0,
            problem);
    }

    private sealed class BrowserContext : IAsyncDisposable
    {
        private BrowserContext(string root, SessionDb database)
        {
            Root = root;
            Database = database;
        }

        public string Root { get; }
        public SessionDb Database { get; }

        public static async Task<BrowserContext> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), $"WinOldRecovery-Browse-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            SessionDb database = await SessionDb.OpenAsync(
                Path.Combine(root, "session.db"),
                new SafeFs(new SourceGuard()));
            await database.CreateSessionAsync(
                new SessionRecord("session-1", DateTimeOffset.UtcNow, "Scanning", "0.1.0"));
            return new BrowserContext(root, database);
        }

        public Task InsertAsync(IReadOnlyList<PersistedNode> nodes)
        {
            return Database.InsertNodesAsync(nodes);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }
}
