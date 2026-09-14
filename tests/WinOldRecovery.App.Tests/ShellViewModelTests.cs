using System.IO;
using System.Linq;
using WinOldRecovery.App;
using WinOldRecovery.App.Help;
using WinOldRecovery.App.ViewModels;
using WinOldRecovery.Core.Browse;
using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Planning;
using WinOldRecovery.Core.Processes;
using WinOldRecovery.Core.Recipes;
using WinOldRecovery.Core.Restore;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Core.Scan;
using WinOldRecovery.Core.Sessions;
using WinOldRecovery.Core.Verify;
using WinOldRecovery.Recipes;

namespace WinOldRecovery.App.Tests;

public sealed class ShellViewModelTests
{
    [Fact]
    public async Task CanGoTo_BlocksDecideUntilScanCompletesAndAlwaysAllowsBack()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        Assert.False(context.ViewModel.CanGoTo(WorkflowStep.Decide));
        context.ViewModel.CurrentStep = WorkflowStep.Decide;
        Assert.Equal(WorkflowStep.Scan, context.ViewModel.CurrentStep);

        string source = Path.Combine(context.Root, "Windows.old");
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice", "Desktop"));
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "NTUSER.DAT"),
            "hive");
        context.ViewModel.SelectedSourcePath = source;
        await context.ViewModel.ScanCommand.ExecuteAsync(null);

        Assert.True(context.ViewModel.ScanCompleted);
        Assert.Equal(WorkflowStep.Decide, context.ViewModel.CurrentStep);
        Assert.True(context.ViewModel.CanGoTo(WorkflowStep.Scan));
        Assert.False(context.ViewModel.CanGoTo(WorkflowStep.Purge));
        Assert.Contains(context.ViewModel.TreeRows, row => row.Name.Length > 0);
        Assert.Equal(DecidePane.Cards, context.ViewModel.DecidePane);
        Assert.Contains(context.ViewModel.Cards, card => card.Title == "Alice");
        Assert.Contains(context.ViewModel.Cards, card => card.Title == "Desktop");
        Assert.Equal("Windows.old untouched", context.ViewModel.SourceIntegrityText);
    }

    [Fact]
    public async Task Scan_AddsCollapsedCardsForAppsThatWereLookedForAndMissing()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync(RecipeCatalog.All);
        string source = Path.Combine(context.Root, "Windows.old");
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice", "Desktop"));
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "NTUSER.DAT"),
            "hive");
        context.ViewModel.SelectedSourcePath = source;
        await context.ViewModel.ScanCommand.ExecuteAsync(null);

        Assert.Contains(
            context.ViewModel.Cards,
            card => card.Kind == "Absent" && card.Title.Contains("SSH keys", StringComparison.Ordinal));
        Assert.DoesNotContain(
            context.ViewModel.Cards,
            card => card.Kind == "Absent" && card.Title.Contains("Alice", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OfferInterruptedRestore_ShowsResumeOverlayForTheReopenedPlan()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        string source = Path.Combine(context.Root, "Windows.old");
        string destination = Path.Combine(context.Root, "Recovered");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        string from = Path.Combine(source, "note.txt");
        await File.WriteAllTextAsync(from, "keep");
        PlanItem item = new(
            context.SessionId,
            1,
            PlanOperation.CopyFile,
            from,
            Path.Combine(destination, "note.txt"),
            1,
            ConflictPolicy.KeepBoth,
            OverwriteApproved: false,
            RecipeId: null);
        IReadOnlyList<PlanItem> stored = await context.Database.ReplacePlanItemsAsync(context.SessionId, [item]);
        await context.Database.AppendJournalAsync(stored[0].Id!.Value, "Started");
        InterruptedRestoreReport? report = InterruptedRestore.Describe(
            context.Database,
            context.SessionId,
            context.WorkspaceRoot);

        Assert.NotNull(report);
        context.ViewModel.OfferInterruptedRestore(report);

        Assert.True(context.ViewModel.InterruptedRestoreVisible);
        Assert.Contains("unfinished", context.ViewModel.InterruptedRestoreText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WorkflowStep.Restore, context.ViewModel.CurrentStep);
        Assert.True(context.ViewModel.ResumeInterruptedCommand.CanExecute(null));
        Assert.Single(context.ViewModel.RestoreJobs);
        Assert.Equal("running", context.ViewModel.RestoreJobs[0].Status);

        context.ViewModel.DismissInterruptedCommand.Execute(null);
        Assert.False(context.ViewModel.InterruptedRestoreVisible);
    }

    [Fact]
    public async Task PauseRestore_IsArmedOnlyWhileARestoreIsRunning()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        Assert.False(context.ViewModel.PauseRestoreCommand.CanExecute(null));
        context.ViewModel.ArmRestorePauseForTests();
        Assert.True(context.ViewModel.PauseRestoreCommand.CanExecute(null));
        context.ViewModel.PauseRestoreCommand.Execute(null);
        Assert.False(context.ViewModel.PauseRestoreCommand.CanExecute(null));
        context.ViewModel.SetRestoringForTests(false);
    }

    [Fact]
    public async Task GoToPurge_OpensPurgeAfterVerify()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        Assert.False(context.ViewModel.GoToPurgeCommand.CanExecute(null));
        context.ViewModel.UnlockPurgeForTests();
        Assert.True(context.ViewModel.GoToPurgeCommand.CanExecute(null));
        context.ViewModel.GoToPurgeCommand.Execute(null);
        Assert.Equal(WorkflowStep.Purge, context.ViewModel.CurrentStep);
    }

    [Fact]
    public async Task PlannedDestination_FollowsAFolderOverrideOntoChildren()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        string source = Path.Combine(context.Root, "Windows.old");
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice", "Desktop"));
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "NTUSER.DAT"),
            "hive");
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "Desktop", "notes.txt"),
            "keep");
        context.ViewModel.SelectedSourcePath = source;
        await context.ViewModel.ScanCommand.ExecuteAsync(null);
        ExpandDirectories(context);
        ExpandDirectories(context);
        context.ViewModel.Expand(FindRow(context, "Alice"));
        context.ViewModel.Expand(FindRow(context, "Desktop"));
        context.ViewModel.SelectedNode = FindRow(context, "Desktop");
        string dest = Path.Combine(context.Root, "DeskOut");
        Directory.CreateDirectory(dest);

        context.ViewModel.PlannedDestinationPath = dest;

        Assert.True(context.ViewModel.CanEditDestination);
        Assert.Contains("DeskOut", context.ViewModel.PlannedDestinationPath, StringComparison.OrdinalIgnoreCase);
        context.ViewModel.SelectedNode = FindRow(context, "notes.txt");
        Assert.Contains("notes.txt", context.ViewModel.PlannedDestinationPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(context.ViewModel.CanEditDestination);
    }

    [Fact]
    public async Task Inspect_ShowsDestinationConflictPreview()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        string source = Path.Combine(context.Root, "Windows.old");
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice", "Desktop"));
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "NTUSER.DAT"),
            "hive");
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "Desktop", "notes.txt"),
            "from-old");
        string recovered = Path.Combine(context.Root, "Recovered");
        string destFile = Path.Combine(recovered, "Users", "Alice", "Desktop", "notes.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
        await File.WriteAllTextAsync(destFile, "already-here");
        context.ViewModel.SelectedSourcePath = source;
        await context.ViewModel.ScanCommand.ExecuteAsync(null);
        context.ViewModel.DestinationRoot = recovered;
        ExpandDirectories(context);
        ExpandDirectories(context);
        context.ViewModel.Expand(FindRow(context, "Alice"));
        context.ViewModel.Expand(FindRow(context, "Desktop"));
        context.ViewModel.SelectedNode = FindRow(context, "notes.txt");

        Assert.Contains("already has 1 files", context.ViewModel.DetailText, StringComparison.Ordinal);
        Assert.Contains("1 differ", context.ViewModel.DetailText, StringComparison.Ordinal);
        Assert.Contains("Owner:", context.ViewModel.DetailText, StringComparison.Ordinal);
        Assert.Contains("Attributes:", context.ViewModel.DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inspect_ShowsOldestNewestAndClassificationWhy()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        string source = Path.Combine(context.Root, "Windows.old");
        string desktop = Path.Combine(source, "Users", "Alice", "Desktop");
        Directory.CreateDirectory(desktop);
        await File.WriteAllTextAsync(Path.Combine(source, "Users", "Alice", "NTUSER.DAT"), "hive");
        await File.WriteAllTextAsync(Path.Combine(source, "Users", "Alice", "vault.kdbx"), "keepass");
        string oldFile = Path.Combine(desktop, "old.txt");
        string newFile = Path.Combine(desktop, "new.txt");
        await File.WriteAllTextAsync(oldFile, "old");
        await File.WriteAllTextAsync(newFile, "new");
        File.SetLastWriteTimeUtc(oldFile, new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newFile, new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc));
        context.ViewModel.SelectedSourcePath = source;
        await context.ViewModel.ScanCommand.ExecuteAsync(null);
        ExpandDirectories(context);
        ExpandDirectories(context);
        context.ViewModel.Expand(FindRow(context, "Alice"));
        context.ViewModel.SelectedNode = FindRow(context, "vault.kdbx");
        Assert.Contains("password vault", context.ViewModel.DetailText, StringComparison.OrdinalIgnoreCase);

        context.ViewModel.Expand(FindRow(context, "Desktop"));
        context.ViewModel.SelectedNode = FindRow(context, "Desktop");
        Assert.Contains("Oldest: 3 Jan 2024", context.ViewModel.DetailText, StringComparison.Ordinal);
        Assert.Contains("Newest: 11 Sep 2026", context.ViewModel.DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Space_TogglesFolderExpandAndCollapse()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        string source = Path.Combine(context.Root, "Windows.old");
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice", "Desktop"));
        await File.WriteAllTextAsync(Path.Combine(source, "Users", "Alice", "NTUSER.DAT"), "hive");
        await File.WriteAllTextAsync(Path.Combine(source, "Users", "Alice", "Desktop", "notes.txt"), "keep");
        context.ViewModel.SelectedSourcePath = source;
        await context.ViewModel.ScanCommand.ExecuteAsync(null);
        ExpandDirectories(context);
        ExpandDirectories(context);
        TreeNodeRow alice = FindRow(context, "Alice");
        context.ViewModel.SelectedNode = alice;
        Assert.True(context.ViewModel.ApplyKeyboard("Space"));
        Assert.Contains(context.ViewModel.TreeRows, row => row.Name == "Desktop");
        context.ViewModel.SelectedNode = FindRow(context, "Alice");
        Assert.True(context.ViewModel.ApplyKeyboard("Space"));
        Assert.DoesNotContain(context.ViewModel.TreeRows, row => row.Name == "Desktop");
    }

    [Fact]
    public async Task PreparePreview_ListsCannotRestoreAndUndecidedItems()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        string source = Path.Combine(context.Root, "Windows.old");
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice", "Desktop"));
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "NTUSER.DAT"),
            "hive");
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "Desktop", "notes.txt"),
            "keep");
        context.ViewModel.SelectedSourcePath = source;
        context.ViewModel.DestinationRoot = Path.Combine(context.Root, "Recovered");
        await context.ViewModel.ScanCommand.ExecuteAsync(null);
        await context.ViewModel.PreparePreviewAsync();

        Assert.Contains("Cannot be restored", context.ViewModel.PreviewSummaryText, StringComparison.Ordinal);
        Assert.Contains("Undecided items remaining", context.ViewModel.PreviewSummaryText, StringComparison.Ordinal);
        Assert.Contains("read-only dry run", context.ViewModel.PreviewSummaryText, StringComparison.Ordinal);
        Assert.True(context.ViewModel.ReviewUndecidedCommand.CanExecute(null));
        context.ViewModel.ReviewUndecidedCommand.Execute(null);
        Assert.Equal(WorkflowStep.Decide, context.ViewModel.CurrentStep);
        Assert.Equal(FilesViewMode.Unknown, context.ViewModel.FilesViewMode);
    }

    [Fact]
    public async Task SecondScan_ReplacesTheTreeAndRelocksPurge()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        string source = Path.Combine(context.Root, "Windows.old");
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice", "Desktop"));
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "NTUSER.DAT"),
            "hive");
        context.ViewModel.SelectedSourcePath = source;
        await context.ViewModel.ScanCommand.ExecuteAsync(null);
        int firstCount = context.ViewModel.TreeRows.Count;
        context.ViewModel.UnlockPurgeForTests();
        Assert.True(context.ViewModel.CanGoTo(WorkflowStep.Purge));

        await context.ViewModel.ScanCommand.ExecuteAsync(null);

        Assert.Equal(firstCount, context.ViewModel.TreeRows.Count);
        Assert.False(context.ViewModel.CanGoTo(WorkflowStep.Purge));
        Assert.False(context.ViewModel.VerifyCompleted);
        Assert.Equal("Windows.old untouched", context.ViewModel.SourceIntegrityText);
    }

    [Fact]
    public async Task PurgeStep_ShowsUndecidedWarningAndSessionFolder()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        string source = Path.Combine(context.Root, "Windows.old");
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice", "Desktop"));
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "NTUSER.DAT"),
            "hive");
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "Desktop", "notes.txt"),
            "keep");
        context.ViewModel.SelectedSourcePath = source;
        await context.ViewModel.ScanCommand.ExecuteAsync(null);
        context.ViewModel.UnlockPurgeForTests();
        context.ViewModel.CurrentStep = WorkflowStep.Purge;

        Assert.Contains("Undecided", context.ViewModel.PurgeSummaryText, StringComparison.Ordinal);
        Assert.Contains("deleted with Windows.old", context.ViewModel.PurgeSummaryText, StringComparison.Ordinal);
        Assert.Contains("Free space after purge", context.ViewModel.PurgeSummaryText, StringComparison.Ordinal);
        Assert.Contains(context.WorkspaceRoot, context.ViewModel.PurgeSummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PurgeStep_RadiosAndCancelMatchUx()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        Assert.True(context.ViewModel.PreferCleanupHandler);
        Assert.False(context.ViewModel.PreferManualDelete);
        Assert.False(context.ViewModel.CancelPurgeCommand.CanExecute(null));

        context.ViewModel.PreferManualDelete = true;
        Assert.False(context.ViewModel.PreferCleanupHandler);
        Assert.True(context.ViewModel.PreferManualDelete);

        context.ViewModel.UnlockPurgeForTests();
        context.ViewModel.ArmPurgeForTests();
        Assert.True(context.ViewModel.IsPurging);
        Assert.True(context.ViewModel.CancelPurgeCommand.CanExecute(null));
        Assert.False(context.ViewModel.ExecutePurgeCommand.CanExecute(null));
        Assert.False(context.ViewModel.CanGoTo(WorkflowStep.Scan));
    }

    [Fact]
    public async Task PurgedSession_BecomesReadOnlyAndBlocksScan()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        string source = Path.Combine(context.Root, "Windows.old");
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice", "Desktop"));
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "NTUSER.DAT"),
            "hive");
        context.ViewModel.SelectedSourcePath = source;
        await context.ViewModel.ScanCommand.ExecuteAsync(null);
        Assert.True(context.ViewModel.PreparePreviewCommand.CanExecute(null));
        await context.Database.SetKvAsync(
            context.SessionId,
            SessionLock.PurgedKvKey,
            SessionLock.PurgedValue);

        context.ViewModel.RefreshSessionLockForTests();

        Assert.True(context.ViewModel.SessionReadOnly);
        Assert.Equal("Windows.old removed", context.ViewModel.SourceIntegrityText);
        Assert.False(context.ViewModel.ScanCommand.CanExecute(null));
        Assert.False(context.ViewModel.PreparePreviewCommand.CanExecute(null));
        context.ViewModel.UnlockPurgeForTests();
        Assert.False(context.ViewModel.ExecutePurgeCommand.CanExecute(null));
    }

    [Fact]
    public async Task AcknowledgeVerify_WithTypedReason_SettlesFailedJobs()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        string source = Path.Combine(context.Root, "Windows.old");
        string destination = Path.Combine(context.Root, "Recovered");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        string from = Path.Combine(source, "note.txt");
        await File.WriteAllTextAsync(from, "keep");
        PlanItem item = new(
            context.SessionId,
            1,
            PlanOperation.CopyFile,
            from,
            Path.Combine(destination, "note.txt"),
            1,
            ConflictPolicy.KeepBoth,
            OverwriteApproved: false,
            RecipeId: null);
        IReadOnlyList<PlanItem> stored = await context.Database.ReplacePlanItemsAsync(context.SessionId, [item]);
        await context.Database.InsertVerifyResultsAsync(
        [
            new VerifyResultRow(
                stored[0].Id!.Value,
                "report-fail",
                0,
                false,
                "missing",
                DateTimeOffset.UtcNow),
        ]);
        context.ViewModel.MarkRestoreCompletedForTests();
        context.ViewModel.VerifyAckReason = "short";
        Assert.False(context.ViewModel.AcknowledgeVerifyCommand.CanExecute(null));

        context.ViewModel.VerifyAckReason = "checked restored copies";
        Assert.True(context.ViewModel.AcknowledgeVerifyCommand.CanExecute(null));
        await context.ViewModel.AcknowledgeVerifyCommand.ExecuteAsync(null);

        Assert.True(context.ViewModel.VerifyCompleted);
        Assert.True(context.Database.LastVerifyJobsSettled(context.SessionId));
        Assert.False(context.Database.LastVerifyReportAllOk(context.SessionId));
    }

    [Fact]
    public async Task SearchNow_SwitchesToSearchView()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        string source = Path.Combine(context.Root, "Windows.old");
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice", "Desktop"));
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "NTUSER.DAT"),
            "hive");
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "Desktop", "notes.txt"),
            "keep");
        context.ViewModel.SelectedSourcePath = source;
        await context.ViewModel.ScanCommand.ExecuteAsync(null);

        context.ViewModel.SearchText = "notes";
        context.ViewModel.SearchNow();

        Assert.Equal(FilesViewMode.Search, context.ViewModel.FilesViewMode);
        Assert.Contains(context.ViewModel.TreeRows, row => row.Name == "notes.txt");
    }

    [Fact]
    public async Task FilesViewRadios_SwitchModesWithoutAComboBox()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        Assert.True(context.ViewModel.IsTreeView);
        context.ViewModel.IsProblemsView = true;
        Assert.Equal(FilesViewMode.Problems, context.ViewModel.FilesViewMode);
        Assert.False(context.ViewModel.IsTreeView);
        context.ViewModel.IsLargestView = true;
        Assert.Equal(FilesViewMode.Largest, context.ViewModel.FilesViewMode);
        context.ViewModel.IsTreeView = true;
        Assert.True(context.ViewModel.IsTreeView);
    }

    [Fact]
    public async Task RevealInTree_SelectsTheSearchHitUnderItsParents()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        string source = Path.Combine(context.Root, "Windows.old");
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice", "Desktop"));
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "NTUSER.DAT"),
            "hive");
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "Desktop", "notes.txt"),
            "keep");
        context.ViewModel.SelectedSourcePath = source;
        await context.ViewModel.ScanCommand.ExecuteAsync(null);

        context.ViewModel.SearchText = "notes";
        context.ViewModel.SearchNow();
        context.ViewModel.SelectedNode = context.ViewModel.TreeRows.First(row => row.Name == "notes.txt");
        context.ViewModel.RevealInTree();

        Assert.Equal(FilesViewMode.Tree, context.ViewModel.FilesViewMode);
        Assert.Equal("notes.txt", context.ViewModel.SelectedNode?.Name);
        Assert.Contains(context.ViewModel.TreeRows, row => row.Name == "Desktop");
    }

    [Fact]
    public async Task RecentDays_ReloadsRecentFiles()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        string source = Path.Combine(context.Root, "Windows.old");
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice", "Desktop"));
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "NTUSER.DAT"),
            "hive");
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "Desktop", "notes.txt"),
            "keep");
        context.ViewModel.SelectedSourcePath = source;
        await context.ViewModel.ScanCommand.ExecuteAsync(null);

        context.ViewModel.FilesViewMode = FilesViewMode.Recent;
        context.ViewModel.RecentDays = 7;
        Assert.Equal(7, context.ViewModel.RecentDays);
        Assert.Contains(context.ViewModel.TreeRows, row => row.Name == "notes.txt");
        context.ViewModel.RecentDays = 15;
        Assert.Equal(7, context.ViewModel.RecentDays);
    }

    [Fact]
    public async Task Expand_RemovesNestedRowsWhenTheParentIsCollapsed()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        string source = Path.Combine(context.Root, "Windows.old");
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice", "Desktop"));
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "NTUSER.DAT"),
            "hive");
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "Desktop", "notes.txt"),
            "keep");
        context.ViewModel.SelectedSourcePath = source;
        await context.ViewModel.ScanCommand.ExecuteAsync(null);
        context.ViewModel.FilesViewMode = FilesViewMode.Tree;
        ExpandDirectories(context);
        ExpandDirectories(context);

        TreeNodeRow alice = FindRow(context, "Alice");
        context.ViewModel.Expand(alice);
        context.ViewModel.Expand(FindRow(context, "Desktop"));
        Assert.Contains(context.ViewModel.TreeRows, row => row.Name == "notes.txt");

        context.ViewModel.Expand(FindRow(context, "Alice"));
        Assert.DoesNotContain(context.ViewModel.TreeRows, row => row.Name == "notes.txt");
        Assert.Contains(context.ViewModel.TreeRows, row => row.Name == "Desktop");
    }

    [Fact]
    public async Task CanGoTo_BlocksEveryStepWhileRestoreRuns()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        context.ViewModel.SetRestoringForTests(true);
        Assert.False(context.ViewModel.CanGoTo(WorkflowStep.Scan));
        Assert.False(context.ViewModel.CanGoTo(WorkflowStep.Decide));
        context.ViewModel.SetRestoringForTests(false);
        Assert.True(context.ViewModel.CanGoTo(WorkflowStep.Scan));
    }

    [Fact]
    public async Task RestoreDecision_KeepsExpandedTreeAndUpdatesTheLabel()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        string source = Path.Combine(context.Root, "Windows.old");
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice", "Desktop"));
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "NTUSER.DAT"),
            "hive");
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "Desktop", "notes.txt"),
            "keep");
        context.ViewModel.SelectedSourcePath = source;
        await context.ViewModel.ScanCommand.ExecuteAsync(null);
        context.ViewModel.FilesViewMode = FilesViewMode.Tree;
        ExpandDirectories(context);
        ExpandDirectories(context);
        context.ViewModel.Expand(FindRow(context, "Alice"));
        context.ViewModel.Expand(FindRow(context, "Desktop"));

        context.ViewModel.SelectedNode = FindRow(context, "notes.txt");
        await context.ViewModel.RestoreCommand.ExecuteAsync(null);

        Assert.Contains(context.ViewModel.TreeRows, row => row.Name == "notes.txt");
        Assert.Equal(Decision.Restore, context.ViewModel.SelectedNode?.EffectiveDecision);
        Assert.Contains("Restore", context.ViewModel.SelectedNode?.DecisionLabel, StringComparison.Ordinal);

        Assert.True(context.ViewModel.UndoDecisionCommand.CanExecute(null));
        await context.ViewModel.UndoDecisionCommand.ExecuteAsync(null);
        Assert.Equal(Decision.Undecided, FindRow(context, "notes.txt").EffectiveDecision);
    }

    [Fact]
    public async Task ReplaceSelection_RestoresEverySelectedNode()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        string source = Path.Combine(context.Root, "Windows.old");
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice", "Desktop"));
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "NTUSER.DAT"),
            "hive");
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "Desktop", "a.txt"),
            "a");
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "Desktop", "b.txt"),
            "b");
        context.ViewModel.SelectedSourcePath = source;
        await context.ViewModel.ScanCommand.ExecuteAsync(null);
        context.ViewModel.FilesViewMode = FilesViewMode.Tree;
        ExpandDirectories(context);
        ExpandDirectories(context);
        context.ViewModel.Expand(FindRow(context, "Alice"));
        context.ViewModel.Expand(FindRow(context, "Desktop"));

        TreeNodeRow a = FindRow(context, "a.txt");
        TreeNodeRow b = FindRow(context, "b.txt");
        context.ViewModel.ReplaceSelection([a, b]);
        await context.ViewModel.RestoreCommand.ExecuteAsync(null);

        Assert.Equal(Decision.Restore, FindRow(context, "a.txt").EffectiveDecision);
        Assert.Equal(Decision.Restore, FindRow(context, "b.txt").EffectiveDecision);
    }

    [Fact]
    public void TickingAConflict_UpdatesTheOverwriteButton()
    {
        bool notified = false;
        ConflictRow row = new(@"C:\Recovered\a.txt", 1, DateTimeOffset.UtcNow, () => notified = true);
        row.Approved = true;
        Assert.True(notified);
    }

    [Fact]
    public async Task HashDuringScan_StoresSha256ForSmallFiles()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        string source = Path.Combine(context.Root, "Windows.old");
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice", "Desktop"));
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "NTUSER.DAT"),
            "hive");
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "Desktop", "notes.txt"),
            "abc");
        context.ViewModel.SelectedSourcePath = source;
        context.ViewModel.HashDuringScan = true;
        await context.ViewModel.ScanCommand.ExecuteAsync(null);

        Assert.Contains("Hashed", context.ViewModel.ScanStatus, StringComparison.Ordinal);
        Assert.Contains(
            context.ViewModel.TreeRows,
            row => row.Name.Length > 0);
    }

    [Fact]
    public async Task ComputeFolderSizesOff_LeavesFolderAggregatesZero()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        string source = Path.Combine(context.Root, "Windows.old");
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice", "Desktop"));
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "NTUSER.DAT"),
            "hive");
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "Desktop", "notes.txt"),
            "abc");
        context.ViewModel.SelectedSourcePath = source;
        Assert.True(context.ViewModel.ComputeFolderSizes);
        context.ViewModel.ComputeFolderSizes = false;
        await context.ViewModel.ScanCommand.ExecuteAsync(null);

        ExpandDirectories(context);
        ExpandDirectories(context);
        TreeNodeRow alice = FindRow(context, "Alice");
        Assert.Equal(0, alice.AggSize);
        Assert.Equal(0, alice.AggFiles);
        Assert.Equal("0", context.Database.GetKv(context.SessionId, "scan.computeFolderSizes"));
    }

    [Fact]
    public void ApplyKeyboard_DoesNotRestoreReparsePoints()
    {
        TreeNodeRow junction = new(
            1,
            null,
            "Application Data",
            "Application Data",
            NodeKind.Junction,
            0,
            0,
            0,
            null,
            NodeProblem.None,
            Decision.Undecided,
            false,
            false,
            0,
            []);
        Assert.False(junction.CanRestore);
        Assert.Equal("⊘ Application Data", junction.DisplayName);
        Assert.Equal("—", junction.DecisionLabel);
    }

    [Fact]
    public async Task SkipHint_ExplainsEachSkipCategory()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        Assert.Contains("never followed", context.ViewModel.SkipHint, StringComparison.Ordinal);
        Assert.Contains("OneDrive", context.ViewModel.SkipHint, StringComparison.Ordinal);
        Assert.Contains("EFS", context.ViewModel.SkipHint, StringComparison.Ordinal);
        Assert.Contains("Access denied", context.ViewModel.SkipHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenFolder_UsesExplorerSelectThroughProcessRunner()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        string source = Path.Combine(context.Root, "Windows.old");
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice", "Desktop"));
        await File.WriteAllTextAsync(Path.Combine(source, "Users", "Alice", "NTUSER.DAT"), "hive");
        context.ViewModel.SelectedSourcePath = source;
        await context.ViewModel.ScanCommand.ExecuteAsync(null);
        context.ViewModel.SelectedNode = context.ViewModel.TreeRows.First();

        await context.ViewModel.OpenFolderCommand.ExecuteAsync(null);

        ProcessRequest request = Assert.Single(context.Runner.Requests);
        Assert.Equal("explorer.exe", request.FileName);
        Assert.StartsWith("/select,", Assert.Single(request.Arguments), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenFolder_OnARecipeCard_SelectsTheSourceFactPath()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync(RecipeCatalog.All);
        string source = Path.Combine(context.Root, "Windows.old");
        string ssh = Path.Combine(source, "Users", "Alice", ".ssh");
        Directory.CreateDirectory(ssh);
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "NTUSER.DAT"),
            "hive");
        await File.WriteAllTextAsync(Path.Combine(ssh, "id_ed25519.pub"), "ssh-ed25519 FIXTURE");
        context.ViewModel.SelectedSourcePath = source;
        await context.ViewModel.ScanCommand.ExecuteAsync(null);
        context.ViewModel.SelectedCard = context.ViewModel.Cards.First(
            card => card.Kind == "ssh");
        context.Runner.Requests.Clear();

        Assert.True(context.ViewModel.OpenFolderCommand.CanExecute(null));
        await context.ViewModel.OpenFolderCommand.ExecuteAsync(null);

        ProcessRequest request = Assert.Single(context.Runner.Requests);
        Assert.Equal("explorer.exe", request.FileName);
        Assert.Contains(".ssh", Assert.Single(request.Arguments), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OverviewCardVerbs_InspectOpenRestoreOnPersonalFolderAndRecipe()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync(RecipeCatalog.All);
        string source = Path.Combine(context.Root, "Windows.old");
        string ssh = Path.Combine(source, "Users", "Alice", ".ssh");
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice", "Desktop"));
        Directory.CreateDirectory(ssh);
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "NTUSER.DAT"),
            "hive");
        await File.WriteAllTextAsync(Path.Combine(ssh, "id_ed25519.pub"), "ssh-ed25519 FIXTURE");
        context.ViewModel.SelectedSourcePath = source;
        await context.ViewModel.ScanCommand.ExecuteAsync(null);

        OverviewCard desktop = context.ViewModel.Cards.First(
            card => card.Kind == "PersonalFolder" && card.Title == "Desktop");
        OverviewCard absent = context.ViewModel.Cards.First(card => card.Kind == "Absent");
        OverviewCard sshCard = context.ViewModel.Cards.First(card => card.Kind == "ssh");
        Assert.True(desktop.ShowVerbs);
        Assert.False(absent.ShowVerbs);
        Assert.True(sshCard.ShowVerbs);
        Assert.False(context.ViewModel.InspectOverviewCardCommand.CanExecute(absent));

        context.ViewModel.SetWindowWidth(1100);
        context.ViewModel.InspectOverviewCardCommand.Execute(desktop);
        Assert.Equal("Desktop", context.ViewModel.SelectedCard?.Title);
        Assert.Equal(DecidePane.Cards, context.ViewModel.DecidePane);
        Assert.True(context.ViewModel.CompactInspect);
        Assert.NotNull(context.ViewModel.SelectedNode);

        await context.ViewModel.LeaveOverviewCardCommand.ExecuteAsync(desktop);
        Assert.Equal(Decision.LeaveBehind, context.ViewModel.SelectedNode?.EffectiveDecision);

        await context.ViewModel.RestoreOverviewCardCommand.ExecuteAsync(desktop);
        Assert.Equal(Decision.Restore, context.ViewModel.SelectedNode?.EffectiveDecision);
        Assert.True(context.ViewModel.SelectedNode?.HasOwnUserDecision);
        Assert.Contains(
            context.ViewModel.Cards,
            card => card.Kind == "PersonalFolder" &&
                card.Title == "Desktop" &&
                card.DecisionLabel.Contains("Restore", StringComparison.Ordinal));
        Assert.Contains(
            context.ViewModel.Cards,
            card => card.Kind == "PersonalFolder" &&
                card.Title == "Desktop" &&
                card.DecisionLabel.Contains("●", StringComparison.Ordinal));
        OverviewCard kept = context.ViewModel.Cards.First(
            card => card.Kind == "PersonalFolder" && card.Title == "Desktop");
        Assert.Same(kept, context.ViewModel.SelectedCard);

        context.Runner.Requests.Clear();
        await context.ViewModel.OpenOverviewCardCommand.ExecuteAsync(sshCard);
        Assert.Equal("ssh", context.ViewModel.SelectedCard?.Kind);
        ProcessRequest request = Assert.Single(context.Runner.Requests);
        Assert.Equal("explorer.exe", request.FileName);
        Assert.Contains(".ssh", Assert.Single(request.Arguments), StringComparison.OrdinalIgnoreCase);

        await context.ViewModel.LeaveOverviewCardCommand.ExecuteAsync(sshCard);
        Assert.Contains(
            context.ViewModel.RecipeComponents,
            component => component.Decision == Decision.LeaveBehind);
    }

    [Fact]
    public async Task Inspect_OnARecipeCard_ShowsTheSixQuestionPane()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync(RecipeCatalog.All);
        string source = Path.Combine(context.Root, "Windows.old");
        string ssh = Path.Combine(source, "Users", "Alice", ".ssh");
        Directory.CreateDirectory(ssh);
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "NTUSER.DAT"),
            "hive");
        await File.WriteAllTextAsync(Path.Combine(ssh, "id_ed25519.pub"), "ssh-ed25519 FIXTURE");
        context.ViewModel.SelectedSourcePath = source;
        await context.ViewModel.ScanCommand.ExecuteAsync(null);
        context.ViewModel.SelectedCard = context.ViewModel.Cards.First(
            card => card.Kind == "ssh");

        Assert.True(context.ViewModel.RecipeCardSelected);
        context.ViewModel.ShowInspectCommand.Execute(null);

        Assert.True(context.ViewModel.CompactInspect);
        Assert.NotNull(context.ViewModel.SelectedRecipeCard);
        Assert.False(string.IsNullOrWhiteSpace(context.ViewModel.SelectedRecipeCard.What));
        Assert.False(string.IsNullOrWhiteSpace(context.ViewModel.SelectedRecipeCard.WhyItMatters));
        Assert.False(string.IsNullOrWhiteSpace(context.ViewModel.SelectedRecipeCard.WhatIsRestored));
        Assert.False(string.IsNullOrWhiteSpace(context.ViewModel.SelectedRecipeCard.IfLeftBehind));
    }

    [Fact]
    public async Task Scan_MissingFolder_SurfacesRedactedExplanationAndLogPath()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        context.ViewModel.SelectedSourcePath = Path.Combine(context.Root, "missing-Windows.old");
        await context.ViewModel.ScanCommand.ExecuteAsync(null);

        Assert.False(context.ViewModel.ScanCompleted);
        Assert.Contains("Windows.old was not modified", context.ViewModel.ScanStatus, StringComparison.Ordinal);
        Assert.Contains("log.txt", context.ViewModel.ScanStatus, StringComparison.Ordinal);
        Assert.Contains("was not found", context.ViewModel.ScanStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FirstRun_StaysUntilDismissedAndHelpLoadsLocalMarkdown()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        Assert.True(context.ViewModel.FirstRunVisible);
        Assert.Contains("six steps", context.ViewModel.FirstRunBody, StringComparison.OrdinalIgnoreCase);
        context.ViewModel.DismissFirstRunCommand.Execute(null);
        Assert.False(context.ViewModel.FirstRunVisible);
        Assert.True(
            File.Exists(Path.Combine(context.Root, "WinOldRecovery", "first-run.dismissed")));

        context.ViewModel.OpenHelp();
        Assert.True(context.ViewModel.HelpVisible);
        Assert.Contains("Chrome", context.ViewModel.HelpText, StringComparison.OrdinalIgnoreCase);

        context.ViewModel.ShowLogCommand.Execute(null);
        Assert.True(context.ViewModel.LogVisible);
        Assert.DoesNotContain("WINOLD_RECOVERY_CANARY_DO_NOT_LOG_7F3A91", context.ViewModel.LogText, StringComparison.Ordinal);

        context.ViewModel.SetWindowWidth(1100);
        Assert.True(context.ViewModel.CompactLayout);
        context.ViewModel.ShowInspectCommand.Execute(null);
        Assert.True(context.ViewModel.CompactInspect);
        context.ViewModel.SetWindowWidth(1400);
        Assert.False(context.ViewModel.CompactLayout);
        Assert.False(context.ViewModel.CompactInspect);
    }

    [Fact]
    public async Task BrowseSource_AddsInspectedFolderAndWarnsWithoutUsers()
    {
        await using ShellTestContext context = await ShellTestContext.CreateAsync();
        string browsed = Path.Combine(context.Root, "not-windows-old");
        Directory.CreateDirectory(browsed);
        context.Picker.Folder = browsed;

        await context.ViewModel.BrowseSourceCommand.ExecuteAsync(null);

        Assert.Contains("not-windows-old", context.ViewModel.SelectedSourcePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not contain Users", context.ViewModel.SourceHint, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Scan", context.ViewModel.ScanButtonLabel);
    }

    private static void ExpandDirectories(ShellTestContext context)
    {
        foreach (TreeNodeRow row in context.ViewModel.TreeRows.ToList())
        {
            if (row.ChildCount > 0)
            {
                context.ViewModel.Expand(row);
            }
        }
    }

    private static TreeNodeRow FindRow(ShellTestContext context, string name)
    {
        return context.ViewModel.TreeRows.First(row => row.Name == name);
    }

    private sealed class ShellTestContext : IAsyncDisposable
    {
        private ShellTestContext(
            string root,
            SessionDb database,
            RecordingProcessRunner runner,
            ShellViewModel viewModel,
            StubFolderPicker picker,
            SessionWorkspace workspace)
        {
            Root = root;
            Database = database;
            Runner = runner;
            ViewModel = viewModel;
            Picker = picker;
            Workspace = workspace;
        }

        public string Root { get; }
        public SessionDb Database { get; }
        public RecordingProcessRunner Runner { get; }
        public ShellViewModel ViewModel { get; }
        public StubFolderPicker Picker { get; }
        public SessionWorkspace Workspace { get; }
        public string SessionId => Workspace.SessionId;
        public string WorkspaceRoot => Workspace.RootPath;

        public static async Task<ShellTestContext> CreateAsync(IReadOnlyList<IRecipe>? recipes = null)
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                $"WinOldRecovery-Shell-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            RecordingProcessRunner runner = new();
            SourceGuard sourceGuard = new();
            SafeFs sharedFs = new(sourceGuard);
            SessionWorkspace workspace = SessionWorkspace.Create(sharedFs, root);
            SessionDb database = await SessionDb.OpenAsync(workspace.DatabasePath, sharedFs);
            await database.CreateSessionAsync(
                new SessionRecord(workspace.SessionId, DateTimeOffset.UtcNow, "Created", "0.1.0"));
            SourceDiscovery discovery = new(new StubVolumes([]), runner);
            string helpRoot = Path.Combine(root, "help");
            Directory.CreateDirectory(helpRoot);
            string bundled = Path.Combine(AppContext.BaseDirectory, "help", "limitations.md");
            File.WriteAllText(
                Path.Combine(helpRoot, "limitations.md"),
                File.Exists(bundled)
                    ? File.ReadAllText(bundled)
                    : "# Limits\n\nChrome and Edge passwords cannot be recovered.\n");
            StubFolderPicker picker = new();
            ShellViewModel viewModel = new(
                database,
                workspace,
                discovery,
                new ScanOrchestrator(database, sharedFs, sourceGuard),
                runner,
                sharedFs,
                sourceGuard,
                recipes: recipes,
                firstRunState: null,
                localHelp: new LocalHelp(helpRoot),
                folderPicker: picker,
                processPresence: new NeverRunningProcessPresence());
            return new ShellTestContext(root, database, runner, viewModel, picker, workspace);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class StubFolderPicker : IFolderPicker
    {
        public string? Folder { get; set; }

        public string? PickFolder() => Folder;
    }

    private sealed class StubVolumes(IReadOnlyList<string> roots) : IVolumeRootProvider
    {
        public IReadOnlyList<string> GetFixedVolumeRoots() => roots;
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public Task<ProcessResult> RunAsync(
            ProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }
}
