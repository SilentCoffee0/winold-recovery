using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Planning;
using WinOldRecovery.Core.Restore;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Core.Sessions;

namespace WinOldRecovery.Core.Tests.Restore;

public sealed class InterruptedRestoreTests
{
    [Fact]
    public async Task Describe_IgnoresPreviewPlansThatNeverStarted()
    {
        await using SessionContext context = await SessionContext.CreateAsync();
        await context.StoreAsync("a.txt");

        Assert.Null(InterruptedRestore.Describe(context.Database, context.SessionId, context.Workspace.RootPath));
    }

    [Fact]
    public async Task Describe_CountsUnstartedItemsOnceAnyCopyHasStarted()
    {
        await using SessionContext context = await SessionContext.CreateAsync();
        PlanItem first = await context.StoreAsync("done.txt");
        PlanItem second = await context.StoreAsync("partial.txt");
        await context.StoreAsync("queued.txt");
        await context.Database.AppendJournalAsync(first.Id!.Value, "Completed");
        await context.Database.AppendJournalAsync(second.Id!.Value, "Started");
        await context.Database.SetKvAsync(
            context.SessionId,
            InterruptedRestore.SourceRootKey,
            context.Source);
        await context.Database.SetKvAsync(
            context.SessionId,
            InterruptedRestore.DestinationRootKey,
            context.Destination);

        InterruptedRestoreReport? report = InterruptedRestore.Describe(
            context.Database,
            context.SessionId,
            context.Workspace.RootPath);

        Assert.NotNull(report);
        Assert.Equal(2, report.IncompleteItems);
        Assert.Equal(1, report.CompletedItems);
        Assert.Equal(context.Source, report.SourceRoot);
        Assert.Equal(context.Destination, report.DestinationRoot);
        Assert.Equal(3, report.Plan!.Items.Count);
    }

