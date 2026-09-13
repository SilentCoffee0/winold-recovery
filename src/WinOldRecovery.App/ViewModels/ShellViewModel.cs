using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOldRecovery.Core.Browse;
using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Processes;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Core.Scan;
using WinOldRecovery.Core.Sessions;

namespace WinOldRecovery.App.ViewModels;

public enum WorkflowStep
{
    Scan = 1,
    Decide = 2,
    Preview = 3,
    Restore = 4,
    Verify = 5,
    Purge = 6,
}

public sealed class ShellViewModel : ObservableObject
{
    private readonly SessionWorkspace workspace;
    private readonly SourceDiscovery sourceDiscovery;
    private readonly ScanOrchestrator scanOrchestrator;
    private readonly IProcessRunner processRunner;
    private readonly DecisionEngine decisionEngine;
    private readonly NodeBrowser nodeBrowser;
    private CancellationTokenSource? scanCancellation;
    private WorkflowStep currentStep = WorkflowStep.Scan;
    private bool scanCompleted;
    private string? selectedSourcePath;
    private string scanStatus = "Choose a Windows.old folder, then scan.";
    private string currentPath = string.Empty;
    private int nodesVisited;
    private TreeNodeRow? selectedNode;
    private FilesViewMode filesViewMode = FilesViewMode.Tree;
    private string searchText = string.Empty;

