using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Planning;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Core.Scan;
using WinOldRecovery.Native;

namespace WinOldRecovery.Core.Tests.Planning;

public sealed class PlanBuilderTests
{
    [Fact]
    public async Task I4_BuildNeverPlansWholeAppData()
    {
        await using PlanContext context = await PlanContext.CreateAsync();
        await context.Database.InsertNodesAsync(
        [
            Node(1, null, "", "root", NodeKind.Directory),
            Node(2, 1, @"Users\Alice", "Alice", NodeKind.Directory),
            Node(3, 2, @"Users\Alice\AppData", "AppData", NodeKind.Directory),
            Node(4, 3, @"Users\Alice\AppData\Local", "Local", NodeKind.Directory),
            Node(5, 3, @"Users\Alice\AppData\Roaming", "Roaming", NodeKind.Directory),
            Node(6, 4, @"Users\Alice\AppData\Local\MyApp", "MyApp", NodeKind.Directory, aggSize: 5),
            Node(7, 6, @"Users\Alice\AppData\Local\MyApp\settings.json", "settings.json", NodeKind.File, size: 5),
        ]);
        DecisionEngine engine = new(context.Database, context.SessionId);
        await engine.SetUserDecisionAsync(3, Decision.Restore);

        RestorePlan plan = await new PlanBuilder(context.Database).BuildAsync(
            new PlanRequest(context.SessionId, context.Source, context.Destination));

        Assert.DoesNotContain(
            plan.Items,
            item => item.SourcePath.EndsWith(@"Users\Alice\AppData", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            plan.Items,
            item => item.SourcePath.EndsWith(@"Users\Alice\AppData\Local", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            plan.Items,
            item => item.SourcePath.EndsWith(@"Users\Alice\AppData\Roaming", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            plan.Items,
            item => item.SourcePath.EndsWith(@"Users\Alice\AppData\Local\MyApp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Build_EmitsCopyTreeForUniformRestoreAndSkipsWholeAppData()
    {
        await using PlanContext context = await PlanContext.CreateAsync();
        await context.Database.InsertNodesAsync(
        [
            Node(1, null, "", "root", NodeKind.Directory),
            Node(2, 1, @"Users\Alice", "Alice", NodeKind.Directory),
            Node(3, 2, @"Users\Alice\Desktop", "Desktop", NodeKind.Directory, aggSize: 40),
            Node(4, 3, @"Users\Alice\Desktop\a.txt", "a.txt", NodeKind.File, size: 20),
            Node(5, 3, @"Users\Alice\Desktop\b.txt", "b.txt", NodeKind.File, size: 20),
            Node(6, 2, @"Users\Alice\AppData", "AppData", NodeKind.Directory),
            Node(7, 6, @"Users\Alice\AppData\Local", "Local", NodeKind.Directory),
            Node(8, 7, @"Users\Alice\AppData\Local\MyApp", "MyApp", NodeKind.Directory, aggSize: 5),
            Node(9, 8, @"Users\Alice\AppData\Local\MyApp\settings.json", "settings.json", NodeKind.File, size: 5),
            Node(10, 2, @"Users\Alice\Mixed", "Mixed", NodeKind.Directory),
            Node(11, 10, @"Users\Alice\Mixed\keep.txt", "keep.txt", NodeKind.File, size: 3),
            Node(12, 10, @"Users\Alice\Mixed\skip.txt", "skip.txt", NodeKind.File, size: 3),
        ]);
        DecisionEngine engine = new(context.Database, context.SessionId);
        await engine.SetUserDecisionAsync(3, Decision.Restore);
        await engine.SetUserDecisionAsync(6, Decision.Restore);
        await engine.SetUserDecisionAsync(10, Decision.Restore);
        await engine.SetUserDecisionAsync(12, Decision.LeaveBehind);

        RestorePlan plan = await new PlanBuilder(context.Database).BuildAsync(
            new PlanRequest(context.SessionId, context.Source, context.Destination));

        Assert.Contains(plan.Items, item => item.Operation == PlanOperation.CopyTree &&
            item.SourcePath.EndsWith(@"Users\Alice\Desktop", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Items, item => item.SourcePath.EndsWith(@"Users\Alice\AppData", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Items, item => item.SourcePath.EndsWith(@"Users\Alice\AppData\Local", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Items, item => item.Operation == PlanOperation.CopyTree &&
            item.SourcePath.EndsWith(@"Users\Alice\AppData\Local\MyApp", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Items, item => item.Operation == PlanOperation.CopyFile &&
            item.SourcePath.EndsWith(@"keep.txt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Items, item => item.SourcePath.EndsWith(@"skip.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Build_UsesLongestDestinationOverrideForSubtree()
    {
        await using PlanContext context = await PlanContext.CreateAsync();
        await context.Database.InsertNodesAsync(
        [
            Node(1, null, "", "root", NodeKind.Directory),
            Node(2, 1, @"Users\Alice", "Alice", NodeKind.Directory),
            Node(3, 2, @"Users\Alice\Desktop", "Desktop", NodeKind.Directory, aggSize: 20),
            Node(4, 3, @"Users\Alice\Desktop\a.txt", "a.txt", NodeKind.File, size: 20),
        ]);
        DecisionEngine engine = new(context.Database, context.SessionId);
        await engine.SetUserDecisionAsync(3, Decision.Restore);
        string overrideDest = Path.Combine(context.Root, "DeskOut");
        Directory.CreateDirectory(overrideDest);

        RestorePlan plan = await new PlanBuilder(context.Database).BuildAsync(
            new PlanRequest(
                context.SessionId,
                context.Source,
                context.Destination,
                DestinationByRelPath: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [@"Users\Alice"] = Path.Combine(context.Root, "ProfileOut"),
                    [@"Users\Alice\Desktop"] = overrideDest,
                }));

        PlanItem item = Assert.Single(plan.Items);
        Assert.Equal(PlanOperation.CopyTree, item.Operation);
        Assert.Equal(
            PathCanonicalizer.Canonicalize(overrideDest),
            item.DestinationPath,
            ignoreCase: true);
    }

    [Fact]
    public async Task I13_BuildRejectsDestinationInsideSource()
    {
        await using PlanContext context = await PlanContext.CreateAsync();
        await context.Database.InsertNodesAsync(
        [
            Node(1, null, "", "root", NodeKind.Directory),
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PlanBuilder(context.Database).BuildAsync(
                new PlanRequest(
                    context.SessionId,
                    context.Source,
                    Path.Combine(context.Source, "inside"))));
    }

    [Fact]
    public async Task I13_BuildRejectsSourceInsideDestination()
    {
        await using PlanContext context = await PlanContext.CreateAsync();
        await context.Database.InsertNodesAsync(
        [
            Node(1, null, "", "root", NodeKind.Directory),
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PlanBuilder(context.Database).BuildAsync(
                new PlanRequest(
                    context.SessionId,
                    context.Source,
                    context.Root)));
    }

    [Fact]
    public void GetFreeBytes_ReadsCallerQuota()
    {
        long free = DiskSpace.GetFreeBytes(Path.GetTempPath());
        Assert.True(free > 0);
    }

    private static PersistedNode Node(
        long id,
        long? parentId,
        string relPath,
        string name,
        NodeKind kind,
        long size = 0,
        long aggSize = 0)
    {
        return new PersistedNode(
            id,
            "session-1",
            null,
            parentId,
            name,
            relPath,
            kind,
            size,
            aggSize,
            kind == NodeKind.File ? 1 : 0,
            DateTime.UtcNow,
            0,
            NodeProblem.None);
    }

    private sealed class PlanContext : IAsyncDisposable
    {
        private PlanContext(string root, SessionDb database)
        {
            Root = root;
            Database = database;
            Source = Path.Combine(root, "Windows.old");
            Destination = Path.Combine(root, "Recovered");
            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(Destination);
        }

        public string Root { get; }
        public SessionDb Database { get; }
        public string Source { get; }
        public string Destination { get; }
        public string SessionId => "session-1";

        public static async Task<PlanContext> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), $"WinOldRecovery-Plan-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            SessionDb database = await SessionDb.OpenAsync(
                Path.Combine(root, "session.db"),
                new SafeFs(new SourceGuard()));
            await database.CreateSessionAsync(
                new SessionRecord("session-1", DateTimeOffset.UtcNow, "Deciding", "0.1.0"));
            return new PlanContext(root, database);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }
}
