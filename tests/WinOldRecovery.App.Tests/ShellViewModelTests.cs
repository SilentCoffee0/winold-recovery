using System.IO;
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
        Assert.Equal("Windows.old untouched", context.ViewModel.SourceIntegrityText);
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

    private sealed class ShellTestContext : IAsyncDisposable
    {
        private ShellTestContext(
            string root,
            SessionDb database,
            RecordingProcessRunner runner,
            ShellViewModel viewModel)
        {
            Root = root;
            Database = database;
            Runner = runner;
            ViewModel = viewModel;
        }

        public string Root { get; }
        public SessionDb Database { get; }
        public RecordingProcessRunner Runner { get; }
        public ShellViewModel ViewModel { get; }

        public static async Task<ShellTestContext> CreateAsync()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                $"WinOldRecovery-Shell-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            SafeFs safeFs = new(new SourceGuard());
            SessionWorkspace workspace = SessionWorkspace.Create(safeFs, root);
            SessionDb database = await SessionDb.OpenAsync(workspace.DatabasePath, safeFs);
            await database.CreateSessionAsync(
                new SessionRecord(workspace.SessionId, DateTimeOffset.UtcNow, "Created", "0.1.0"));
            RecordingProcessRunner runner = new();
            SourceDiscovery discovery = new(new StubVolumes([]), runner);
            ShellViewModel viewModel = new(
                database,
                workspace,
                discovery,
                new ScanOrchestrator(database, safeFs, new SourceGuard()),
                runner);
            return new ShellTestContext(root, database, runner, viewModel);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
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
