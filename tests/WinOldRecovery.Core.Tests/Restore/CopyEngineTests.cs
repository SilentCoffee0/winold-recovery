using System.Diagnostics;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Planning;
using WinOldRecovery.Core.Purge;
using WinOldRecovery.Core.Restore;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Core.Verify;

namespace WinOldRecovery.Core.Tests.Restore;

public sealed class CopyEngineTests
{
    [Fact]
    public async Task I2_KeepBothLeavesExistingDestinationUnchanged()
    {
        await using CopyContext context = await CopyContext.CreateAsync();
        string sourceFile = Path.Combine(context.Source, "note.txt");
        await File.WriteAllTextAsync(sourceFile, "from-old");
        File.SetLastWriteTimeUtc(sourceFile, new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        string destFile = Path.Combine(context.Destination, "note.txt");
        await File.WriteAllTextAsync(destFile, "already-here");

        PlanItem item = await context.StoreAsync(
            PlanOperation.CopyFile,
            sourceFile,
            destFile,
            ConflictPolicy.KeepBoth);
        CopyEngine engine = new(context.Database, context.SafeFs);
        RestoreItemResult result = await engine.CopyAsync(item);

        Assert.Equal("Completed", result.State);
        Assert.Equal("already-here", await File.ReadAllTextAsync(destFile));
        string keepBoth = Path.Combine(context.Destination, "note (from Windows.old).txt");
        Assert.Equal("from-old", await File.ReadAllTextAsync(keepBoth));
        Assert.False(Directory.EnumerateFiles(context.Destination, "*" + CopyEngine.PartialSuffix).Any());
        Assert.Equal("from-old", await File.ReadAllTextAsync(sourceFile));
        FileInfo restored = new(keepBoth);
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), restored.LastWriteTimeUtc);
        Assert.False(restored.Attributes.HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public async Task SkipPolicy_DoesNotReplaceExistingFile()
    {
        await using CopyContext context = await CopyContext.CreateAsync();
        string sourceFile = Path.Combine(context.Source, "note.txt");
        await File.WriteAllTextAsync(sourceFile, "from-old");
        string destFile = Path.Combine(context.Destination, "note.txt");
        await File.WriteAllTextAsync(destFile, "keep");

        PlanItem item = await context.StoreAsync(
            PlanOperation.CopyFile,
            sourceFile,
            destFile,
            ConflictPolicy.Skip);
        await new CopyEngine(context.Database, context.SafeFs).CopyAsync(item);

        Assert.Equal("keep", await File.ReadAllTextAsync(destFile));
        Assert.False(File.Exists(Path.Combine(context.Destination, "note (from Windows.old).txt")));
    }

    [Fact]
    public async Task I2_OverwriteOnlyReplacesWhenThatFileIsApproved()
    {
        await using CopyContext context = await CopyContext.CreateAsync();
        string sourceFile = Path.Combine(context.Source, "note.txt");
        await File.WriteAllTextAsync(sourceFile, "from-old");
        string destFile = Path.Combine(context.Destination, "note.txt");
        await File.WriteAllTextAsync(destFile, "already-here");
        await context.Database.SetKvAsync(
            context.SessionId,
            OverwriteApprovals.KvKey,
            destFile);

        PlanItem item = await context.StoreAsync(
            PlanOperation.CopyFile,
            sourceFile,
            destFile,
            ConflictPolicy.KeepBoth);
        await new CopyEngine(context.Database, context.SafeFs).CopyAsync(item);

        Assert.Equal("from-old", await File.ReadAllTextAsync(destFile));
        Assert.False(File.Exists(Path.Combine(context.Destination, "note (from Windows.old).txt")));
    }

    [Fact]
    public async Task I14_RestoredFileDoesNotCopySourceAcls()
    {
        await using CopyContext context = await CopyContext.CreateAsync();
        string sourceFile = Path.Combine(context.Source, "note.txt");
        await File.WriteAllTextAsync(sourceFile, "from-old");
        SecurityIdentifier marker = new("S-1-5-21-3623811015-3361044348-30300820-1013");
        FileInfo sourceInfo = new(sourceFile);
        FileSecurity sourceSecurity = sourceInfo.GetAccessControl();
        sourceSecurity.AddAccessRule(
            new FileSystemAccessRule(marker, FileSystemRights.Read, AccessControlType.Allow));
        sourceInfo.SetAccessControl(sourceSecurity);
        Assert.True(HasExplicitRule(sourceFile, marker));

        string destFile = Path.Combine(context.Destination, "note.txt");
        PlanItem item = await context.StoreAsync(
            PlanOperation.CopyFile,
            sourceFile,
            destFile,
            ConflictPolicy.KeepBoth);
        await new CopyEngine(context.Database, context.SafeFs).CopyAsync(item);

        Assert.Equal("from-old", await File.ReadAllTextAsync(destFile));
        Assert.False(HasExplicitRule(destFile, marker));
        Assert.False(HasExplicitRule(context.Destination, marker));
    }

    [Fact]
    public async Task CrashResume_DeletesPartialAndFinishesWithoutLeftovers()
    {
        await using CopyContext context = await CopyContext.CreateAsync();
        string sourceFile = Path.Combine(context.Source, "note.txt");
        await File.WriteAllTextAsync(sourceFile, "complete");
        string destFile = Path.Combine(context.Destination, "note.txt");
        PlanItem item = await context.StoreAsync(
            PlanOperation.CopyFile,
            sourceFile,
            destFile,
            ConflictPolicy.KeepBoth);
        await context.Database.AppendJournalAsync(item.Id!.Value, "Started");
        await File.WriteAllTextAsync(destFile + CopyEngine.PartialSuffix, "torn");

        RestoreItemResult result = await new CopyEngine(context.Database, context.SafeFs).CopyAsync(item);

        Assert.Equal("Completed", result.State);
        Assert.Equal("complete", await File.ReadAllTextAsync(destFile));
        Assert.False(File.Exists(destFile + CopyEngine.PartialSuffix));
        Assert.Equal("complete", await File.ReadAllTextAsync(sourceFile));
    }

    [Fact]
    public async Task DiskFull_PausesWithoutDeletingExistingDestination()
    {
        await using CopyContext context = await CopyContext.CreateAsync();
        string sourceFile = Path.Combine(context.Source, "note.txt");
        await File.WriteAllTextAsync(sourceFile, "from-old");
        string destFile = Path.Combine(context.Destination, "note.txt");
        await File.WriteAllTextAsync(destFile, "keep");
        PlanItem item = await context.StoreAsync(
            PlanOperation.CopyFile,
            sourceFile,
            destFile,
            ConflictPolicy.KeepBoth);
        context.SafeFs.FailNextWriteAsDiskFull = true;
        RestoreResult result = await new RestoreRunner(new CopyEngine(context.Database, context.SafeFs))
            .RunAsync(new RestorePlan(context.SessionId, context.Source, context.Destination, [item], 1));

        Assert.True(result.PausedDiskFull);
        Assert.False(result.PausedByUser);
        Assert.False(result.Completed);
        Assert.Equal("keep", await File.ReadAllTextAsync(destFile));
        Assert.False(Directory.EnumerateFiles(context.Destination, "*" + CopyEngine.PartialSuffix).Any());
    }

    [Fact]
    public async Task UserPause_StopsBeforeLaterPlanItems_AndResumeCopiesTheRest()
    {
        await using CopyContext context = await CopyContext.CreateAsync();
        string firstSource = Path.Combine(context.Source, "one.txt");
        string secondSource = Path.Combine(context.Source, "two.txt");
        await File.WriteAllTextAsync(firstSource, "first");
        await File.WriteAllTextAsync(secondSource, "second");
        string firstDest = Path.Combine(context.Destination, "one.txt");
        string secondDest = Path.Combine(context.Destination, "two.txt");
        IReadOnlyList<PlanItem> stored = await context.Database.ReplacePlanItemsAsync(
            context.SessionId,
            [
                new PlanItem(
                    context.SessionId,
                    1,
                    PlanOperation.CopyFile,
                    firstSource,
                    firstDest,
                    5,
                    ConflictPolicy.KeepBoth,
                    OverwriteApproved: false,
                    RecipeId: null),
                new PlanItem(
                    context.SessionId,
                    2,
                    PlanOperation.CopyFile,
                    secondSource,
                    secondDest,
                    6,
                    ConflictPolicy.KeepBoth,
                    OverwriteApproved: false,
                    RecipeId: null),
            ]);
        RestorePlan plan = new(context.SessionId, context.Source, context.Destination, stored, 11);
        using CancellationTokenSource pause = new();
        Progress<RestoreProgress> progress = new(report =>
        {
            if (report.CompletedItems >= 1)
            {
                pause.Cancel();
            }
        });
        RestoreRunner runner = new(new CopyEngine(context.Database, context.SafeFs), context.Database);

        RestoreResult paused = await runner.RunAsync(
            plan,
            CancellationToken.None,
            progress,
            pause.Token);

        Assert.True(paused.PausedByUser);
        Assert.False(paused.PausedDiskFull);
        Assert.False(paused.Completed);
        Assert.Equal("first", await File.ReadAllTextAsync(firstDest));
        Assert.False(File.Exists(secondDest));
        Assert.Equal("Completed", context.Database.GetLatestJournalState(stored[0].Id!.Value));
        Assert.Equal("Paused", context.Database.GetLatestJournalState(stored[1].Id!.Value));
        Assert.Equal("first", await File.ReadAllTextAsync(firstSource));

        RestoreResult resumed = await runner.RunAsync(plan);

        Assert.True(resumed.Completed);
        Assert.Equal("second", await File.ReadAllTextAsync(secondDest));
        Assert.False(Directory.EnumerateFiles(context.Destination, "*" + CopyEngine.PartialSuffix).Any());
        Assert.DoesNotContain(
            Directory.EnumerateFiles(context.Destination),
            path => path.Contains("from Windows.old", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Resume_CopyTree_AfterPaused_DoesNotKeepBothAlreadyCopiedFiles()
    {
        await using CopyContext context = await CopyContext.CreateAsync();
        string tree = Path.Combine(context.Source, "docs");
        Directory.CreateDirectory(tree);
        string firstSource = Path.Combine(tree, "a.txt");
        string secondSource = Path.Combine(tree, "b.txt");
        await File.WriteAllTextAsync(firstSource, "alpha");
        await File.WriteAllTextAsync(secondSource, "beta");
        DateTime stamp = new(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(firstSource, stamp);
        string destTree = Path.Combine(context.Destination, "docs");
        Directory.CreateDirectory(destTree);
        string firstDest = Path.Combine(destTree, "a.txt");
        await File.WriteAllTextAsync(firstDest, "alpha");
        File.SetLastWriteTimeUtc(firstDest, stamp);
        PlanItem item = await context.StoreAsync(
            PlanOperation.CopyTree,
            tree,
            destTree,
            ConflictPolicy.KeepBoth);
        await context.Database.AppendJournalAsync(item.Id!.Value, "Paused", RestorePausedException.UserReason);

        RestoreItemResult result = await new CopyEngine(context.Database, context.SafeFs).CopyAsync(item);

        Assert.Equal("Completed", result.State);
        Assert.Equal("alpha", await File.ReadAllTextAsync(firstDest));
        Assert.Equal("beta", await File.ReadAllTextAsync(Path.Combine(destTree, "b.txt")));
        Assert.False(File.Exists(Path.Combine(destTree, "a (from Windows.old).txt")));
    }

    [Fact]
    public void Preflight_OverwritePolicyBlocksUntilEachConflictIsApproved()
    {
        string dest = Path.Combine(Path.GetTempPath(), $"WinOldRecovery-conflict-{Guid.NewGuid():N}.txt");
        File.WriteAllText(dest, "exists");
        try
        {
            RestorePlan plan = new(
                "session-1",
                @"C:\old",
                Path.GetTempPath(),
                [
                    new PlanItem(
                        "session-1",
                        1,
                        PlanOperation.CopyFile,
                        Path.Combine(Path.GetTempPath(), "src.txt"),
                        dest,
                        4,
                        ConflictPolicy.KeepBoth,
                        false,
                        null),
                ],
                4);
            PreflightResult blocked = new PreflightChecker(new FixedFreeSpace(long.MaxValue))
                .Check(plan, ConflictPolicy.OverwriteApproved);
            Assert.False(blocked.CanProceed);
            Assert.Contains(blocked.BlockingIssues, issue => issue.Contains("per-file", StringComparison.OrdinalIgnoreCase));

            PreflightResult allowed = new PreflightChecker(new FixedFreeSpace(long.MaxValue))
                .Check(plan, ConflictPolicy.OverwriteApproved, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { dest });
            Assert.True(allowed.CanProceed);
        }
        finally
        {
            File.Delete(dest);
        }
    }

    [Fact]
    public async Task CopyTree_SkipsReparsePointsAndVerifiesHashes()
    {
        await using CopyContext context = await CopyContext.CreateAsync();
        string tree = Path.Combine(context.Source, "Desktop");
        Directory.CreateDirectory(tree);
        await File.WriteAllTextAsync(Path.Combine(tree, "a.txt"), "aaa");
        string live = Path.Combine(context.Root, "live");
        Directory.CreateDirectory(live);
        await File.WriteAllTextAsync(Path.Combine(live, "secret.txt"), "no");
        CreateJunction(Path.Combine(tree, "link"), live);

        PlanItem item = await context.StoreAsync(
            PlanOperation.CopyTree,
            tree,
            Path.Combine(context.Destination, "Desktop"),
            ConflictPolicy.KeepBoth);
        RestoreItemResult copied = await new CopyEngine(context.Database, context.SafeFs).CopyAsync(item);

        Assert.True(File.Exists(Path.Combine(context.Destination, "Desktop", "a.txt")));
        Assert.False(Directory.Exists(Path.Combine(context.Destination, "Desktop", "link")));
        Assert.False(File.Exists(Path.Combine(context.Destination, "Desktop", "link", "secret.txt")));
        Assert.NotNull(copied.Skips);
        Assert.Equal(1, copied.Skips.Reparse);
        Assert.Equal(1, copied.Skips.Total);

        RestorePlan plan = new(
            context.SessionId,
            context.Source,
            context.Destination,
            [item],
            3);
        VerifyReport report = await new Verifier(context.Database, context.SafeFs).VerifyAsync(plan);
        Assert.True(report.AllOk);
        Assert.Contains(report.Rows, row => row.Level == 2 && row.Ok);
        Assert.Equal(1, report.SizeTimeFiles);
        Assert.Equal(1, report.SizeTimeOk);
        Assert.Equal(1, report.HashFiles);
        Assert.Equal(1, report.HashOk);
    }

    [Fact]
    public async Task CopyTree_SkipsOfflineFilesAndVerifyStillPasses()
    {
        await using CopyContext context = await CopyContext.CreateAsync();
        string tree = Path.Combine(context.Source, "Desktop");
        Directory.CreateDirectory(tree);
        await File.WriteAllTextAsync(Path.Combine(tree, "keep.txt"), "ok");
        File.SetLastWriteTimeUtc(Path.Combine(tree, "keep.txt"), new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc));
        string offline = Path.Combine(tree, "cloud.txt");
        await File.WriteAllTextAsync(offline, "placeholder");
        File.SetAttributes(offline, File.GetAttributes(offline) | FileAttributes.Offline);
        PlanItem item = await context.StoreAsync(
            PlanOperation.CopyTree,
            tree,
            Path.Combine(context.Destination, "Desktop"),
            ConflictPolicy.KeepBoth);

        RestoreItemResult copy = await new CopyEngine(context.Database, context.SafeFs).CopyAsync(item);
        VerifyReport report = await new Verifier(context.Database, context.SafeFs).VerifyAsync(
            new RestorePlan(context.SessionId, context.Source, context.Destination, [item], 2));

        Assert.Equal("Completed", copy.State);
        Assert.True(File.Exists(Path.Combine(context.Destination, "Desktop", "keep.txt")));
        Assert.False(File.Exists(Path.Combine(context.Destination, "Desktop", "cloud.txt")));
        Assert.NotNull(copy.Skips);
        Assert.Equal(1, copy.Skips.Offline);
        Assert.Equal(1, copy.Skips.Total);
        Assert.True(report.AllOk, string.Join(';', report.Rows.Select(row => row.Level + ":" + row.Ok + ":" + row.Detail)));
        Assert.True(context.Database.LastVerifyReportAllOk(context.SessionId));
        Assert.Equal(1, report.SizeTimeFiles);
        Assert.Equal(1, report.SizeTimeOk);
        Assert.Equal(1, report.HashFiles);
        Assert.Equal(1, report.HashOk);
    }

    [Fact]
    public async Task Resume_SkipsCompletedItems()
    {
        await using CopyContext context = await CopyContext.CreateAsync();
        string sourceFile = Path.Combine(context.Source, "note.txt");
        await File.WriteAllTextAsync(sourceFile, "once");
        string destFile = Path.Combine(context.Destination, "note.txt");
        PlanItem item = await context.StoreAsync(
            PlanOperation.CopyFile,
            sourceFile,
            destFile,
            ConflictPolicy.KeepBoth);
        CopyEngine engine = new(context.Database, context.SafeFs);
        await engine.CopyAsync(item);
        await File.WriteAllTextAsync(sourceFile, "changed");
        RestoreItemResult second = await engine.CopyAsync(item);
        Assert.Equal("Completed", second.State);
        Assert.Equal("once", await File.ReadAllTextAsync(destFile));
    }

    [Fact]
    public async Task Resume_CopyTree_DoesNotDuplicateAlreadyCopiedFiles()
    {
        await using CopyContext context = await CopyContext.CreateAsync();
        string tree = Path.Combine(context.Source, "Desktop");
        Directory.CreateDirectory(tree);
        await File.WriteAllTextAsync(Path.Combine(tree, "a.txt"), "aaa");
        File.SetLastWriteTimeUtc(Path.Combine(tree, "a.txt"), new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        string dest = Path.Combine(context.Destination, "Desktop");
        PlanItem item = await context.StoreAsync(
            PlanOperation.CopyTree,
            tree,
            dest,
            ConflictPolicy.KeepBoth);
        CopyEngine engine = new(context.Database, context.SafeFs);
        await engine.CopyAsync(item);
        await context.Database.AppendJournalAsync(item.Id!.Value, "Started");

        RestoreItemResult second = await engine.CopyAsync(item);

        Assert.Equal("Completed", second.State);
        Assert.Equal("aaa", await File.ReadAllTextAsync(Path.Combine(dest, "a.txt")));
        Assert.False(File.Exists(Path.Combine(dest, "a (from Windows.old).txt")));
    }

    [Fact]
    public async Task DeletePartial_DoesNotFollowDestinationJunctions()
    {
        await using CopyContext context = await CopyContext.CreateAsync();
        string tree = Path.Combine(context.Source, "Desktop");
        Directory.CreateDirectory(tree);
        await File.WriteAllTextAsync(Path.Combine(tree, "a.txt"), "aaa");
        string dest = Path.Combine(context.Destination, "Desktop");
        Directory.CreateDirectory(dest);
        string live = Path.Combine(context.Root, "live");
        Directory.CreateDirectory(live);
        string trap = Path.Combine(live, "trap" + CopyEngine.PartialSuffix);
        await File.WriteAllTextAsync(trap, "do-not-delete");
        CreateJunction(Path.Combine(dest, "link"), live);
        PlanItem item = await context.StoreAsync(
            PlanOperation.CopyTree,
            tree,
            dest,
            ConflictPolicy.KeepBoth);
        await context.Database.AppendJournalAsync(item.Id!.Value, "Started");

        await new CopyEngine(context.Database, context.SafeFs).CopyAsync(item);

        Assert.Equal("do-not-delete", await File.ReadAllTextAsync(trap));
        Assert.Equal("aaa", await File.ReadAllTextAsync(Path.Combine(dest, "a.txt")));
    }

    [Fact]
    public async Task Verifier_KeepBoth_ChecksTheRestoredCopyNotThePreexistingFile()
    {
        await using CopyContext context = await CopyContext.CreateAsync();
        string sourceFile = Path.Combine(context.Source, "note.txt");
        await File.WriteAllTextAsync(sourceFile, "from-old");
        File.SetLastWriteTimeUtc(sourceFile, new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        string destFile = Path.Combine(context.Destination, "note.txt");
        await File.WriteAllTextAsync(destFile, "already-here-different");
        PlanItem item = await context.StoreAsync(
            PlanOperation.CopyFile,
            sourceFile,
            destFile,
            ConflictPolicy.KeepBoth);
        await new CopyEngine(context.Database, context.SafeFs).CopyAsync(item);
        RestorePlan plan = new(context.SessionId, context.Source, context.Destination, [item], 8);

        VerifyReport report = await new Verifier(context.Database, context.SafeFs).VerifyAsync(plan);

        Assert.True(report.AllOk, string.Join(';', report.Rows.Select(row => row.Level + ":" + row.Ok + ":" + row.Detail)));
        Assert.Equal("already-here-different", await File.ReadAllTextAsync(destFile));
        Assert.True(context.Database.LastVerifyReportAllOk(context.SessionId));
    }

    [Fact]
    public async Task RestoreRunner_FailedItem_IsNotReportedCompleted()
    {
        await using CopyContext context = await CopyContext.CreateAsync();
        string sourceFile = Path.Combine(context.Source, "note.txt");
        await File.WriteAllTextAsync(sourceFile, "from-old");
        string destFile = Path.Combine(context.Destination, "note.txt");
        PlanItem item = await context.StoreAsync(
            PlanOperation.CopyFile,
            sourceFile,
            destFile,
            ConflictPolicy.KeepBoth);
        File.Delete(sourceFile);

        RestoreResult result = await new RestoreRunner(new CopyEngine(context.Database, context.SafeFs))
            .RunAsync(new RestorePlan(context.SessionId, context.Source, context.Destination, [item], 1));

        Assert.False(result.Completed);
        Assert.Equal("Failed", Assert.Single(result.Items).State);
        Assert.False(context.Database.RestoreJournalSettled(context.SessionId));
    }

    [Fact]
    public async Task KeepBoth_DoesNotTreatMatchingSizeTimeExistingDestAsAlreadyCopied()
    {
        await using CopyContext context = await CopyContext.CreateAsync();
        DateTime stamp = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        string sourceFile = Path.Combine(context.Source, "note.txt");
        await File.WriteAllTextAsync(sourceFile, "from-old!");
        File.SetLastWriteTimeUtc(sourceFile, stamp);
        string destFile = Path.Combine(context.Destination, "note.txt");
        await File.WriteAllTextAsync(destFile, "live-copy!");
        File.SetLastWriteTimeUtc(destFile, stamp);
        PlanItem item = await context.StoreAsync(
            PlanOperation.CopyFile,
            sourceFile,
            destFile,
            ConflictPolicy.KeepBoth);

        RestoreItemResult result = await new CopyEngine(context.Database, context.SafeFs).CopyAsync(item);

        Assert.Equal("Completed", result.State);
        Assert.Equal("live-copy!", await File.ReadAllTextAsync(destFile));
        Assert.Equal(
            "from-old!",
            await File.ReadAllTextAsync(Path.Combine(context.Destination, "note (from Windows.old).txt")));
    }

    [Fact]
    public async Task Verifier_DoesNotPassWhenOnlyThePreexistingDestinationExists()
    {
        await using CopyContext context = await CopyContext.CreateAsync();
        string sourceFile = Path.Combine(context.Source, "note.txt");
        await File.WriteAllTextAsync(sourceFile, "from-old");
        File.SetLastWriteTimeUtc(sourceFile, new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        string destFile = Path.Combine(context.Destination, "note.txt");
        await File.WriteAllTextAsync(destFile, "already-here-different");
        PlanItem item = await context.StoreAsync(
            PlanOperation.CopyFile,
            sourceFile,
            destFile,
            ConflictPolicy.KeepBoth);
        RestorePlan plan = new(context.SessionId, context.Source, context.Destination, [item], 8);

        VerifyReport report = await new Verifier(context.Database, context.SafeFs).VerifyAsync(plan);

        Assert.False(report.AllOk);
        Assert.Contains(report.Rows, row => row.Level == 0 && !row.Ok);
        Assert.Equal(1, report.SizeTimeFiles);
        Assert.Equal(0, report.SizeTimeOk);
        Assert.Equal(1, report.HashFiles);
        Assert.Equal(0, report.HashOk);
        Assert.False(context.Database.LastVerifyReportAllOk(context.SessionId));
        Assert.False(context.Database.LastVerifyJobsSettled(context.SessionId));

        await context.Database.SetKvAsync(
            context.SessionId,
            VerifyAcknowledgement.KvKey(report.ReportId),
            "no");
        Assert.False(context.Database.LastVerifyJobsSettled(context.SessionId));

        await context.Database.SetKvAsync(
            context.SessionId,
            VerifyAcknowledgement.KvKey(report.ReportId),
            "checked restored copies");
        Assert.True(context.Database.LastVerifyJobsSettled(context.SessionId));

        VerifyReport again = await new Verifier(context.Database, context.SafeFs).VerifyAsync(plan);
        Assert.False(again.AllOk);
        Assert.False(context.Database.LastVerifyJobsSettled(context.SessionId));
    }

    [Fact]
    public void SageFlagName_PadsToFourDigits()
    {
        Assert.Equal("StateFlags0777", RegistryCleanupSage.StateFlagsName(777));
    }

    [Fact]
    public void Preflight_I16_BlocksWhenFreeSpaceBelowMargin()
    {
        RestorePlan plan = new(
            "session-1",
            @"C:\old",
            @"C:\new",
            [],
            TotalBytes: 10);
        PreflightResult result = new PreflightChecker(new FixedFreeSpace(100)).Check(plan);
        Assert.False(result.CanProceed);
        Assert.Contains(result.BlockingIssues, issue => issue.Contains("free space", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FixedFreeSpace(long bytes) : IFreeSpaceProvider
    {
        public long GetFreeBytes(string directoryPath) => bytes;
    }

    private sealed class CopyContext : IAsyncDisposable
    {
        private CopyContext(string root, SessionDb database, SafeFs safeFs, SourceGuard guard)
        {
            Root = root;
            Database = database;
            SafeFs = safeFs;
            Guard = guard;
            Source = Path.Combine(root, "Windows.old");
            Destination = Path.Combine(root, "Recovered");
            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(Destination);
            Guard.RegisterSourceRoot(Source);
        }

        public string Root { get; }
        public SessionDb Database { get; }
        public SafeFs SafeFs { get; }
        public SourceGuard Guard { get; }
        public string Source { get; }
        public string Destination { get; }
        public string SessionId => "session-1";

        public static async Task<CopyContext> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), $"WinOldRecovery-Copy-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            SourceGuard guard = new();
            SafeFs safeFs = new(guard);
            SessionDb database = await SessionDb.OpenAsync(Path.Combine(root, "session.db"), safeFs);
            await database.CreateSessionAsync(
                new SessionRecord("session-1", DateTimeOffset.UtcNow, "Restoring", "0.1.0"));
            return new CopyContext(root, database, safeFs, guard);
        }

        public async Task<PlanItem> StoreAsync(
            PlanOperation operation,
            string source,
            string dest,
            ConflictPolicy policy)
        {
            PlanItem item = new(
                SessionId,
                1,
                operation,
                source,
                dest,
                1,
                policy,
                OverwriteApproved: policy == ConflictPolicy.OverwriteApproved,
                RecipeId: null);
            IReadOnlyList<PlanItem> stored = await Database.ReplacePlanItemsAsync(SessionId, [item]);
            return stored[0];
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            SqliteConnection.ClearAllPools();
            DeleteTree(Root);
        }
    }

    private static void CreateJunction(string junction, string target)
    {
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
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("mklink");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
        }
    }

    private static void DeleteTree(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(entry);
            }
            else if ((attributes & FileAttributes.Directory) != 0)
            {
                DeleteTree(entry);
            }
            else
            {
                File.Delete(entry);
            }
        }

        Directory.Delete(root);
    }

    private static bool HasExplicitRule(string path, SecurityIdentifier identity)
    {
        FileSystemSecurity security = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        foreach (FileSystemAccessRule rule in security
            .GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>())
        {
            if (rule.IdentityReference.Equals(identity))
            {
                return true;
            }
        }

        return false;
    }
}
