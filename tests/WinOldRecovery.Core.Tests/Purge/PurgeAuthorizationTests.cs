using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Planning;
using WinOldRecovery.Core.Purge;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Core.Verify;

namespace WinOldRecovery.Core.Tests.Purge;

public sealed class PurgeAuthorizationTests
{
    [Fact]
    public async Task EachGateIndividuallyBlocks()
    {
        await using SettledSession settled = await SettledSession.CreateAsync();
        PurgeGateRequest ok = Valid(settled.CanonicalSource);
        Assert.True(Eval(ok, settled).Authorized);
        Assert.NotNull(Eval(ok, settled).Token);

        Assert.Contains("verify", Eval(ok with { VerifyAllOk = false }, settled).BlockedGates);
        Assert.Contains("files-checked", Eval(ok with { FilesChecked = false }, settled).BlockedGates);
        Assert.Contains("undecided", Eval(ok with { UndecidedAcknowledged = false }, settled).BlockedGates);
        Assert.Contains("restore-active", Eval(ok with { RestoreJobActive = true }, settled).BlockedGates);
        Assert.Contains("folder-name", Eval(ok with { TypedFolderName = "wrong" }, settled).BlockedGates);
        Assert.Contains("custom-root",
            Eval(ok with { SourceFolderName = "OldInstall", TypedFolderName = "OldInstall", CustomRootConfirmed = false }, settled)
                .BlockedGates);
        Assert.Null(Eval(ok with { VerifyAllOk = false }, settled).Token);
    }

    [Fact]
    public async Task CallerTrueFlagsCannotMintWithoutSettledStore()
    {
        await using SettledSession unsettled = await SettledSession.CreateAsync(settle: false);
        PurgeGateRequest ok = Valid(unsettled.CanonicalSource);
        PurgeGateResult result = Eval(ok, unsettled);
        Assert.False(result.Authorized);
        Assert.Null(result.Token);
        Assert.Contains("journal", result.BlockedGates);
        Assert.Contains("verify-store", result.BlockedGates);
    }

    [Fact]
    public async Task CustomRootIsAllowedAfterExtraConfirmation()
    {
        await using SettledSession settled = await SettledSession.CreateAsync();
        PurgeGateResult result = Eval(
            Valid(settled.CanonicalSource) with
            {
                SourceFolderName = "Custom.old",
                TypedFolderName = "Custom.old",
                CustomRootConfirmed = true,
            },
            settled);
        Assert.True(result.Authorized);
    }

    private static PurgeGateResult Eval(PurgeGateRequest request, SettledSession session)
    {
        return PurgeAuthorization.Evaluate(request, session.Database, session.SessionId);
    }

    private static PurgeGateRequest Valid(string canonical)
    {
        return new PurgeGateRequest(
            true,
            true,
            true,
            false,
            "Windows.old",
            "Windows.old",
            canonical,
            Environment.ProcessPath,
            Path.GetTempPath(),
            [Path.Combine(Path.GetTempPath(), "Recovered")],
            false);
    }

    private sealed class SettledSession : IAsyncDisposable
    {
        private SettledSession(string root, SessionDb database, string canonicalSource)
        {
            Root = root;
            Database = database;
            CanonicalSource = canonicalSource;
        }

        public string Root { get; }
        public SessionDb Database { get; }
        public string CanonicalSource { get; }
        public string SessionId => "session-1";

        public static async Task<SettledSession> CreateAsync(bool settle = true)
        {
            string root = Path.Combine(Path.GetTempPath(), "WinOldRecovery-PurgeAuth-" + Guid.NewGuid().ToString("N"));
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
                null);
            IReadOnlyList<PlanItem> stored = await database.ReplacePlanItemsAsync("session-1", [item]);
            if (settle)
            {
                await database.AppendJournalAsync(stored[0].Id!.Value, "Completed");
                DateTimeOffset now = DateTimeOffset.UtcNow;
                await database.InsertVerifyResultsAsync(
                [
                    new VerifyResultRow(stored[0].Id!.Value, "report-ok", 0, true, "exists", now),
                    new VerifyResultRow(stored[0].Id!.Value, "report-ok", 1, true, "size-time", now),
                    new VerifyResultRow(stored[0].Id!.Value, "report-ok", 2, true, "hash", now),
                ]);
            }

            return new SettledSession(root, database, PathCanonicalizer.Canonicalize(source));
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
