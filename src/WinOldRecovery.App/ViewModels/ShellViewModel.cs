using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOldRecovery.Core.Browse;
using WinOldRecovery.Core.Classification;
using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.Hashing;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Logging;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Planning;
using WinOldRecovery.Core.Processes;
using WinOldRecovery.Core.Purge;
using WinOldRecovery.Core.Recipes;
using WinOldRecovery.Core.Restore;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Core.Scan;
using WinOldRecovery.Core.Sessions;
using WinOldRecovery.Recipes;
using WinOldRecovery.Core.Verify;

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
    private readonly SessionDb sessionDb;
    private readonly SessionWorkspace workspace;
    private readonly SourceDiscovery sourceDiscovery;
    private readonly ScanOrchestrator scanOrchestrator;
    private readonly IProcessRunner processRunner;
    private readonly SafeFs safeFs;
    private readonly SourceGuard sourceGuard;
    private readonly RecipeHost? recipeHost;
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
    private DecidePane decidePane = DecidePane.Cards;
    private bool hashDuringScan;
    private OverviewCard? selectedCard;
    private IReadOnlyList<DetectedProfile> lastProfiles = [];
    private IReadOnlyList<RecipeCard> lastRecipeCards = [];
    private ClassificationSummary? lastClassification;
    private RestorePlan? lastPlan;
    private PreflightResult? lastPreflight;
    private bool restoreCompleted;
    private bool verifyCompleted;
    private bool filesChecked;
    private bool undecidedAcknowledged;
    private bool customRootConfirmed;
    private bool preferCleanupHandler = true;
    private string purgeTypedFolderName = string.Empty;
    private ConflictPolicy selectedConflictPolicy = ConflictPolicy.KeepBoth;
    private string spaceBudgetText = "Selected: — of destination free space";
    private string destinationRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Recovered");

    private static string LiveProfileRoot =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public ShellViewModel(
        SessionDb sessionDb,
        SessionWorkspace workspace,
        SourceDiscovery sourceDiscovery,
        ScanOrchestrator scanOrchestrator,
        IProcessRunner processRunner,
        SafeFs safeFs,
        SourceGuard sourceGuard,
        IReadOnlyList<IRecipe>? recipes = null)
    {
        this.sessionDb = sessionDb;
        this.workspace = workspace;
        this.sourceDiscovery = sourceDiscovery;
        this.scanOrchestrator = scanOrchestrator;
        this.processRunner = processRunner;
        this.safeFs = safeFs;
        this.sourceGuard = sourceGuard;
        recipeHost = recipes is { Count: > 0 }
            ? new RecipeHost(sessionDb, safeFs, processRunner, recipes)
            : null;
        decisionEngine = new DecisionEngine(sessionDb, workspace.SessionId);
        nodeBrowser = new NodeBrowser(sessionDb, workspace.SessionId);
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsScanning && SelectedSourcePath is not null);
        CancelScanCommand = new RelayCommand(CancelScan, () => IsScanning);
        RestoreCommand = new AsyncRelayCommand(() => ApplyDecisionAsync(Decision.Restore), CanMutateSelection);
        LeaveBehindCommand = new AsyncRelayCommand(() => ApplyDecisionAsync(Decision.LeaveBehind), CanMutateSelection);
        UndecidedCommand = new AsyncRelayCommand(ClearDecisionAsync, CanMutateSelection);
        OpenFolderCommand = new AsyncRelayCommand(OpenFolderAsync, () => SelectedNode is not null && SourceRoot is not null);
        ExpandCommand = new RelayCommand<TreeNodeRow>(Expand);
        ShowCardsCommand = new RelayCommand(() => DecidePane = DecidePane.Cards);
        ShowFilesCommand = new RelayCommand(() => DecidePane = DecidePane.Files);
        SearchCommand = new RelayCommand(SearchNow);
        InspectCommand = new RelayCommand(
            () =>
            {
                DecidePane = DecidePane.Files;
                OnPropertyChanged(nameof(DetailText));
            },
            () => SelectedNode is not null);
        CopyPathCommand = new RelayCommand(CopyPath, () => SelectedNode is not null && SourceRoot is not null);
        ExecuteRestoreCommand = new AsyncRelayCommand(ExecuteRestoreAsync, () => lastPlan is not null && lastPreflight is { CanProceed: true } && !IsScanning);
        ExecuteVerifyCommand = new AsyncRelayCommand(ExecuteVerifyAsync, () => restoreCompleted);
        PreparePreviewCommand = new AsyncRelayCommand(PreparePreviewAsync, () => scanCompleted && SourceRoot is not null);
        ApproveOverwritesCommand = new AsyncRelayCommand(ApproveOverwritesAsync, CanApproveOverwrites);
        ExecutePurgeCommand = new AsyncRelayCommand(ExecutePurgeAsync, () => verifyCompleted && !IsScanning);
        CreateSupportBundleCommand = new RelayCommand(CreateSupportBundle, () => verifyCompleted);
    }

    public IAsyncRelayCommand ScanCommand { get; }
    public IRelayCommand CancelScanCommand { get; }
    public IAsyncRelayCommand RestoreCommand { get; }
    public IAsyncRelayCommand LeaveBehindCommand { get; }
    public IAsyncRelayCommand UndecidedCommand { get; }
    public IAsyncRelayCommand OpenFolderCommand { get; }
    public IRelayCommand<TreeNodeRow> ExpandCommand { get; }
    public IRelayCommand ShowCardsCommand { get; }
    public IRelayCommand ShowFilesCommand { get; }
    public IRelayCommand SearchCommand { get; }
    public IRelayCommand InspectCommand { get; }
    public IRelayCommand CopyPathCommand { get; }
    public IAsyncRelayCommand ExecuteRestoreCommand { get; }
    public IAsyncRelayCommand ExecuteVerifyCommand { get; }
    public IAsyncRelayCommand PreparePreviewCommand { get; }
    public IAsyncRelayCommand ApproveOverwritesCommand { get; }
    public IAsyncRelayCommand ExecutePurgeCommand { get; }
    public IRelayCommand CreateSupportBundleCommand { get; }

    public ObservableCollection<SourceCandidate> Sources { get; } = [];

    public ObservableCollection<TreeNodeRow> TreeRows { get; } = [];

    public ObservableCollection<OverviewCard> Cards { get; } = [];

    public ObservableCollection<ConflictRow> Conflicts { get; } = [];

    public IReadOnlyList<ConflictPolicy> ConflictPolicies { get; } =
    [
        ConflictPolicy.KeepBoth,
        ConflictPolicy.Skip,
        ConflictPolicy.OverwriteApproved,
    ];

    public ConflictPolicy SelectedConflictPolicy
    {
        get => selectedConflictPolicy;
        set
        {
            if (SetProperty(ref selectedConflictPolicy, value))
            {
                RefreshPreflight();
                ApproveOverwritesCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string OverwriteButtonLabel =>
        "Overwrite these " + Conflicts.Count(static row => row.Approved) + " files";

    public IReadOnlyList<FilesViewMode> FilesViewModes { get; } =
    [
        FilesViewMode.Tree,
        FilesViewMode.Largest,
        FilesViewMode.Recent,
        FilesViewMode.Search,
        FilesViewMode.Unknown,
        FilesViewMode.Problems,
    ];

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

    public bool RestoreCompleted => restoreCompleted;

    public bool VerifyCompleted => verifyCompleted;

    public bool FilesChecked
    {
        get => filesChecked;
        set => SetProperty(ref filesChecked, value);
    }

    public bool UndecidedAcknowledged
    {
        get => undecidedAcknowledged;
        set => SetProperty(ref undecidedAcknowledged, value);
    }

    public bool CustomRootConfirmed
    {
        get => customRootConfirmed;
        set => SetProperty(ref customRootConfirmed, value);
    }

    public bool PreferCleanupHandler
    {
        get => preferCleanupHandler;
        set => SetProperty(ref preferCleanupHandler, value);
    }

    public string PurgeTypedFolderName
    {
        get => purgeTypedFolderName;
        set => SetProperty(ref purgeTypedFolderName, value);
    }

    public string SourceIntegrityText { get; } = "Windows.old untouched";

    public string SpaceBudgetText
    {
        get => spaceBudgetText;
        private set => SetProperty(ref spaceBudgetText, value);
    }

    public string ElevationNote { get; } =
        "Files in Windows.old belong to a user account that no longer exists; reading them needs administrator rights.";

    public string PromisesText { get; } =
        "We never change Windows.old until you choose to delete it in the last step. We never overwrite your files silently.";

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

    public string DestinationRoot
    {
        get => destinationRoot;
        set => SetProperty(ref destinationRoot, value);
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
                InspectCommand.NotifyCanExecuteChanged();
                CopyPathCommand.NotifyCanExecuteChanged();
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

    public DecidePane DecidePane
    {
        get => decidePane;
        set
        {
            if (SetProperty(ref decidePane, value))
            {
                OnPropertyChanged(nameof(IsCardsPane));
                OnPropertyChanged(nameof(IsFilesPane));
            }
        }
    }

    public bool IsCardsPane => DecidePane == DecidePane.Cards;

    public bool IsFilesPane => DecidePane == DecidePane.Files;

    public bool HashDuringScan
    {
        get => hashDuringScan;
        set => SetProperty(ref hashDuringScan, value);
    }

    public OverviewCard? SelectedCard
    {
        get => selectedCard;
        set
        {
            if (SetProperty(ref selectedCard, value))
            {
                if (value?.NodeId is long nodeId)
                {
                    SelectedNode = nodeBrowser.GetNode(nodeId) ?? SelectedNode;
                }

                OnPropertyChanged(nameof(DetailText));
                OnPropertyChanged(nameof(SelectedRecipeCard));
                OnPropertyChanged(nameof(RecipeCardSelected));
            }
        }
    }

    public string WindowTitle =>
        SourceRoot is null
            ? $"WinOld Recovery — {CurrentStep}"
            : $"WinOld Recovery — {SourceRoot} — {CurrentStep}";

    public RecipeCard? SelectedRecipeCard =>
        SelectedCard is null
            ? null
            : lastRecipeCards.FirstOrDefault(card => card.Title == SelectedCard.Title);

    public bool RecipeCardSelected => SelectedRecipeCard is not null;

    public string DetailText
    {
        get
        {
            if (SelectedRecipeCard is RecipeCard recipe)
            {
                return string.Join(
                    Environment.NewLine,
                    [
                        recipe.Title,
                        "What is this? " + recipe.What,
                        "Why does it matter? " + recipe.WhyItMatters,
                        "What is restored? " + recipe.WhatIsRestored,
                        "Can the cloud restore it? " + recipe.CloudAlternative,
                        "Does it regenerate? " + recipe.Regeneratable,
                        "If left behind: " + recipe.IfLeftBehind,
                        .. recipe.Components.Select(static component =>
                            component.Title + ": " + component.Summary +
                            (component.Fixed ? " (cannot be recovered)" : " — " + component.SuggestedDefault)),
                    ]);
            }

            if (SelectedNode is null)
            {
                return "Select a file, folder, or app card to inspect it.";
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
            WorkflowStep.Restore => lastPlan is not null && lastPreflight is { CanProceed: true },
            WorkflowStep.Verify => restoreCompleted,
            WorkflowStep.Purge => verifyCompleted,
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
            case "I":
                if (InspectCommand.CanExecute(null))
                {
                    InspectCommand.Execute(null);
                    return true;
                }

                return false;
            case "Space":
                if (SelectedNode is not null)
                {
                    Expand(SelectedNode);
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
            if (!safeFs.DirectoryExists(SelectedSourcePath))
            {
                throw new DirectoryNotFoundException(
                    "The chosen Windows.old folder was not found.");
            }

            ScanRunResult result = await scanOrchestrator.RunAsync(
                    workspace.SessionId,
                    SelectedSourcePath,
                    workspace.TemporaryPath,
                    progress: progress,
                    cancellationToken: scanCancellation.Token)
                .ConfigureAwait(true);
            SourceRoot = result.SourceRoot;
            scanCompleted = true;
            if (HashDuringScan)
            {
                FileHashingPass hasher = new(sessionDb, safeFs);
                int hashed = await hasher.HashSessionFilesAsync(
                        workspace.SessionId,
                        result.SourceRoot,
                        scanCancellation.Token)
                    .ConfigureAwait(true);
                ScanStatus =
                    $"Windows.old holds {result.Walk.NodesVisited} entries across {result.Profiles.Count} profiles. Hashed {hashed} files under 64 MB. Nothing has been changed.";
            }
            else
            {
                ScanStatus =
                    $"Windows.old holds {result.Walk.NodesVisited} entries across {result.Profiles.Count} profiles. Nothing has been changed.";
            }

            lastProfiles = result.Profiles;
            lastClassification = result.Classification;
            lastRecipeCards = [];
            if (recipeHost is not null)
            {
                lastRecipeCards = await recipeHost.DetectAsync(
                        workspace.SessionId,
                        result.Profiles,
                        LiveProfileRoot,
                        workspace.TemporaryPath,
                        workspace.ExportsPath,
                        scanCancellation.Token)
                    .ConfigureAwait(true);
                ScanStatus += $" Found {lastRecipeCards.Count} app cards.";
            }

            RebuildCards(lastProfiles);
            CurrentStep = WorkflowStep.Decide;
            DecidePane = DecidePane.Cards;
            ReloadView();
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(ScanCompleted));
            PreparePreviewCommand.NotifyCanExecuteChanged();
        }
        catch (OperationCanceledException)
        {
            ScanStatus = "Scan paused. Partial results were kept.";
        }
        catch (Exception exception)
        {
            ShowHandledFailure(exception);
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
        RebuildCards(lastProfiles);
    }

    private async Task ClearDecisionAsync()
    {
        if (SelectedNode is null)
        {
            return;
        }

        await decisionEngine.ClearUserDecisionAsync(SelectedNode.Id).ConfigureAwait(true);
        ReloadView();
        RebuildCards(lastProfiles);
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

    private void CopyPath()
    {
        if (SelectedNode is null || SourceRoot is null)
        {
            return;
        }

        System.Windows.Clipboard.SetText(Path.Combine(SourceRoot, SelectedNode.RelPath));
    }

    private void RebuildCards(IReadOnlyList<DetectedProfile> profiles)
    {
        Cards.Clear();
        foreach (DetectedProfile profile in profiles)
        {
            Cards.Add(
                new OverviewCard(
                    profile.DisplayName,
                    profile.Kind == ProfileKind.Service
                        ? "Other account (service)"
                        : "User profile from Windows.old",
                    profile.LastUsedUtc is { } used
                        ? "Last used " + used.ToString("d MMM yyyy")
                        : "Last used unknown",
                    "Undecided",
                    NodeId: null,
                    "Profile"));

            foreach (StandardFolderMatch folder in profile.StandardFolders.Where(static item => item.PresentInSource))
            {
                TreeNodeRow? node = folder.RelativePathInSource is null
                    ? null
                    : nodeBrowser.FindByRelPath(folder.RelativePathInSource);
                Cards.Add(
                    new OverviewCard(
                        folder.KnownName,
                        "Personal folder in " + profile.DisplayName,
                        node is null
                            ? "Present in the old profile"
                            : $"{node.AggFiles} files, {node.AggSize} bytes",
                        node?.DecisionLabel ?? "Undecided",
                        node?.Id,
                        "PersonalFolder"));
            }
        }

        if (lastClassification is not null)
        {
            Cards.Add(
                new OverviewCard(
                    "High-value items",
                    "Password vaults, libraries, VM disks, and similar files.",
                    $"{lastClassification.HighValueCount} items, {lastClassification.HighValueBytes} bytes",
                    "Undecided",
                    NodeId: null,
                    "HighValue"));
            Cards.Add(
                new OverviewCard(
                    "Regeneratable",
                    "Caches and installers. Badged only — never left behind automatically.",
                    $"{lastClassification.RegeneratableCount} folders or files, {lastClassification.RegeneratableBytes} bytes",
                    "Undecided",
                    NodeId: null,
                    "Regeneratable"));
        }

        foreach (RecipeCard recipe in lastRecipeCards)
        {
            Cards.Add(
                new OverviewCard(
                    recipe.Title,
                    recipe.What,
                    recipe.WhyItMatters,
                    recipe.Components.Any(static component => component.SuggestedDefault == Decision.Restore)
                        ? "Restore (suggested)"
                        : "Undecided",
                    NodeId: null,
                    recipe.RecipeId));
        }

        OnPropertyChanged(nameof(Cards));
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

    public async Task PreparePreviewAsync()
    {
        if (SourceRoot is null)
        {
            return;
        }

        try
        {
        safeFs.CreateDirectory(DestinationRoot);
        PlanBuilder builder = new(sessionDb);
        lastPlan = await builder.BuildAsync(
                new PlanRequest(workspace.SessionId, SourceRoot, DestinationRoot, ConflictPolicy: selectedConflictPolicy))
            .ConfigureAwait(true);
        ApplyPreflight();
        Conflicts.Clear();
        if (lastPreflight is not null)
        {
            foreach (PlanConflict conflict in lastPreflight.Conflicts)
            {
                Conflicts.Add(new ConflictRow(conflict.DestinationPath, conflict.ExistingSize, conflict.ExistingWriteUtc));
            }
        }

        int recipeWrites = 0;
        if (recipeHost is not null)
        {
            DestinationContext destination = new(LiveProfileRoot, workspace.ExportsPath, safeFs, processRunner);
            foreach (RecipeCard card in lastRecipeCards)
            {
                IRecipe? recipe = recipeHost.Find(card.RecipeId);
                if (recipe is null)
                {
                    continue;
                }

                recipeWrites += recipeHost.PlanCard(recipe, card, destination).Writes.Count;
            }
        }

        SpaceBudgetText =
            $"Selected: {lastPlan.TotalBytes} bytes of {lastPreflight!.FreeBytes} free (need {lastPreflight.RequiredBytes} with margin)";
        ScanStatus = lastPreflight.CanProceed
            ? $"Preview: {lastPlan.Items.Count} copy operations, {Conflicts.Count} conflicts, and {recipeWrites} app writes. Windows.old has not been changed."
            : string.Join(" ", lastPreflight.BlockingIssues);
        ExecuteRestoreCommand.NotifyCanExecuteChanged();
        ApproveOverwritesCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(OverwriteButtonLabel));
        OnPropertyChanged(nameof(WindowTitle));
        CurrentStep = WorkflowStep.Preview;
        }
        catch (Exception exception)
        {
            ShowHandledFailure(exception);
        }
    }

    private async Task ApproveOverwritesAsync()
    {
        IReadOnlyList<string> approved = Conflicts
            .Where(static row => row.Approved)
            .Select(static row => row.DestinationPath)
            .ToArray();
        await sessionDb.SetKvAsync(
                workspace.SessionId,
                OverwriteApprovals.KvKey,
                OverwriteApprovals.Format(approved))
            .ConfigureAwait(true);
        RefreshPreflight();
        ScanStatus = "Overwrite confirmed for " + approved.Count + " files. Other conflicts keep both copies.";
        OnPropertyChanged(nameof(OverwriteButtonLabel));
    }

    private bool CanApproveOverwrites()
    {
        return selectedConflictPolicy == ConflictPolicy.OverwriteApproved &&
            Conflicts.Any(static row => row.Approved);
    }

    private void RefreshPreflight()
    {
        if (lastPlan is null)
        {
            return;
        }

        ApplyPreflight();
        ExecuteRestoreCommand.NotifyCanExecuteChanged();
        ApproveOverwritesCommand.NotifyCanExecuteChanged();
    }

    private void ApplyPreflight()
    {
        if (lastPlan is null)
        {
            return;
        }

        IReadOnlySet<string> approved = OverwriteApprovals.Parse(
            sessionDb.GetKv(workspace.SessionId, OverwriteApprovals.KvKey));
        lastPreflight = new PreflightChecker().Check(lastPlan, selectedConflictPolicy, approved);
    }

    private async Task ExecuteRestoreAsync()
    {
        if (lastPlan is null)
        {
            return;
        }

        try
        {
        RestoreRunner runner = new(new CopyEngine(sessionDb, safeFs));
        RestoreResult result = await runner.RunAsync(lastPlan).ConfigureAwait(true);
        if (result.Completed && recipeHost is not null)
        {
            DestinationContext destination = new(LiveProfileRoot, workspace.ExportsPath, safeFs, processRunner);
            foreach (RecipeCard card in lastRecipeCards)
            {
                IRecipe? recipe = recipeHost.Find(card.RecipeId);
                if (recipe is null)
                {
                    continue;
                }

                PlanResult recipePlan = recipeHost.PlanCard(recipe, card, destination);
                if (recipePlan.Writes.Count == 0)
                {
                    continue;
                }

                await recipeHost.ExecuteAsync(workspace.SessionId, recipe, recipePlan).ConfigureAwait(true);
            }
        }

        restoreCompleted = result.Completed;
        ScanStatus = result.PausedDiskFull
            ? "Restore paused: the destination volume is full. Nothing was deleted to make room. A redacted log is at " +
              workspace.LogPath + "."
            : result.Completed
                ? "Restore finished. Verify the copies before considering a purge."
                : "Restore did not finish. A redacted log is at " + workspace.LogPath + ".";
        ExecuteVerifyCommand.NotifyCanExecuteChanged();
        CurrentStep = WorkflowStep.Restore;
        }
        catch (Exception exception)
        {
            ShowHandledFailure(exception);
        }
    }

    private async Task ExecuteVerifyAsync()
    {
        if (lastPlan is null)
        {
            return;
        }

        try
        {
        VerifyReport report = await new Verifier(sessionDb, safeFs).VerifyAsync(lastPlan).ConfigureAwait(true);
        verifyCompleted = report.AllOk;
        ScanStatus = report.AllOk
            ? "Verify report: every checked item passed existence, size/time, and hash samples."
            : "Verify report: at least one item failed. Purge stays locked. A redacted log is at " +
              workspace.LogPath + ".";
        ExecutePurgeCommand.NotifyCanExecuteChanged();
        CreateSupportBundleCommand.NotifyCanExecuteChanged();
        CurrentStep = WorkflowStep.Verify;
        }
        catch (Exception exception)
        {
            ShowHandledFailure(exception);
        }
    }

    private async Task ExecutePurgeAsync()
    {
        if (SourceRoot is null)
        {
            return;
        }

        try
        {
        string folderName = Path.GetFileName(SourceRoot.TrimEnd('\\'));
        string canonical = sourceGuard.SourceRoots.FirstOrDefault(
            root => root.Equals(
                PathCanonicalizer.Canonicalize(SourceRoot),
                StringComparison.OrdinalIgnoreCase)) ?? PathCanonicalizer.Canonicalize(SourceRoot);
        PurgeGateResult gate = PurgeAuthorization.Evaluate(
            new PurgeGateRequest(
                verifyCompleted,
                filesChecked,
                undecidedAcknowledged,
                RestoreJobActive: IsScanning,
                PurgeTypedFolderName,
                folderName,
                canonical,
                Environment.ProcessPath,
                workspace.RootPath,
                [destinationRoot],
                customRootConfirmed));
        if (!gate.Authorized || gate.Token is null)
        {
            ScanStatus = "Purge blocked: " + string.Join(", ", gate.BlockedGates);
            return;
        }

        PurgeExecuteResult result = await new PurgeExecutor()
            .ExecuteAsync(
                new PurgeExecuteRequest(
                    canonical,
                    gate.Token,
                    safeFs,
                    sourceGuard,
                    processRunner,
                    workspace.RootPath,
                    preferCleanupHandler))
            .ConfigureAwait(true);
        ScanStatus = result.Completed
            ? "Purge finished (" + result.Method + "). Session records remain in this app's data folder."
            : "Purge did not finish: " + result.Detail + " A redacted log is at " + workspace.LogPath + ".";
        CurrentStep = WorkflowStep.Purge;
        }
        catch (Exception exception)
        {
            ShowHandledFailure(exception);
        }
    }

    private void CreateSupportBundle()
    {
        try
        {
            string zip = SupportBundle.Create(safeFs, workspace);
            ScanStatus = "Support bundle written to " + zip;
        }
        catch (Exception exception)
        {
            ShowHandledFailure(exception);
        }
    }

    private void ShowHandledFailure(Exception exception)
    {
        ScanStatus = ExceptionReport.FormatUserMessage(
            exception,
            workspace.LogPath,
            new SensitiveDataRedactor());
    }
}
