using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Planning;
using WinOldRecovery.Core.Processes;
using WinOldRecovery.Core.Purge;
using WinOldRecovery.Core.Recipes;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Core.Verify;

namespace WinOldRecovery.Core.Tests.Recipes;

public sealed class RecipeLevel3Tests
{
    [Fact]
    public async Task I9_Level3FailureFailsVerifyStoreAndBlocksPurge()
    {
        await using Level3Context context = await Level3Context.CreateAsync();
        RecipeHost host = new(
            context.Database,
            context.SafeFs,
            new SilentRunner(),
            [new StubRecipe(verifyOk: false)]);
        RecipeCard card = StubRecipe.Card();
        IReadOnlyList<VerifyResultRow> level3 = await host.CollectLevel3Async(
            "session-1",
            "report-ok",
            [card],
            new DestinationContext(context.Root, context.Root, context.SafeFs, new SilentRunner()));
        Assert.Contains(level3, row => row.Level == 3 && !row.Ok);
        await context.Database.InsertVerifyResultsAsync(level3);

        Assert.False(context.Database.LastVerifyReportAllOk("session-1"));
        PurgeGateResult gated = PurgeAuthorization.Evaluate(
            new PurgeGateRequest(
                true,
                true,
                true,
                false,
                "Windows.old",
                "Windows.old",
                context.CanonicalSource,
                Environment.ProcessPath,
                Path.GetTempPath(),
                [Path.Combine(Path.GetTempPath(), "Recovered")],
                false),
            context.Database,
            "session-1");
        Assert.Null(gated.Token);
        Assert.Contains("verify-store", gated.BlockedGates);
    }

    private sealed class StubRecipe(bool verifyOk) : IRecipe
    {
        public string Id => "stub";

        public DetectResult Detect(ProfileContext context) => new([], []);

        public PlanResult Plan(CardDecisions decisions, DestinationContext destination) =>
            new(decisions.Card, []);

        public Task ExecuteAsync(
            PlanResult plan,
            IRecipeJournal journal,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public RecipeVerifyResult Verify(PlanResult plan) =>
            new(verifyOk, verifyOk ? "stub ok" : "stub L3 fail");

        public IReadOnlyList<Prerequisite> Prerequisites(PlanResult plan) => [];

        public static RecipeCard Card()
        {
            return new RecipeCard(
                "stub",
                "Stub",
                "what",
                "why",
                "restore",
                "cloud",
                "regen",
                "leave",
                [],
                "instance",
                new Dictionary<string, string>());
        }
    }

    private sealed class SilentRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            ProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class Level3Context : IAsyncDisposable
    {
        private Level3Context(string root, SessionDb database, SafeFs safeFs, string canonicalSource)
        {
            Root = root;
            Database = database;
            SafeFs = safeFs;
            CanonicalSource = canonicalSource;
        }

        public string Root { get; }
        public SessionDb Database { get; }
        public SafeFs SafeFs { get; }
        public string CanonicalSource { get; }

        public static async Task<Level3Context> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "WinOldRecovery-L3-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string source = Path.Combine(root, "Windows.old");
            Directory.CreateDirectory(source);
            SafeFs safeFs = new(new SourceGuard());
            SessionDb database = await SessionDb.OpenAsync(Path.Combine(root, "session.db"), safeFs);
            await database.CreateSessionAsync(
                new SessionRecord("session-1", DateTimeOffset.UtcNow, "Verifying", "0.1.0"));
            PlanItem item = new(
                "session-1",
                1,
                PlanOperation.CopyFile,
                Path.Combine(source, "a.txt"),
                Path.Combine(root, "dest.txt"),
                1,
                ConflictPolicy.KeepBoth,
                false,
                "stub");
            IReadOnlyList<PlanItem> stored = await database.ReplacePlanItemsAsync("session-1", [item]);
            await database.AppendJournalAsync(stored[0].Id!.Value, "Completed");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            await database.InsertVerifyResultsAsync(
            [
                new VerifyResultRow(stored[0].Id!.Value, "report-ok", 0, true, "exists", now),
                new VerifyResultRow(stored[0].Id!.Value, "report-ok", 1, true, "size-time", now),
                new VerifyResultRow(stored[0].Id!.Value, "report-ok", 2, true, "hash", now),
            ]);
            return new Level3Context(root, database, safeFs, PathCanonicalizer.Canonicalize(source));
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
