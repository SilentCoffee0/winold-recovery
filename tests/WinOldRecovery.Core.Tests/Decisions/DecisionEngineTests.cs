using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Core.Scan;

namespace WinOldRecovery.Core.Tests.Decisions;

public sealed class DecisionEngineTests
{
    [Fact]
    public async Task EffectiveDecision_PrefersOwnUserThenAncestorUserThenSuggestedDefault()
    {
        await using DecisionTestContext context = await DecisionTestContext.CreateAsync();
        await context.InsertTreeAsync();
        DecisionEngine engine = new(context.Database, "session-1");

        await engine.SetSuggestedDefaultAsync(context.Child, Decision.LeaveBehind);
        Assert.Equal(Decision.LeaveBehind, engine.GetEffectiveDecision(context.Child));
        Assert.Equal(Decision.Undecided, engine.GetEffectiveDecision(context.Grandchild));

        await engine.SetUserDecisionAsync(context.Root, Decision.Restore);
        Assert.Equal(Decision.Restore, engine.GetEffectiveDecision(context.Root));
        Assert.Equal(Decision.Restore, engine.GetEffectiveDecision(context.Child));
        Assert.Equal(Decision.Restore, engine.GetEffectiveDecision(context.Grandchild));

        await engine.SetUserDecisionAsync(context.Child, Decision.LeaveBehind);
        Assert.Equal(Decision.Restore, engine.GetEffectiveDecision(context.Root));
        Assert.Equal(Decision.LeaveBehind, engine.GetEffectiveDecision(context.Child));
        Assert.Equal(Decision.LeaveBehind, engine.GetEffectiveDecision(context.Grandchild));
    }

    [Fact]
    public async Task SubtreeSummary_ReportsMixedChildren()
    {
        await using DecisionTestContext context = await DecisionTestContext.CreateAsync();
        await context.InsertTreeAsync();
        DecisionEngine engine = new(context.Database, "session-1");
        await engine.SetUserDecisionAsync(context.Child, Decision.Restore);
        await engine.SetUserDecisionAsync(context.Sibling, Decision.LeaveBehind);

        SubtreeDecisionSummary summary = engine.GetSubtreeSummary(context.Root);
        Assert.True(summary.Mixed);
        Assert.True(summary.RestoreCount >= 1);
        Assert.True(summary.LeaveBehindCount >= 1);
        Assert.True(summary.UndecidedCount >= 1);
    }

    [Fact]
    public async Task Undo_RestoresPreviousUserDecision()
    {
        await using DecisionTestContext context = await DecisionTestContext.CreateAsync();
        await context.InsertTreeAsync();
        DecisionEngine engine = new(context.Database, "session-1");
        await engine.SetUserDecisionAsync(context.Root, Decision.Restore);
        await engine.SetUserDecisionAsync(context.Root, Decision.LeaveBehind);
        Assert.Equal(Decision.LeaveBehind, engine.GetEffectiveDecision(context.Grandchild));

        await engine.UndoAsync();
        Assert.Equal(Decision.Restore, engine.GetEffectiveDecision(context.Grandchild));

        await engine.UndoAsync();
        Assert.Equal(Decision.Undecided, engine.GetEffectiveDecision(context.Grandchild));
    }

    [Fact]
    public async Task SetSuggestedDefaults_AppliesEachNodeWithoutRewritingUntouchedChildren()
    {
        await using DecisionTestContext context = await DecisionTestContext.CreateAsync();
        await context.InsertTreeAsync();
        DecisionEngine engine = new(context.Database, "session-1");
        await engine.SetSuggestedDefaultsAsync(
            new Dictionary<long, Decision>
            {
                [context.Child] = Decision.LeaveBehind,
                [context.Sibling] = Decision.Restore,
            });

        Assert.Equal(Decision.LeaveBehind, engine.GetEffectiveDecision(context.Child));
        Assert.Equal(Decision.Restore, engine.GetEffectiveDecision(context.Sibling));
        Assert.Equal(Decision.Undecided, engine.GetEffectiveDecision(context.Grandchild));
        Assert.Equal(Decision.Undecided, engine.GetEffectiveDecision(context.Root));

        await engine.SetUserDecisionAsync(context.Root, Decision.Restore);
        Assert.Equal(Decision.Restore, engine.GetEffectiveDecision(context.Child));
        Assert.Equal(Decision.Restore, engine.GetEffectiveDecision(context.Grandchild));
    }

    [Fact]
    public async Task ApplyUserDecisionToMatching_SkipsRegeneratableBadges()
    {
        await using DecisionTestContext context = await DecisionTestContext.CreateAsync();
        await context.InsertTreeAsync();
        await context.Database.InsertBadgesAsync(
        [
            new NodeBadgeRow(context.Child, "Regeneratable", "Regeneratable"),
        ]);
        DecisionEngine engine = new(context.Database, "session-1");
        await engine.ApplyUserDecisionToMatchingAsync(
            context.Child,
            Decision.Restore,
            filesOnly: false,
            excludeRegeneratable: true,
            minMtimeUtc: null);

        Assert.Equal(Decision.Undecided, engine.GetEffectiveDecision(context.Child));
        Assert.Equal(Decision.Restore, engine.GetEffectiveDecision(context.Grandchild));
        Assert.DoesNotContain(
            context.Child,
            engine.ListMatchingSubtreeIds(context.Child, false, true, null));
    }

    private sealed class DecisionTestContext : IAsyncDisposable
    {
        private DecisionTestContext(string root, SessionDb database)
        {
            RootPath = root;
            Database = database;
        }

        public string RootPath { get; }
        public SessionDb Database { get; }
        public long Root { get; private set; } = 1;
        public long Child { get; private set; } = 2;
        public long Grandchild { get; private set; } = 3;
        public long Sibling { get; private set; } = 4;

        public static async Task<DecisionTestContext> CreateAsync()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                $"WinOldRecovery-Decision-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            SessionDb database = await SessionDb.OpenAsync(
                Path.Combine(root, "session.db"),
                new SafeFs(new SourceGuard()));
            await database.CreateSessionAsync(
                new SessionRecord("session-1", DateTimeOffset.UtcNow, "Scanning", "0.1.0"));
            return new DecisionTestContext(root, database);
        }

        public async Task InsertTreeAsync()
        {
            await Database.InsertNodesAsync(
            [
                Node(Root, null, "", "root"),
                Node(Child, Root, "A", "A"),
                Node(Grandchild, Child, @"A\file.txt", "file.txt"),
                Node(Sibling, Root, "B", "B"),
            ]);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(RootPath, recursive: true);
        }

        private static PersistedNode Node(long id, long? parentId, string relPath, string name)
        {
            return new PersistedNode(
                id,
                "session-1",
                ProfileId: null,
                parentId,
                name,
                relPath,
                NodeKind.Directory,
                Size: 0,
                AggSize: 0,
                AggFiles: 0,
                DateTime.UtcNow,
                0,
                NodeProblem.None);
        }
    }
}