    [Fact]
    public async Task FindLatest_ReopensTheNewestInterruptedSession()
    {
        string localData = Path.Combine(Path.GetTempPath(), $"WinOldRecovery-Find-{Guid.NewGuid():N}");
        Directory.CreateDirectory(localData);
        SourceGuard guard = new();
        SafeFs safeFs = new(guard);
        try
        {
            SessionWorkspace older = SessionWorkspace.Create(safeFs, localData, new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
            SessionWorkspace newer = SessionWorkspace.Create(safeFs, localData, new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero));
            {
                await using SessionDb olderDb = await SessionDb.OpenAsync(older.DatabasePath, safeFs);
                await olderDb.CreateSessionAsync(new SessionRecord(older.SessionId, DateTimeOffset.UtcNow, "Created", "0.1.0"));
                await StoreFileAsync(olderDb, older.SessionId, Path.Combine(localData, "old.txt"));
            }

            {
                await using SessionDb newerDb = await SessionDb.OpenAsync(newer.DatabasePath, safeFs);
                await newerDb.CreateSessionAsync(new SessionRecord(newer.SessionId, DateTimeOffset.UtcNow, "Created", "0.1.0"));
                PlanItem item = await StoreFileAsync(newerDb, newer.SessionId, Path.Combine(localData, "new.txt"));
                await newerDb.AppendJournalAsync(item.Id!.Value, "Paused", "DiskFull");
            }

            InterruptedRestoreReport? report = InterruptedRestore.FindLatest(safeFs, localData);

            Assert.NotNull(report);
            Assert.Equal(newer.SessionId, report.SessionId);
            Assert.Equal(newer.RootPath, report.WorkspaceRoot);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(localData))
            {
                Directory.Delete(localData, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FindLatest_SkipsScanOnlyNewerSessionsWithoutOpeningThemAsWriters()
    {
        string localData = Path.Combine(Path.GetTempPath(), $"WinOldRecovery-FindScan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(localData);
        SourceGuard guard = new();
        SafeFs safeFs = new(guard);
        try
        {
            SessionWorkspace older = SessionWorkspace.Create(
                safeFs,
                localData,
                new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
            SessionWorkspace newer = SessionWorkspace.Create(
                safeFs,
                localData,
                new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero));
            await using (SessionDb olderDb = await SessionDb.OpenAsync(older.DatabasePath, safeFs))
            {
                await olderDb.CreateSessionAsync(
                    new SessionRecord(older.SessionId, DateTimeOffset.UtcNow, "Created", "0.1.0"));
                PlanItem item = await StoreFileAsync(olderDb, older.SessionId, Path.Combine(localData, "old.txt"));
                await olderDb.AppendJournalAsync(item.Id!.Value, "Started");
            }

            await using (SessionDb newerDb = await SessionDb.OpenAsync(newer.DatabasePath, safeFs))
            {
                await newerDb.CreateSessionAsync(
                    new SessionRecord(newer.SessionId, DateTimeOffset.UtcNow, "Created", "0.1.0"));
            }

            InterruptedRestoreReport? report = InterruptedRestore.FindLatest(safeFs, localData);
            Assert.NotNull(report);
            Assert.Equal(older.SessionId, report.SessionId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(localData))
            {
                Directory.Delete(localData, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FindLatest_SkipsACorruptNewerDatabase()
    {
        string localData = Path.Combine(Path.GetTempPath(), $"WinOldRecovery-FindSkip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(localData);
        SourceGuard guard = new();
        SafeFs safeFs = new(guard);
        try
        {
            SessionWorkspace older = SessionWorkspace.Create(
                safeFs,
                localData,
                new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
            SessionWorkspace newer = SessionWorkspace.Create(
                safeFs,
                localData,
                new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero));
            await using (SessionDb olderDb = await SessionDb.OpenAsync(older.DatabasePath, safeFs))
            {
                await olderDb.CreateSessionAsync(
                    new SessionRecord(older.SessionId, DateTimeOffset.UtcNow, "Created", "0.1.0"));
                PlanItem item = await StoreFileAsync(olderDb, older.SessionId, Path.Combine(localData, "old.txt"));
                await olderDb.AppendJournalAsync(item.Id!.Value, "Started");
            }

            await File.WriteAllTextAsync(newer.DatabasePath, "not-a-sqlite-database");

            InterruptedRestoreReport? report = InterruptedRestore.FindLatest(safeFs, localData);
            Assert.NotNull(report);
            Assert.Equal(older.SessionId, report.SessionId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(localData))
            {
                Directory.Delete(localData, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FindLatest_SkipsADatabaseHeldOpenByAnotherHandle()
    {
        string localData = Path.Combine(Path.GetTempPath(), $"WinOldRecovery-FindLock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(localData);
        SourceGuard guard = new();
        SafeFs safeFs = new(guard);
        FileStream? locker = null;
        try
        {
            SessionWorkspace older = SessionWorkspace.Create(
                safeFs,
                localData,
                new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
            SessionWorkspace newer = SessionWorkspace.Create(
                safeFs,
                localData,
                new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero));
            await using (SessionDb olderDb = await SessionDb.OpenAsync(older.DatabasePath, safeFs))
            {
                await olderDb.CreateSessionAsync(
                    new SessionRecord(older.SessionId, DateTimeOffset.UtcNow, "Created", "0.1.0"));
                PlanItem item = await StoreFileAsync(olderDb, older.SessionId, Path.Combine(localData, "old.txt"));
                await olderDb.AppendJournalAsync(item.Id!.Value, "Started");
            }

            await File.WriteAllBytesAsync(newer.DatabasePath, [0, 1, 2, 3]);
            locker = new FileStream(newer.DatabasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

            InterruptedRestoreReport? report = InterruptedRestore.FindLatest(safeFs, localData);
            Assert.NotNull(report);
            Assert.Equal(older.SessionId, report.SessionId);
        }
        finally
        {
            locker?.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(localData))
            {
                Directory.Delete(localData, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FindLatest_SkipsOversizedNewerDatabasesWithoutOpeningThem()
    {
        string localData = Path.Combine(Path.GetTempPath(), $"WinOldRecovery-FindHuge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(localData);
        SourceGuard guard = new();
        SafeFs safeFs = new(guard);
        try
        {
            SessionWorkspace older = SessionWorkspace.Create(
                safeFs,
                localData,
                new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
            SessionWorkspace newer = SessionWorkspace.Create(
                safeFs,
                localData,
                new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero));
            await using (SessionDb olderDb = await SessionDb.OpenAsync(older.DatabasePath, safeFs))
            {
                await olderDb.CreateSessionAsync(
                    new SessionRecord(older.SessionId, DateTimeOffset.UtcNow, "Created", "0.1.0"));
                PlanItem item = await StoreFileAsync(olderDb, older.SessionId, Path.Combine(localData, "old.txt"));
                await olderDb.AppendJournalAsync(item.Id!.Value, "Started");
            }

            await using (FileStream stream = new(newer.DatabasePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(InterruptedRestore.MaxPeekDatabaseBytes + 1);
            }

            InterruptedRestoreReport? report = InterruptedRestore.FindLatest(safeFs, localData);
            Assert.NotNull(report);
            Assert.Equal(older.SessionId, report.SessionId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(localData))
            {
                Directory.Delete(localData, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FindLatest_SkipsInterruptedSessionsWhoseSourceFolderIsGone()
    {
        string localData = Path.Combine(Path.GetTempPath(), $"WinOldRecovery-FindGone-{Guid.NewGuid():N}");
        Directory.CreateDirectory(localData);
        SourceGuard guard = new();
        SafeFs safeFs = new(guard);
        string goneSource = Path.Combine(localData, "gone-source");
        Directory.CreateDirectory(goneSource);
        try
        {
            SessionWorkspace older = SessionWorkspace.Create(
                safeFs,
                localData,
                new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
            SessionWorkspace newer = SessionWorkspace.Create(
                safeFs,
                localData,
                new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero));
            await using (SessionDb olderDb = await SessionDb.OpenAsync(older.DatabasePath, safeFs))
            {
                await olderDb.CreateSessionAsync(
                    new SessionRecord(older.SessionId, DateTimeOffset.UtcNow, "Created", "0.1.0"));
                PlanItem item = await StoreFileAsync(olderDb, older.SessionId, Path.Combine(localData, "old.txt"));
                await olderDb.AppendJournalAsync(item.Id!.Value, "Started");
                await olderDb.SetKvAsync(older.SessionId, InterruptedRestore.SourceRootKey, localData);
            }

            await using (SessionDb newerDb = await SessionDb.OpenAsync(newer.DatabasePath, safeFs))
            {
                await newerDb.CreateSessionAsync(
                    new SessionRecord(newer.SessionId, DateTimeOffset.UtcNow, "Created", "0.1.0"));
                PlanItem item = await StoreFileAsync(newerDb, newer.SessionId, Path.Combine(goneSource, "partial.txt"));
                await newerDb.AppendJournalAsync(item.Id!.Value, "Started");
                await newerDb.SetKvAsync(newer.SessionId, InterruptedRestore.SourceRootKey, goneSource);
            }

            Directory.Delete(goneSource, recursive: true);

            InterruptedRestoreReport? report = InterruptedRestore.FindLatest(safeFs, localData);
            Assert.NotNull(report);
            Assert.Equal(older.SessionId, report.SessionId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(localData))
            {
                Directory.Delete(localData, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FindLatest_ReturnsNullWhenCancelledBeforePeeking()
    {
        string localData = Path.Combine(Path.GetTempPath(), $"WinOldRecovery-FindCancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(localData);
        SourceGuard guard = new();
        SafeFs safeFs = new(guard);
        try
        {
            SessionWorkspace workspace = SessionWorkspace.Create(
                safeFs,
                localData,
                new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero));
            await using (SessionDb database = await SessionDb.OpenAsync(workspace.DatabasePath, safeFs))
            {
                await database.CreateSessionAsync(
                    new SessionRecord(workspace.SessionId, DateTimeOffset.UtcNow, "Created", "0.1.0"));
                PlanItem item = await StoreFileAsync(database, workspace.SessionId, Path.Combine(localData, "paused.txt"));
                await database.AppendJournalAsync(item.Id!.Value, "Paused");
            }

            using CancellationTokenSource cts = new();
            cts.Cancel();
            Assert.Null(InterruptedRestore.FindLatest(safeFs, localData, cts.Token));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(localData))
            {
                Directory.Delete(localData, recursive: true);
            }
        }
    }

    private static async Task<PlanItem> StoreFileAsync(SessionDb database, string sessionId, string source)
    {
        PlanItem item = new(
            sessionId,
            1,
            PlanOperation.CopyFile,
            source,
            source + ".out",
            1,
            ConflictPolicy.KeepBoth,
            OverwriteApproved: false,
            RecipeId: null);
        IReadOnlyList<PlanItem> stored = await database.InsertPlanItemsAsync([item]);
        return stored[0];
    }

    private sealed class SessionContext : IAsyncDisposable
    {
        private SessionContext(string root, SessionWorkspace workspace, SessionDb database)
        {
            Root = root;
            Workspace = workspace;
            Database = database;
            Source = Path.Combine(root, "Windows.old");
            Destination = Path.Combine(root, "Recovered");
            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(Destination);
        }

        public string Root { get; }
        public SessionWorkspace Workspace { get; }
        public SessionDb Database { get; }
        public string Source { get; }
        public string Destination { get; }
        public string SessionId => Workspace.SessionId;

        public static async Task<SessionContext> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), $"WinOldRecovery-Interrupt-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            SourceGuard guard = new();
            SafeFs safeFs = new(guard);
            SessionWorkspace workspace = SessionWorkspace.Create(safeFs, root);
            SessionDb database = await SessionDb.OpenAsync(workspace.DatabasePath, safeFs);
            await database.CreateSessionAsync(
                new SessionRecord(workspace.SessionId, DateTimeOffset.UtcNow, "Created", "0.1.0"));
            return new SessionContext(root, workspace, database);
        }

        public async Task<PlanItem> StoreAsync(string name)
        {
            return await StoreFileAsync(
                Database,
                SessionId,
                Path.Combine(Source, name));
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }
}