    public ShellViewModel(
        SessionDb sessionDb,
        SessionWorkspace workspace,
        SourceDiscovery sourceDiscovery,
        ScanOrchestrator scanOrchestrator,
        IProcessRunner processRunner)
    {
        this.workspace = workspace;
        this.sourceDiscovery = sourceDiscovery;
        this.scanOrchestrator = scanOrchestrator;
        this.processRunner = processRunner;
        decisionEngine = new DecisionEngine(sessionDb, workspace.SessionId);
        nodeBrowser = new NodeBrowser(sessionDb, workspace.SessionId);
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsScanning && SelectedSourcePath is not null);
        CancelScanCommand = new RelayCommand(CancelScan, () => IsScanning);
        RestoreCommand = new AsyncRelayCommand(() => ApplyDecisionAsync(Decision.Restore), CanMutateSelection);
        LeaveBehindCommand = new AsyncRelayCommand(() => ApplyDecisionAsync(Decision.LeaveBehind), CanMutateSelection);
        UndecidedCommand = new AsyncRelayCommand(ClearDecisionAsync, CanMutateSelection);
        OpenFolderCommand = new AsyncRelayCommand(OpenFolderAsync, () => SelectedNode is not null && SourceRoot is not null);
        ExpandCommand = new RelayCommand<TreeNodeRow>(Expand);
    }

    public IAsyncRelayCommand ScanCommand { get; }
    public IRelayCommand CancelScanCommand { get; }
    public IAsyncRelayCommand RestoreCommand { get; }
    public IAsyncRelayCommand LeaveBehindCommand { get; }
    public IAsyncRelayCommand UndecidedCommand { get; }
    public IAsyncRelayCommand OpenFolderCommand { get; }
    public IRelayCommand<TreeNodeRow> ExpandCommand { get; }

    public ObservableCollection<SourceCandidate> Sources { get; } = [];

    public ObservableCollection<TreeNodeRow> TreeRows { get; } = [];

    public WorkflowStep CurrentStep
    {
        get => currentStep;
        set
        {
            if (!CanGoTo(value))
            {
                return;
            }

            SetProperty(ref currentStep, value);
            OnPropertyChanged(nameof(WindowTitle));
        }
    }

    public bool IsScanning { get; private set; }

    public bool ScanCompleted => scanCompleted;

    public string SourceIntegrityText { get; } = "Windows.old untouched";

    public string SpaceBudgetText { get; } = "Selected: — of destination free space";

    public string ElevationNote { get; } =
        "Files in Windows.old belong to a user account that no longer exists; reading them needs administrator rights.";

    public string ScanStatus
    {
        get => scanStatus;
        private set => SetProperty(ref scanStatus, value);
    }

    public string CurrentPath
    {
        get => currentPath;
        private set => SetProperty(ref currentPath, value);
    }

    public int NodesVisited
    {
        get => nodesVisited;
        private set => SetProperty(ref nodesVisited, value);
    }

    public string? SelectedSourcePath
    {
        get => selectedSourcePath;
        set
        {
            if (SetProperty(ref selectedSourcePath, value))
            {
                ScanCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string? SourceRoot { get; private set; }

    public TreeNodeRow? SelectedNode
    {
        get => selectedNode;
        set
        {
            if (SetProperty(ref selectedNode, value))
            {
                RestoreCommand.NotifyCanExecuteChanged();
                LeaveBehindCommand.NotifyCanExecuteChanged();
                UndecidedCommand.NotifyCanExecuteChanged();
                OpenFolderCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(DetailText));
            }
        }
    }

    public FilesViewMode FilesViewMode
    {
        get => filesViewMode;
        set
        {
            if (SetProperty(ref filesViewMode, value))
            {
                ReloadView();
            }
        }
    }

    public string SearchText
    {
        get => searchText;
        set => SetProperty(ref searchText, value);
    }

    public string WindowTitle =>
        SourceRoot is null
            ? $"WinOld Recovery — {CurrentStep}"
            : $"WinOld Recovery — {SourceRoot} — {CurrentStep}";

    public string DetailText
    {
        get
        {
            if (SelectedNode is null)
            {
                return "Select a file or folder to inspect it.";
            }

            TreeNodeRow node = SelectedNode;
            string destination = SourceRoot is null
                ? node.RelPath
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Recovered",
                    node.RelPath);
            string sensitive = node.RelPath.Contains("AppData", StringComparison.OrdinalIgnoreCase)
                ? "This path may contain secrets. Contents are never displayed or logged."
                : string.Empty;
            return string.Join(
                Environment.NewLine,
                ((string[])
                [
                    "Source: " + (SourceRoot is null ? node.RelPath : Path.Combine(SourceRoot, node.RelPath)),
                    "Planned destination: " + destination,
                    $"Size: {node.AggSize} bytes  Files: {node.AggFiles}",
                    "Modified: " + (node.ModifiedUtc?.ToString("d MMM yyyy") ?? "—"),
                    "Decision: " + node.DecisionLabel,
                    "Problem: " + node.Problem,
                    node.IsReparse ? "This reparse point cannot be restored." : string.Empty,
                    sensitive,
                ]).Where(static line => line.Length > 0));
        }
    }

    public async Task LoadSourcesAsync(CancellationToken cancellationToken = default)
    {
        Sources.Clear();
        foreach (SourceCandidate candidate in await sourceDiscovery.DiscoverAsync(cancellationToken)
                     .ConfigureAwait(false))
        {
            Sources.Add(candidate);
        }

        if (Sources.Count > 0)
        {
            SelectedSourcePath = Sources[0].Path;
        }

        OnPropertyChanged(nameof(Sources));
    }

    public bool CanGoTo(WorkflowStep step)
    {
        if (step <= CurrentStep)
        {
            return true;
        }

        return step switch
        {
            WorkflowStep.Decide => scanCompleted,
            WorkflowStep.Preview => scanCompleted,
            _ => false,
        };
    }

    public bool ApplyKeyboard(string key)
    {
        switch (key)
        {
            case "R":
                if (RestoreCommand.CanExecute(null))
                {
                    RestoreCommand.Execute(null);
                    return true;
                }

                return false;
            case "L":
                if (LeaveBehindCommand.CanExecute(null))
                {
                    LeaveBehindCommand.Execute(null);
                    return true;
                }

                return false;
            case "U":
                if (UndecidedCommand.CanExecute(null))
                {
                    UndecidedCommand.Execute(null);
                    return true;
                }

                return false;
            case "O":
                if (OpenFolderCommand.CanExecute(null))
                {
                    OpenFolderCommand.Execute(null);
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    public void Expand(TreeNodeRow? row)
    {
        if (row is null || FilesViewMode != FilesViewMode.Tree)
        {
            return;
        }

        NodePage page = nodeBrowser.GetChildren(row.Id);
        ReplaceRowsUnder(row, page.Rows);
    }

    public void SearchNow()
    {
        FilesViewMode = FilesViewMode.Search;
        ReloadView();
    }

    private async Task ScanAsync()
    {
        if (SelectedSourcePath is null)
        {
            return;
        }

        IsScanning = true;
        OnPropertyChanged(nameof(IsScanning));
        ScanCommand.NotifyCanExecuteChanged();
        CancelScanCommand.NotifyCanExecuteChanged();
        scanCancellation = new CancellationTokenSource();
        Progress<WalkProgress> progress = new(report =>
        {
            NodesVisited = report.NodesVisited;
            CurrentPath = report.CurrentRelativePath;
            ScanStatus = $"Scanning… {report.NodesVisited} entries";
        });

        try
        {
            ScanRunResult result = await scanOrchestrator.RunAsync(
                    workspace.SessionId,
                    SelectedSourcePath,
                    workspace.TemporaryPath,
                    progress: progress,
                    cancellationToken: scanCancellation.Token)
                .ConfigureAwait(true);
            SourceRoot = result.SourceRoot;
            scanCompleted = true;
            ScanStatus =
                $"Windows.old holds {result.Walk.NodesVisited} entries across {result.Profiles.Count} profiles. Nothing has been changed.";
            CurrentStep = WorkflowStep.Decide;
            ReloadView();
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(ScanCompleted));
        }
        catch (OperationCanceledException)
        {
            ScanStatus = "Scan paused. Partial results were kept.";
        }
        finally
        {
            IsScanning = false;
            OnPropertyChanged(nameof(IsScanning));
            ScanCommand.NotifyCanExecuteChanged();
            CancelScanCommand.NotifyCanExecuteChanged();
        }
    }

    private void CancelScan()
    {
        scanCancellation?.Cancel();
    }

    private bool CanMutateSelection()
    {
        return CurrentStep == WorkflowStep.Decide &&
            SelectedNode is { CanRestore: true };
    }

    private async Task ApplyDecisionAsync(Decision decision)
    {
        if (SelectedNode is null || !SelectedNode.CanRestore)
        {
            return;
        }

        await decisionEngine.SetUserDecisionAsync(SelectedNode.Id, decision).ConfigureAwait(true);
        ReloadView();
    }

    private async Task ClearDecisionAsync()
    {
        if (SelectedNode is null)
        {
            return;
        }

        await decisionEngine.ClearUserDecisionAsync(SelectedNode.Id).ConfigureAwait(true);
        ReloadView();
    }

    private async Task OpenFolderAsync()
    {
        if (SelectedNode is null || SourceRoot is null)
        {
            return;
        }

        string path = Path.Combine(SourceRoot, SelectedNode.RelPath);
        await processRunner.RunAsync(
                new ProcessRequest("explorer.exe", ["/select," + path]))
            .ConfigureAwait(true);
    }

    private void ReloadView()
    {
        TreeRows.Clear();
        NodePage page = FilesViewMode switch
        {
            FilesViewMode.Largest => CombineLargest(),
            FilesViewMode.Recent => nodeBrowser.GetRecent(30, null),
            FilesViewMode.Search => string.IsNullOrWhiteSpace(SearchText)
                ? new NodePage([], 0, false)
                : nodeBrowser.Search(SearchText, null),
            FilesViewMode.Unknown => nodeBrowser.GetUnknown(null),
            FilesViewMode.Problems => nodeBrowser.GetProblems(),
            _ => nodeBrowser.GetChildren(null),
        };

        foreach (TreeNodeRow row in page.Rows)
        {
            TreeRows.Add(row);
        }

        OnPropertyChanged(nameof(TreeRows));
        if (SelectedNode is not null)
        {
            SelectedNode = TreeRows.FirstOrDefault(row => row.Id == SelectedNode.Id) ?? SelectedNode;
        }
    }

    private NodePage CombineLargest()
    {
        List<TreeNodeRow> rows = [];
        rows.AddRange(nodeBrowser.GetLargest(null, folders: true).Rows);
        rows.AddRange(nodeBrowser.GetLargest(null, folders: false).Rows);
        return new NodePage(rows, rows.Count, false);
    }

    private void ReplaceRowsUnder(TreeNodeRow parent, IReadOnlyList<TreeNodeRow> children)
    {
        int index = -1;
        for (int i = 0; i < TreeRows.Count; i++)
        {
            if (TreeRows[i].Id == parent.Id)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        for (int i = TreeRows.Count - 1; i > index; i--)
        {
            if (TreeRows[i].ParentId == parent.Id)
            {
                TreeRows.RemoveAt(i);
            }
        }

        int insertAt = index + 1;
        foreach (TreeNodeRow child in children)
        {
            TreeRows.Insert(insertAt++, child);
        }

        OnPropertyChanged(nameof(TreeRows));
    }
}
