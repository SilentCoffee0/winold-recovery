using System.IO;
using System.Linq;
using WinOldRecovery.App;
using WinOldRecovery.App.Help;
using WinOldRecovery.App.ViewModels;
using WinOldRecovery.Core.Browse;
using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Processes;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Core.Scan;
using WinOldRecovery.Core.Sessions;

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

        context.ViewModel.BrowseSourceCommand.Execute(null);

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
            StubFolderPicker picker)
        {
            Root = root;
            Database = database;
            Runner = runner;
            ViewModel = viewModel;
            Picker = picker;
        }

        public string Root { get; }
        public SessionDb Database { get; }
        public RecordingProcessRunner Runner { get; }
        public ShellViewModel ViewModel { get; }
        public StubFolderPicker Picker { get; }

        public static async Task<ShellTestContext> CreateAsync()
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
                recipes: null,
                firstRunState: null,
                localHelp: new LocalHelp(helpRoot),
                folderPicker: picker);
            return new ShellTestContext(root, database, runner, viewModel, picker);
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
