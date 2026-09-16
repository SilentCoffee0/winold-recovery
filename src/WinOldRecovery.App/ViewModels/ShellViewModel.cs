using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOldRecovery.App.Help;
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

public enum SubfolderPolicy
{
    RecoveredFolder,
    MergeIntoProfile,
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
    private readonly IReadOnlyList<ClassificationRule> classificationRules = ClassificationRuleCatalog.LoadEmbedded();
    private readonly FirstRunState firstRun;
    private readonly LocalHelp localHelp;
    private CancellationTokenSource? scanCancellation;
    private WorkflowStep currentStep = WorkflowStep.Scan;
    private bool scanCompleted;
    private string? selectedSourcePath;
    private string scanStatus = "Choose a Windows.old folder, then scan.";
    private string currentPath = string.Empty;
    private int nodesVisited;
    private IReadOnlyList<string> scanProfileNames = [];
    private TreeNodeRow? selectedNode;
    private List<TreeNodeRow> selectedNodes = [];
    private FilesViewMode filesViewMode = FilesViewMode.Tree;
    private string searchText = string.Empty;
    private DecidePane decidePane = DecidePane.Cards;
    private bool hashDuringScan;
    private bool computeFolderSizes = true;
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
    private string purgeProgressText = string.Empty;
    private CancellationTokenSource? purgeCancellation;
    private bool isPurging;
    private ConflictPolicy selectedConflictPolicy = ConflictPolicy.KeepBoth;
    private string spaceBudgetText = "Selected: — of destination free space";
    private SpaceBudgetLevel spaceBudgetLevel = SpaceBudgetLevel.Idle;
    private string destinationRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Recovered");
    private bool helpVisible;
    private bool firstRunVisible;
    private bool interruptedVisible;
    private string interruptedText = string.Empty;
    private bool diskFullVisible;
    private string diskFullText = string.Empty;
    private Dictionary<string, string> destinationByRelPath = new(StringComparer.OrdinalIgnoreCase);
    private string previewSummary = string.Empty;
    private string purgeSummary = string.Empty;
    private string restoreProgress = string.Empty;
    private SubfolderPolicy subfolderPolicy = SubfolderPolicy.RecoveredFolder;
    private CancellationTokenSource? restoreCancellation;
    private CancellationTokenSource? restorePause;
    private IReadOnlyList<string> runningApps = [];
    private HelpTopic? selectedHelpTopic;
    private string helpText = string.Empty;
    private bool logVisible;
    private string logText = string.Empty;
    private bool scanPaused;
    private bool scanAbortRequested;
    private string? pausedSourcePath;
    private string sourceHint = string.Empty;
    private readonly IFolderPicker folderPicker;
    private readonly IProcessPresence processPresence;
    private bool compactLayout;
    private bool compactInspect;
    private string currentPathFull = string.Empty;
    private bool isRestoring;
    private int recentDays = 30;
    private bool treeTruncated;
    private long? pagedParentId;
    private int pagedLoaded;
    private int pagedTotal;
    private string sourceIntegrityText = "Windows.old untouched";
    private bool sessionReadOnly;
    private string verifyAckReason = string.Empty;
    private string verifyFailureText = string.Empty;
    private string inspectConflictText = string.Empty;
    private string inspectMtimeText = string.Empty;
    private string inspectWhyText = string.Empty;
    private string restoreWarningSummary = string.Empty;
    private string restoreWarningDetail = string.Empty;
    private bool restoreWarningsExpanded;
    private bool syncthingMappingEditorVisible;
    private Task mappingsPersist = Task.CompletedTask;

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
        IReadOnlyList<IRecipe>? recipes = null,
        FirstRunState? firstRunState = null,
        LocalHelp? localHelp = null,
        IFolderPicker? folderPicker = null,
        IProcessPresence? processPresence = null)
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
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => CanWriteSession && !IsScanning && SelectedSourcePath is not null);
        CancelScanCommand = new RelayCommand(AbortScan, () => IsScanning);
        PauseScanCommand = new RelayCommand(PauseScan, () => IsScanning);
        BrowseSourceCommand = new AsyncRelayCommand(BrowseSourceAsync, () => CanWriteSession);
        RestoreCommand = new AsyncRelayCommand(() => ApplyDecisionAsync(Decision.Restore), CanMutateSelection);
        LeaveBehindCommand = new AsyncRelayCommand(() => ApplyDecisionAsync(Decision.LeaveBehind), CanMutateSelection);
        UndecidedCommand = new AsyncRelayCommand(ClearDecisionAsync, CanMutateSelection);
        UndoDecisionCommand = new AsyncRelayCommand(UndoDecisionAsync, () => CanWriteSession && scanCompleted && decisionEngine.CanUndo);
        RestoreExceptRegeneratableCommand = new AsyncRelayCommand(RestoreExceptRegeneratableAsync, CanMutateSelection);
        RestoreNewerCommand = new AsyncRelayCommand(RestoreNewerAsync, CanMutateSelection);
        OpenFolderCommand = new AsyncRelayCommand(OpenFolderAsync, CanOpenFolder);
        InspectOverviewCardCommand = new RelayCommand<OverviewCard>(
            InspectOverviewCard,
            static card => card is { ShowVerbs: true });
        OpenOverviewCardCommand = new AsyncRelayCommand<OverviewCard>(
            OpenOverviewCardAsync,
            static card => card is { ShowVerbs: true });
        RestoreOverviewCardCommand = new AsyncRelayCommand<OverviewCard>(
            card => DecideOverviewCardAsync(card, Decision.Restore),
            static card => card is { ShowVerbs: true });
        LeaveOverviewCardCommand = new AsyncRelayCommand<OverviewCard>(
            card => DecideOverviewCardAsync(card, Decision.LeaveBehind),
            static card => card is { ShowVerbs: true });
        AnalyzeGitCommand = new AsyncRelayCommand(AnalyzeGitAsync, CanAnalyzeGit);
        ExpandCommand = new RelayCommand<TreeNodeRow>(Expand);
        ShowCardsCommand = new RelayCommand(() =>
        {
            compactInspect = false;
            DecidePane = DecidePane.Cards;
            OnPropertyChanged(nameof(CompactInspect));
        });
        ShowFilesCommand = new RelayCommand(() =>
        {
            compactInspect = false;
            DecidePane = DecidePane.Files;
            OnPropertyChanged(nameof(CompactInspect));
        });
        ShowInspectCommand = new RelayCommand(() =>
        {
            compactInspect = true;
            OnPropertyChanged(nameof(CompactInspect));
        });
        SearchCommand = new RelayCommand(SearchNow);
        InspectCommand = new RelayCommand(
            () =>
            {
                DecidePane = DecidePane.Files;
                OnPropertyChanged(nameof(DetailText));
            },
            () => SelectedNode is not null);
        CopyPathCommand = new RelayCommand(CopyPath, () => SelectedNode is not null && SourceRoot is not null);
        ShowMoreCommand = new RelayCommand(ShowMore, () => TreeTruncated);
        RevealInTreeCommand = new RelayCommand(RevealInTree, () => SelectedNode is not null);
        ExecuteRestoreCommand = new AsyncRelayCommand(
            ExecuteRestoreAsync,
            () => CanWriteSession && lastPlan is not null && lastPreflight is { CanProceed: true } && runningApps.Count == 0 && !IsScanning && !isRestoring);
        ExecuteVerifyCommand = new AsyncRelayCommand(
            ExecuteVerifyAsync,
            () => CanWriteSession && restoreCompleted && !isRestoring);
        PreparePreviewCommand = new AsyncRelayCommand(
            PreparePreviewAsync,
            () => CanWriteSession && scanCompleted && SourceRoot is not null);
        EditSyncthingMappingCommand = new RelayCommand(
            () => SyncthingMappingEditorVisible = !SyncthingMappingEditorVisible,
            () => HasSyncthingMappings);
        BrowseSyncthingMappingCommand = new RelayCommand<SyncthingMappingRow>(
            BrowseSyncthingMapping,
            static row => row is not null);
        ApproveOverwritesCommand = new AsyncRelayCommand(ApproveOverwritesAsync, CanApproveOverwrites);
        ExecutePurgeCommand = new AsyncRelayCommand(
            ExecutePurgeAsync,
            () => CanWriteSession && verifyCompleted && !IsScanning && !isRestoring && !isPurging);
        CancelPurgeCommand = new RelayCommand(CancelPurge, () => isPurging);
        AcknowledgeVerifyCommand = new AsyncRelayCommand(AcknowledgeVerifyAsync, CanAcknowledgeVerify);
        CreateSupportBundleCommand = new RelayCommand(CreateSupportBundle, () => verifyCompleted);
        this.firstRun = firstRunState ?? FirstRunState.FromWorkspace(safeFs, workspace);
        this.localHelp = localHelp ?? LocalHelp.FromAppDirectory();
        this.folderPicker = folderPicker ?? new NullFolderPicker();
        this.processPresence = processPresence ?? new Win32ProcessPresence();
        firstRunVisible = !this.firstRun.IsDismissed();
        OpenHelpCommand = new RelayCommand(OpenHelp);
        CloseHelpCommand = new RelayCommand(() => HelpVisible = false);
        ShowLogCommand = new RelayCommand(OpenLog);
        CloseLogCommand = new RelayCommand(() => LogVisible = false);
        DismissFirstRunCommand = new RelayCommand(DismissFirstRun);
        ResumeInterruptedCommand = new AsyncRelayCommand(
            ResumeInterruptedAsync,
            () => CanWriteSession && lastPlan is not null && !isRestoring);
        DismissInterruptedCommand = new RelayCommand(DismissInterrupted);
        BrowseDestinationCommand = new RelayCommand(BrowseDestination, () => CanWriteSession);
        ResumeDiskFullCommand = new AsyncRelayCommand(
            ResumeDiskFullAsync,
            () => CanWriteSession && lastPlan is not null && diskFullVisible && !isRestoring);
        CancelDiskFullCommand = new RelayCommand(CancelDiskFull);
        CancelRestoreCommand = new RelayCommand(CancelRestore, () => isRestoring);
        PauseRestoreCommand = new RelayCommand(
            PauseRestore,
            () => isRestoring && restorePause is { IsCancellationRequested: false });
        ToggleRestoreWarningsCommand = new RelayCommand(
            () => RestoreWarningsExpanded = !RestoreWarningsExpanded,
            () => HasRestoreWarnings);
        GoToPurgeCommand = new RelayCommand(
            () => CurrentStep = WorkflowStep.Purge,
            () => CanGoTo(WorkflowStep.Purge));
        ReviewUndecidedCommand = new RelayCommand(ReviewUndecided, () => scanCompleted);
        destinationByRelPath = DestinationMap.Parse(sessionDb.GetKv(workspace.SessionId, DestinationMap.KvKey));
        ApplySessionLockFromStore();
    }

    public IAsyncRelayCommand ScanCommand { get; }
    public IRelayCommand CancelScanCommand { get; }
    public IRelayCommand PauseScanCommand { get; }
    public IAsyncRelayCommand BrowseSourceCommand { get; }
    public IAsyncRelayCommand RestoreCommand { get; }
    public IAsyncRelayCommand LeaveBehindCommand { get; }
    public IAsyncRelayCommand UndecidedCommand { get; }
    public IAsyncRelayCommand UndoDecisionCommand { get; }
    public IAsyncRelayCommand RestoreExceptRegeneratableCommand { get; }
    public IAsyncRelayCommand RestoreNewerCommand { get; }
    public IAsyncRelayCommand OpenFolderCommand { get; }
    public IRelayCommand<OverviewCard> InspectOverviewCardCommand { get; }
    public IAsyncRelayCommand<OverviewCard> OpenOverviewCardCommand { get; }
    public IAsyncRelayCommand<OverviewCard> RestoreOverviewCardCommand { get; }
    public IAsyncRelayCommand<OverviewCard> LeaveOverviewCardCommand { get; }
    public IAsyncRelayCommand AnalyzeGitCommand { get; }
    public IRelayCommand<TreeNodeRow> ExpandCommand { get; }
    public IRelayCommand ShowCardsCommand { get; }
    public IRelayCommand ShowFilesCommand { get; }
    public IRelayCommand ShowInspectCommand { get; }
    public IRelayCommand SearchCommand { get; }
    public IRelayCommand InspectCommand { get; }
    public IRelayCommand CopyPathCommand { get; }
    public IRelayCommand ShowMoreCommand { get; }
    public IRelayCommand RevealInTreeCommand { get; }
    public IAsyncRelayCommand ExecuteRestoreCommand { get; }
    public IAsyncRelayCommand ExecuteVerifyCommand { get; }
    public IAsyncRelayCommand PreparePreviewCommand { get; }
    public IRelayCommand EditSyncthingMappingCommand { get; }
    public IRelayCommand<SyncthingMappingRow> BrowseSyncthingMappingCommand { get; }
    public IAsyncRelayCommand ApproveOverwritesCommand { get; }
    public IAsyncRelayCommand ExecutePurgeCommand { get; }
    public IRelayCommand CancelPurgeCommand { get; }
    public IRelayCommand CreateSupportBundleCommand { get; }
    public IRelayCommand OpenHelpCommand { get; }
    public IRelayCommand CloseHelpCommand { get; }
    public IRelayCommand ShowLogCommand { get; }
    public IRelayCommand CloseLogCommand { get; }
    public IRelayCommand DismissFirstRunCommand { get; }
    public IAsyncRelayCommand ResumeInterruptedCommand { get; }
    public IRelayCommand DismissInterruptedCommand { get; }
    public IRelayCommand BrowseDestinationCommand { get; }
    public IAsyncRelayCommand ResumeDiskFullCommand { get; }
    public IRelayCommand CancelDiskFullCommand { get; }
    public IRelayCommand CancelRestoreCommand { get; }
    public IRelayCommand PauseRestoreCommand { get; }
    public IRelayCommand ToggleRestoreWarningsCommand { get; }
    public IRelayCommand GoToPurgeCommand { get; }
    public IRelayCommand ReviewUndecidedCommand { get; }
    public IAsyncRelayCommand AcknowledgeVerifyCommand { get; }

    public IReadOnlyList<HelpTopic> HelpTopics => LocalHelp.Catalog;

    public ObservableCollection<SourceCandidate> Sources { get; } = [];

    public ObservableCollection<TreeNodeRow> TreeRows { get; } = [];

    public ObservableCollection<OverviewCard> Cards { get; } = [];

    public ObservableCollection<ConflictRow> Conflicts { get; } = [];

    public ObservableCollection<SyncthingMappingRow> SyncthingMappings { get; } = [];

    public ObservableCollection<RestoreJobRow> RestoreJobs { get; } = [];

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
            if (value == WorkflowStep.Purge)
            {
                RefreshPurgeSummary();
            }
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
        set
        {
            if (SetProperty(ref preferCleanupHandler, value))
            {
                OnPropertyChanged(nameof(PreferManualDelete));
            }
        }
    }

    public bool PreferManualDelete
    {
        get => !preferCleanupHandler;
        set
        {
            if (value == PreferManualDelete)
            {
                return;
            }

            PreferCleanupHandler = !value;
        }
    }

    public string PurgeProgressText
    {
        get => purgeProgressText;
        private set => SetProperty(ref purgeProgressText, value);
    }

    public bool IsPurging => isPurging;

    public string PurgeTypedFolderName
    {
        get => purgeTypedFolderName;
        set => SetProperty(ref purgeTypedFolderName, value);
    }

    public string SourceIntegrityText
    {
        get => sourceIntegrityText;
        private set
        {
            if (SetProperty(ref sourceIntegrityText, value))
            {
                OnPropertyChanged(nameof(SourceIntegrityLevel));
            }
        }
    }

    public SourceIntegrityLevel SourceIntegrityLevel => StatusStrip.IntegrityLevel(sourceIntegrityText);

    public SpaceBudgetLevel SpaceBudgetLevel
    {
        get => spaceBudgetLevel;
        private set => SetProperty(ref spaceBudgetLevel, value);
    }

    public IReadOnlyList<int> RecentDayChoices { get; } = [7, 30, 90];

    public int RecentDays
    {
        get => recentDays;
        set
        {
            if (value is not (7 or 30 or 90))
            {
                return;
            }

            if (SetProperty(ref recentDays, value) && FilesViewMode == FilesViewMode.Recent)
            {
                ReloadView();
            }
        }
    }

    public bool TreeTruncated
    {
        get => treeTruncated;
        private set
        {
            if (SetProperty(ref treeTruncated, value))
            {
                ShowMoreCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(PagingStatus));
            }
        }
    }

    public string PagingStatus =>
        pagedTotal == 0 ? string.Empty : "Showing " + pagedLoaded.ToString("N0", CultureInfo.InvariantCulture) +
            " of " + pagedTotal.ToString("N0", CultureInfo.InvariantCulture);

    public bool IsRecentView
    {
        get => FilesViewMode == FilesViewMode.Recent;
        set
        {
            if (value)
            {
                FilesViewMode = FilesViewMode.Recent;
            }
        }
    }

    public bool IsTreeView
    {
        get => FilesViewMode == FilesViewMode.Tree;
        set
        {
            if (value)
            {
                FilesViewMode = FilesViewMode.Tree;
            }
        }
    }

    public bool IsLargestView
    {
        get => FilesViewMode == FilesViewMode.Largest;
        set
        {
            if (value)
            {
                FilesViewMode = FilesViewMode.Largest;
            }
        }
    }

    public bool IsSearchView => FilesViewMode == FilesViewMode.Search;

    public bool IsUnknownView
    {
        get => FilesViewMode == FilesViewMode.Unknown;
        set
        {
            if (value)
            {
                FilesViewMode = FilesViewMode.Unknown;
            }
        }
    }

    public bool IsProblemsView
    {
        get => FilesViewMode == FilesViewMode.Problems;
        set
        {
            if (value)
            {
                FilesViewMode = FilesViewMode.Problems;
            }
        }
    }

    public string SpaceBudgetText
    {
        get => spaceBudgetText;
        private set => SetProperty(ref spaceBudgetText, value);
    }

    public string ElevationNote { get; } =
        "Files in Windows.old belong to a user account that no longer exists; reading them needs administrator rights.";

    public string PromisesText { get; } =
        "We never change Windows.old until you choose to delete it in the last step. We never overwrite your files silently.";

    public string FirstRunBody { get; } =
        "WinOld Recovery has six steps." + Environment.NewLine + Environment.NewLine +
        "1. Scan Windows.old (read-only)." + Environment.NewLine +
        "2. Decide Restore, Leave Behind, or Undecided." + Environment.NewLine +
        "3. Preview the plan and conflicts." + Environment.NewLine +
        "4. Restore copies to your new profile." + Environment.NewLine +
        "5. Verify the copies." + Environment.NewLine +
        "6. Purge Windows.old only after you type its name." + Environment.NewLine + Environment.NewLine +
        "We never change Windows.old until you choose to delete it in the last step. We never overwrite your files silently." +
        Environment.NewLine + Environment.NewLine +
        "Files in Windows.old belong to a user account that no longer exists; reading them needs administrator rights.";

    public bool HelpVisible
    {
        get => helpVisible;
        private set => SetProperty(ref helpVisible, value);
    }

    public bool FirstRunVisible
    {
        get => firstRunVisible;
        private set => SetProperty(ref firstRunVisible, value);
    }

    public bool InterruptedRestoreVisible
    {
        get => interruptedVisible;
        private set => SetProperty(ref interruptedVisible, value);
    }

    public string InterruptedRestoreText
    {
        get => interruptedText;
        private set => SetProperty(ref interruptedText, value);
    }

    public bool DiskFullVisible
    {
        get => diskFullVisible;
        private set => SetProperty(ref diskFullVisible, value);
    }

    public string DiskFullText
    {
        get => diskFullText;
        private set => SetProperty(ref diskFullText, value);
    }

    public string PreviewSummaryText
    {
        get => previewSummary;
        private set => SetProperty(ref previewSummary, value);
    }

    public bool HasSyncthingMappings => SyncthingMappings.Count > 0;

    public bool SyncthingMappingEditorVisible
    {
        get => syncthingMappingEditorVisible;
        private set
        {
            if (SetProperty(ref syncthingMappingEditorVisible, value))
            {
                OnPropertyChanged(nameof(EditSyncthingMappingLabel));
            }
        }
    }

    public string EditSyncthingMappingLabel =>
        SyncthingMappingEditorVisible ? "Hide mapping" : "Edit mapping";

    public string PurgeSummaryText
    {
        get => purgeSummary;
        private set => SetProperty(ref purgeSummary, value);
    }

    public string RestoreProgress
    {
        get => restoreProgress;
        private set => SetProperty(ref restoreProgress, value);
    }

    public string RestoreWarningSummary
    {
        get => restoreWarningSummary;
        private set
        {
            if (SetProperty(ref restoreWarningSummary, value))
            {
                OnPropertyChanged(nameof(HasRestoreWarnings));
                OnPropertyChanged(nameof(RestoreWarningsToggleLabel));
                ToggleRestoreWarningsCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string RestoreWarningDetail => restoreWarningsExpanded ? restoreWarningDetail : string.Empty;

    public bool HasRestoreWarnings => restoreWarningSummary.Length > 0;

    public bool RestoreWarningsExpanded
    {
        get => restoreWarningsExpanded;
        private set
        {
            if (SetProperty(ref restoreWarningsExpanded, value))
            {
                OnPropertyChanged(nameof(RestoreWarningDetail));
                OnPropertyChanged(nameof(RestoreWarningsToggleLabel));
            }
        }
    }

    public string RestoreWarningsToggleLabel => restoreWarningsExpanded ? "Hide" : "Show";

    public bool SessionReadOnly => sessionReadOnly;

    public bool CanMutateSession => !sessionReadOnly;

    public bool CanWriteSession => !sessionReadOnly && !isRestoring && !isPurging;

    public string VerifyAckReason
    {
        get => verifyAckReason;
        set
        {
            if (SetProperty(ref verifyAckReason, value ?? string.Empty))
            {
                AcknowledgeVerifyCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string VerifyFailureText
    {
        get => verifyFailureText;
        private set => SetProperty(ref verifyFailureText, value);
    }

    public SubfolderPolicy SubfolderPolicy
    {
        get => subfolderPolicy;
        set
        {
            if (SetProperty(ref subfolderPolicy, value))
            {
                ApplySubfolderPolicy();
            }
        }
    }

    public bool MergeIntoProfile
    {
        get => subfolderPolicy == SubfolderPolicy.MergeIntoProfile;
        set => SubfolderPolicy = value ? SubfolderPolicy.MergeIntoProfile : SubfolderPolicy.RecoveredFolder;
    }

    public HelpTopic? SelectedHelpTopic
    {
        get => selectedHelpTopic;
        set
        {
            if (SetProperty(ref selectedHelpTopic, value) && value is not null)
            {
                LoadHelp(value);
            }
        }
    }

    public string HelpText
    {
        get => helpText;
        private set => SetProperty(ref helpText, value);
    }

    public bool LogVisible
    {
        get => logVisible;
        private set => SetProperty(ref logVisible, value);
    }

    public string LogText
    {
        get => logText;
        private set => SetProperty(ref logText, value);
    }

    public bool CompactLayout
    {
        get => compactLayout;
        private set => SetProperty(ref compactLayout, value);
    }

    public bool CompactInspect
    {
        get => compactInspect;
        private set => SetProperty(ref compactInspect, value);
    }

    public string CurrentPathFull
    {
        get => currentPathFull;
        private set => SetProperty(ref currentPathFull, value);
    }

    public string ScanButtonLabel => scanPaused ? "Resume scan" : "Scan";

    public string SourceHint
    {
        get => sourceHint;
        private set => SetProperty(ref sourceHint, value);
    }

    public string SkipHint { get; } =
        "Junctions and symlinks: listed as leaves, never followed. " +
        "Cloud placeholders: not opened, so OneDrive files stay in the cloud. " +
        "Encrypted (EFS): not decrypted. " +
        "Access denied: the scan continued without that folder.";

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
                RefreshSourceHint();
            }
        }
    }

    public string DestinationRoot
    {
        get => destinationRoot;
        set
        {
            if (SetProperty(ref destinationRoot, value))
            {
                OnPropertyChanged(nameof(PlannedDestinationPath));
                RefreshInspectConflict();
            }
        }
    }

    public bool CanEditDestination =>
        !sessionReadOnly &&
        SelectedNode is { Kind: NodeKind.Directory, IsReparse: false } && SourceRoot is not null;

    public string PlannedDestinationPath
    {
        get
        {
            if (SelectedNode is null)
            {
                return string.Empty;
            }

            return DestinationMap.Resolve(DestinationRoot, destinationByRelPath, SelectedNode.RelPath);
        }
        set
        {
            if (!CanEditDestination || SelectedNode is null || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            destinationByRelPath[SelectedNode.RelPath] = value.Trim();
            OnPropertyChanged();
            RefreshInspectConflict();
            _ = PersistDestinationMapAsync();
        }
    }

    public string? SourceRoot { get; private set; }

    public IReadOnlyList<TreeNodeRow> SelectedNodes => selectedNodes;

    public void ReplaceSelection(IReadOnlyList<TreeNodeRow> rows)
    {
        selectedNodes = rows.Count == 0 ? [] : [.. rows];
        TreeNodeRow? primary = selectedNodes.Count == 0 ? null : selectedNodes[^1];
        if (!Equals(selectedNode, primary))
        {
            SelectedNode = primary;
        }
        else
        {
            RestoreCommand.NotifyCanExecuteChanged();
            LeaveBehindCommand.NotifyCanExecuteChanged();
            UndecidedCommand.NotifyCanExecuteChanged();
            RestoreExceptRegeneratableCommand.NotifyCanExecuteChanged();
            RestoreNewerCommand.NotifyCanExecuteChanged();
        }
    }

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
                RestoreExceptRegeneratableCommand.NotifyCanExecuteChanged();
                RestoreNewerCommand.NotifyCanExecuteChanged();
                OpenFolderCommand.NotifyCanExecuteChanged();
                InspectCommand.NotifyCanExecuteChanged();
                CopyPathCommand.NotifyCanExecuteChanged();
                RevealInTreeCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(DetailText));
                OnPropertyChanged(nameof(PlannedDestinationPath));
                OnPropertyChanged(nameof(CanEditDestination));
                RefreshInspectConflict();
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
                OnPropertyChanged(nameof(IsRecentView));
                OnPropertyChanged(nameof(IsTreeView));
                OnPropertyChanged(nameof(IsLargestView));
                OnPropertyChanged(nameof(IsSearchView));
                OnPropertyChanged(nameof(IsUnknownView));
                OnPropertyChanged(nameof(IsProblemsView));
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

    public bool ComputeFolderSizes
    {
        get => computeFolderSizes;
        set => SetProperty(ref computeFolderSizes, value);
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
                OnPropertyChanged(nameof(GitCardSelected));
                ReloadRecipeComponents();
                RestoreCommand.NotifyCanExecuteChanged();
                LeaveBehindCommand.NotifyCanExecuteChanged();
                UndecidedCommand.NotifyCanExecuteChanged();
                OpenFolderCommand.NotifyCanExecuteChanged();
                AnalyzeGitCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string WindowTitle =>
        SourceRoot is null
            ? $"WinOld Recovery — {CurrentStep}"
            : $"WinOld Recovery — {PathDisplay.MiddleEllipsis(SourceRoot, 48)} — {CurrentStep}";

    public ObservableCollection<RecipeComponentChoice> RecipeComponents { get; } = [];

    public RecipeCard? SelectedRecipeCard =>
        SelectedCard is null
            ? null
            : lastRecipeCards.FirstOrDefault(card => card.Title == SelectedCard.Title);

    public bool RecipeCardSelected => SelectedRecipeCard is not null;

    public bool GitCardSelected =>
        SelectedRecipeCard is { RecipeId: "git" };

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
                        .. RecipeRiskLines(recipe),
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
            string sourceFull = SourceRoot is null ? node.RelPath : Path.Combine(SourceRoot, node.RelPath);
            string destination = string.IsNullOrEmpty(PlannedDestinationPath)
                ? (SourceRoot is null ? node.RelPath : Path.Combine(DestinationRoot, node.RelPath))
                : PlannedDestinationPath;
            string sensitive = node.Badges.Any(static badge =>
                    badge.Contains("Sensitive", StringComparison.OrdinalIgnoreCase)) ||
                node.RelPath.Contains("AppData", StringComparison.OrdinalIgnoreCase)
                ? "This file contains secrets. Contents are never displayed or logged."
                : string.Empty;
            return string.Join(
                Environment.NewLine,
                ((string[])
                [
                    "Source: " + PathDisplay.MiddleEllipsis(sourceFull),
                    "Full source path: " + sourceFull,
                    "Planned destination: " + PathDisplay.MiddleEllipsis(destination),
                    "Full destination path: " + destination,
                    "Size: " + QuantityFormat.Bytes(node.AggSize) + "  Files: " + QuantityFormat.Count(node.AggFiles),
                    inspectMtimeText,
                    inspectWhyText,
                    OwnerSidDisplay.Line(sourceFull),
                    OwnerSidDisplay.AttributesLine(sourceFull),
                    "Decision: " + node.DecisionLabel,
                    node.Problem == NodeProblem.None ? string.Empty : node.ProblemExplanation,
                    node.IsReparse ? node.RowTooltip : string.Empty,
                    inspectConflictText,
                    sensitive,
                ]).Where(static line => line.Length > 0));
        }
    }

    private void RefreshInspectConflict()
    {
        if (SelectedNode is null || SourceRoot is null || SelectedNode.IsReparse)
        {
            inspectConflictText = string.Empty;
            inspectMtimeText = SelectedNode is null
                ? string.Empty
                : NodeBrowser.FormatMtimeRange(SelectedNode.ModifiedUtc, SelectedNode.ModifiedUtc);
            inspectWhyText = SelectedNode is null
                ? string.Empty
                : string.Join(
                    Environment.NewLine,
                    ClassificationExplanations.ForNode(SelectedNode, classificationRules));
            OnPropertyChanged(nameof(DetailText));
            return;
        }

        string source = Path.Combine(SourceRoot, SelectedNode.RelPath);
        inspectConflictText = DestinationConflictPreview.Format(
            DestinationConflictPreview.Scan(source, PlannedDestinationPath));
        (DateTimeOffset? oldest, DateTimeOffset? newest) = nodeBrowser.GetMtimeRange(SelectedNode.Id);
        inspectMtimeText = NodeBrowser.FormatMtimeRange(
            oldest ?? SelectedNode.ModifiedUtc,
            newest ?? SelectedNode.ModifiedUtc);
        inspectWhyText = string.Join(
            Environment.NewLine,
            ClassificationExplanations.ForNode(SelectedNode, classificationRules));
        OnPropertyChanged(nameof(DetailText));
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
        if (isRestoring || isPurging)
        {
            return false;
        }

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
                    ToggleExpand(SelectedNode);
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
        ApplyPaging(page, row.Id);
    }

    public void ToggleExpand(TreeNodeRow? row)
    {
        if (row is null || FilesViewMode != FilesViewMode.Tree)
        {
            return;
        }

        int index = IndexOfRow(row.Id);
        if (index >= 0 && ExclusiveSubtreeEnd(index) > index + 1)
        {
            ReplaceRowsUnder(row, []);
            if (pagedParentId == row.Id)
            {
                ApplyPaging(new NodePage([], 0, false), row.Id);
            }

            return;
        }

        Expand(row);
    }

    public void ShowMore()
    {
        if (!TreeTruncated || FilesViewMode == FilesViewMode.Largest)
        {
            return;
        }

        NodePage page = FilesViewMode switch
        {
            FilesViewMode.Recent => nodeBrowser.GetRecent(RecentDays, null, pagedLoaded),
            FilesViewMode.Search => string.IsNullOrWhiteSpace(SearchText)
                ? new NodePage([], 0, false)
                : nodeBrowser.Search(SearchText, null, pagedLoaded),
            FilesViewMode.Unknown => nodeBrowser.GetUnknown(null, pagedLoaded),
            FilesViewMode.Problems => nodeBrowser.GetProblems(pagedLoaded),
            _ => nodeBrowser.GetChildren(pagedParentId, pagedLoaded),
        };

        if (FilesViewMode == FilesViewMode.Tree && pagedParentId is long parentId)
        {
            TreeNodeRow? parent = TreeRows.FirstOrDefault(row => row.Id == parentId);
            if (parent is not null)
            {
                AppendRowsUnder(parent, page.Rows);
            }
        }
        else
        {
            foreach (TreeNodeRow row in PresentRows(page.Rows, append: true))
            {
                TreeRows.Add(row);
            }
        }

        pagedLoaded += page.Rows.Count;
        pagedTotal = page.TotalCount;
        TreeTruncated = page.Truncated;
        OnPropertyChanged(nameof(PagingStatus));
        OnPropertyChanged(nameof(TreeRows));
    }

    public void RevealInTree()
    {
        if (SelectedNode is null)
        {
            return;
        }

        long targetId = SelectedNode.Id;
        List<long> chain = [.. nodeBrowser.GetAncestors(targetId).Select(static row => row.Id), targetId];
        DecidePane = DecidePane.Files;
        FilesViewMode = FilesViewMode.Tree;
        ReloadView();
        for (int i = 0; i < chain.Count - 1; i++)
        {
            TreeNodeRow? parent = i == 0
                ? PageUntilRootVisible(chain[i])
                : TreeRows.FirstOrDefault(row => row.Id == chain[i]);
            if (parent is null)
            {
                continue;
            }

            ExpandUntil(parent, chain[i + 1]);
        }

        SelectedNode = TreeRows.FirstOrDefault(row => row.Id == targetId) ?? nodeBrowser.GetNode(targetId);
    }

    public void SearchNow()
    {
        FilesViewMode = FilesViewMode.Search;
        ReloadView();
    }

    public void OpenHelp()
    {
        HelpVisible = true;
        SelectedHelpTopic ??= HelpTopics[0];
        if (selectedHelpTopic is HelpTopic topic)
        {
            LoadHelp(topic);
        }
    }

    private void LoadHelp(HelpTopic topic)
    {
        try
        {
            HelpText = localHelp.ReadDisplayText(topic.FileName);
        }
        catch (Exception exception)
        {
            ShowHandledFailure(exception);
        }
    }

    public void OpenLog()
    {
        try
        {
            LogText = SessionLogReader.Read(safeFs, workspace.LogPath, new SensitiveDataRedactor());
            LogVisible = true;
        }
        catch (Exception exception)
        {
            ShowHandledFailure(exception);
        }
    }

    public void SetWindowWidth(double width)
    {
        CompactLayout = width < 1200;
        if (!CompactLayout)
        {
            CompactInspect = false;
        }
    }

    public async Task<string> RunPublishedMemoryProbeAsync(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        DismissFirstRun();
        InterruptedRestoreVisible = false;
        SourceCandidate candidate = sourceDiscovery.InspectBrowsedPath(sourcePath, cleanupTaskPresent: false);
        if (!Sources.Any(existing => existing.Path.Equals(candidate.Path, StringComparison.OrdinalIgnoreCase)))
        {
            Sources.Add(candidate);
        }

        SelectedSourcePath = candidate.Path;
        string report = await PublishedScanProbe.RunAsync(
                sessionDb,
                safeFs,
                sourceGuard,
                workspace.SessionId,
                candidate.Path,
                workspace.TemporaryPath)
            .ConfigureAwait(true);
        scanCompleted = true;
        CurrentStep = WorkflowStep.Decide;
        DecidePane = DecidePane.Files;
        ReloadView();
        OnPropertyChanged(nameof(ScanCompleted));
        return report;
    }

    /// <summary>
    /// Opt-in FlaUI fixture: browse a TEMP folder, set the restore destination, and
    /// force manual delete. Never selects a volume-root Windows.old* path, so a smoke
    /// run cannot arm Previous Installations cleanup against the live install.
    /// </summary>
    public bool TryApplySmokeFixture(string sourcePath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        string source = Path.GetFullPath(sourcePath);
        string destination = Path.GetFullPath(destinationPath);
        if (TouchesVolumeRootPreviousInstallation(source) ||
            TouchesVolumeRootPreviousInstallation(destination))
        {
            return false;
        }

        if (!Directory.Exists(source))
        {
            return false;
        }

        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith(
                source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        safeFs.CreateDirectory(destination);
        PreferCleanupHandler = false;
        InterruptedRestoreVisible = false;
        CurrentStep = WorkflowStep.Scan;
        SourceCandidate candidate = sourceDiscovery.InspectBrowsedPath(source, cleanupTaskPresent: false);
        if (!Sources.Any(existing => existing.Path.Equals(candidate.Path, StringComparison.OrdinalIgnoreCase)))
        {
            Sources.Add(candidate);
        }

        SelectedSourcePath = candidate.Path;
        DestinationRoot = destination;
        ScanStatus = "Smoke fixture ready (" + Path.GetFileName(source) + "). Windows.old has not been touched.";
        return true;
    }

    internal static bool TouchesVolumeRootPreviousInstallation(string path)
    {
        string current = Path.GetFullPath(path);
        string? volume = Path.GetPathRoot(current);
        if (string.IsNullOrEmpty(volume))
        {
            return false;
        }

        string volumeTrim = volume.TrimEnd(Path.DirectorySeparatorChar);
        while (!string.IsNullOrEmpty(current))
        {
            string trimmed = current.TrimEnd(Path.DirectorySeparatorChar);
            string? parent = Path.GetDirectoryName(trimmed);
            if (parent is null)
            {
                break;
            }

            string parentTrim = parent.TrimEnd(Path.DirectorySeparatorChar);
            if (parentTrim.Equals(volumeTrim, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(trimmed).StartsWith("Windows.old", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (parentTrim.Equals(volumeTrim, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        return false;
    }

    private void DismissFirstRun()
    {
        firstRun.Dismiss();
        FirstRunVisible = false;
    }

    public void OfferInterruptedRestore(InterruptedRestoreReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        destinationByRelPath = DestinationMap.Parse(sessionDb.GetKv(workspace.SessionId, DestinationMap.KvKey));
        lastPlan = report.Plan;
        if (report.SourceRoot is not null)
        {
            SourceRoot = report.SourceRoot;
            sourceGuard.RegisterSourceRoot(report.SourceRoot);
        }

        if (report.DestinationRoot is not null)
        {
            DestinationRoot = report.DestinationRoot;
        }

        scanCompleted = true;
        ApplyPreflight();
        if (report.Plan is RestorePlan plan)
        {
            RebuildRestoreJobs(plan);
            RefreshRestoreJobStatuses();
        }
        OnPropertyChanged(nameof(SourceRoot));
        OnPropertyChanged(nameof(ScanCompleted));
        ExecuteRestoreCommand.NotifyCanExecuteChanged();
        ResumeInterruptedCommand.NotifyCanExecuteChanged();
        PreparePreviewCommand.NotifyCanExecuteChanged();
        InterruptedRestoreText =
            "A restore was interrupted on " +
            report.InterruptedAt.ToLocalTime().ToString("d MMM yyyy 'at' HH:mm", CultureInfo.InvariantCulture) +
            ". " + report.IncompleteItems + " items were left unfinished; " +
            report.CompletedItems + " already finished. Resume?";
        InterruptedRestoreVisible = true;
        CurrentStep = WorkflowStep.Restore;
        OnPropertyChanged(nameof(ScanCompleted));
        OnPropertyChanged(nameof(WindowTitle));
    }

    private async Task ResumeInterruptedAsync()
    {
        InterruptedRestoreVisible = false;
        await ExecuteRestoreAsync().ConfigureAwait(true);
    }

    private void DismissInterrupted()
    {
        InterruptedRestoreVisible = false;
    }

    private void BrowseDestination()
    {
        if (!CanWriteSession)
        {
            return;
        }

        string? path = folderPicker.PickFolder();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (CanEditDestination)
        {
            PlannedDestinationPath = path;
            return;
        }

        DestinationRoot = path;
    }

    private Task PersistDestinationMapAsync()
    {
        return sessionDb.SetKvAsync(
            workspace.SessionId,
            DestinationMap.KvKey,
            DestinationMap.Format(destinationByRelPath));
    }

    private async Task ResumeDiskFullAsync()
    {
        RefreshPreflight();
        if (lastPreflight is not { CanProceed: true })
        {
            if (lastPlan is not null)
            {
                RestorePlan pending = PlanProgress.Pending(sessionDb, lastPlan);
                DiskFullText = DiskFullPause.Format(
                    DestinationRoot,
                    pending.TotalBytes,
                    lastPreflight?.FreeBytes ?? 0);
            }

            return;
        }

        DiskFullVisible = false;
        await ExecuteRestoreAsync().ConfigureAwait(true);
    }

    private void CancelDiskFull()
    {
        DiskFullVisible = false;
        restoreCompleted = false;
        ExecuteVerifyCommand.NotifyCanExecuteChanged();
        ScanStatus = "Restore paused for disk space was cancelled. Already copied files were kept. Windows.old was not changed.";
    }

    private void CancelRestore()
    {
        restoreCancellation?.Cancel();
    }

    private void CancelPurge()
    {
        purgeCancellation?.Cancel();
    }

    private void PauseRestore()
    {
        restorePause?.Cancel();
        PauseRestoreCommand.NotifyCanExecuteChanged();
    }

    private void ReviewUndecided()
    {
        CurrentStep = WorkflowStep.Decide;
        DecidePane = DecidePane.Files;
        FilesViewMode = FilesViewMode.Unknown;
    }

    private void ApplySubfolderPolicy()
    {
        if (subfolderPolicy == SubfolderPolicy.MergeIntoProfile)
        {
            DestinationRoot = LiveProfileRoot;
            string user = Environment.UserName;
            foreach (DetectedProfile profile in lastProfiles)
            {
                if (profile.Name.Equals(user, StringComparison.OrdinalIgnoreCase))
                {
                    destinationByRelPath["Users\\" + profile.Name] = LiveProfileRoot;
                }
            }
        }
        else
        {
            DestinationRoot = Path.Combine(LiveProfileRoot, "Recovered");
        }

        OnPropertyChanged(nameof(MergeIntoProfile));
        _ = PersistDestinationMapAsync();
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
        PauseScanCommand.NotifyCanExecuteChanged();
        scanCancellation = new CancellationTokenSource();
        Progress<WalkProgress> progress = new(report =>
        {
            NodesVisited = report.NodesVisited;
            CurrentPathFull = report.CurrentRelativePath;
            CurrentPath = PathDisplay.MiddleEllipsis(report.CurrentRelativePath);
            if (report.ProfileNames is { Count: > 0 })
            {
                scanProfileNames = report.ProfileNames;
            }

            ScanStatus = StatusStrip.FormatScanProgress(report, scanProfileNames);
        });

        try
        {
            if (!safeFs.DirectoryExists(SelectedSourcePath))
            {
                throw new DirectoryNotFoundException(
                    "The chosen Windows.old folder was not found.");
            }

            bool resume = scanPaused &&
                string.Equals(pausedSourcePath, SelectedSourcePath, StringComparison.OrdinalIgnoreCase);
            scanPaused = false;
            pausedSourcePath = null;
            scanAbortRequested = false;
            OnPropertyChanged(nameof(ScanButtonLabel));
            if (!resume)
            {
                ResetWorkflowAfterFreshScan();
            }

            ScanRunResult result = await scanOrchestrator.RunAsync(
                    workspace.SessionId,
                    SelectedSourcePath,
                    workspace.TemporaryPath,
                    progress: progress,
                    resume: resume,
                    computeFolderSizes: computeFolderSizes,
                    cancellationToken: scanCancellation.Token)
                .ConfigureAwait(true);
            await sessionDb.SetKvAsync(
                    workspace.SessionId,
                    "scan.computeFolderSizes",
                    computeFolderSizes ? "1" : "0",
                    scanCancellation.Token)
                .ConfigureAwait(true);
            await sessionDb.SetKvAsync(
                    workspace.SessionId,
                    "scan.hashDuringScan",
                    hashDuringScan ? "1" : "0",
                    scanCancellation.Token)
                .ConfigureAwait(true);
            SourceRoot = result.SourceRoot;
            scanCompleted = true;
            int? hashed = null;
            if (HashDuringScan)
            {
                FileHashingPass hasher = new(sessionDb, safeFs);
                hashed = await hasher.HashSessionFilesAsync(
                        workspace.SessionId,
                        result.SourceRoot,
                        scanCancellation.Token)
                    .ConfigureAwait(true);
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
            }

            ScanStatus = StatusStrip.FormatScanSummary(
                result.Walk.NodesVisited,
                result.Walk.BytesSeen,
                result.Profiles.Count,
                lastRecipeCards.Count,
                lastClassification?.HighValueCount ?? 0,
                hashed);

            RebuildCards(lastProfiles);
            CurrentStep = WorkflowStep.Decide;
            DecidePane = DecidePane.Cards;
            ReloadView();
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(ScanCompleted));
            PreparePreviewCommand.NotifyCanExecuteChanged();
            ExecuteRestoreCommand.NotifyCanExecuteChanged();
            ReviewUndecidedCommand.NotifyCanExecuteChanged();
            ExecuteVerifyCommand.NotifyCanExecuteChanged();
            ExecutePurgeCommand.NotifyCanExecuteChanged();
            CreateSupportBundleCommand.NotifyCanExecuteChanged();
            AnalyzeGitCommand.NotifyCanExecuteChanged();
        }
        catch (OperationCanceledException)
        {
            if (scanAbortRequested)
            {
                await sessionDb.ClearScanDataAsync(workspace.SessionId).ConfigureAwait(true);
                ResetWorkflowAfterFreshScan();
                scanCompleted = false;
                SourceRoot = null;
                ReloadView();
                CurrentStep = WorkflowStep.Scan;
                OnPropertyChanged(nameof(ScanCompleted));
                OnPropertyChanged(nameof(WindowTitle));
                ScanStatus = "Scan cancelled. Choose Scan to start again.";
            }
            else
            {
                scanPaused = true;
                pausedSourcePath = SelectedSourcePath;
                OnPropertyChanged(nameof(ScanButtonLabel));
                ScanStatus = "Scan paused. Partial results were kept. Choose Resume scan to continue.";
            }
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
            PauseScanCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task BrowseSourceAsync()
    {
        string? path = folderPicker.PickFolder();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            CleanupTaskStatus cleanupTask = await sourceDiscovery.QueryCleanupTaskAsync()
                .ConfigureAwait(true);
            SourceCandidate candidate = sourceDiscovery.InspectBrowsedPath(path, cleanupTask);
            if (!Sources.Any(existing => existing.Path.Equals(candidate.Path, StringComparison.OrdinalIgnoreCase)))
            {
                Sources.Add(candidate);
            }

            SelectedSourcePath = candidate.Path;
        }
        catch (Exception exception)
        {
            ShowHandledFailure(exception);
        }
    }

    private void RefreshSourceHint()
    {
        SourceCandidate? selected = Sources.FirstOrDefault(
            candidate => candidate.Path.Equals(SelectedSourcePath, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            SourceHint = string.Empty;
            return;
        }

        string users = selected.HasUsersFolder
            ? string.Empty
            : "This folder does not contain Users. Scan can continue, but it may not be a Windows installation. ";
        SourceHint = users + selected.DeletionWarning;
    }

    private void PauseScan()
    {
        scanAbortRequested = false;
        scanCancellation?.Cancel();
    }

    private void AbortScan()
    {
        scanAbortRequested = true;
        scanCancellation?.Cancel();
    }

    private void ResetWorkflowAfterFreshScan()
    {
        lastPlan = null;
        lastPreflight = null;
        restoreCompleted = false;
        verifyCompleted = false;
        Conflicts.Clear();
        ClearSyncthingMappings(hideEditor: true);
        destinationByRelPath.Clear();
        DiskFullVisible = false;
        PreviewSummaryText = string.Empty;
        PurgeSummaryText = string.Empty;
        RestoreProgress = string.Empty;
        ApplyRestoreSkips(null);
        runningApps = [];
        scanProfileNames = [];
        SourceIntegrityText = "Windows.old untouched";
        SpaceBudgetText = "Selected: — of destination free space";
        SpaceBudgetLevel = SpaceBudgetLevel.Idle;
        ExecuteRestoreCommand.NotifyCanExecuteChanged();
        ExecuteVerifyCommand.NotifyCanExecuteChanged();
        ExecutePurgeCommand.NotifyCanExecuteChanged();
        CreateSupportBundleCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(RestoreCompleted));
        OnPropertyChanged(nameof(VerifyCompleted));
        OnPropertyChanged(nameof(OverwriteButtonLabel));
    }

    private bool CanMutateSelection()
    {
        return CanWriteSession &&
            CurrentStep == WorkflowStep.Decide &&
            (SelectedRecipeCard is not null ||
                DecisionTargets().Any(static row => row.CanRestore));
    }

    private IReadOnlyList<TreeNodeRow> DecisionTargets()
    {
        if (selectedNodes.Count > 0)
        {
            return selectedNodes;
        }

        return SelectedNode is null ? [] : [SelectedNode];
    }

    private void ActivateOverviewCard(OverviewCard? card)
    {
        if (card is null)
        {
            return;
        }

        SelectedCard = card;
        if (SelectedNode is TreeNodeRow node && card.NodeId is not null)
        {
            ReplaceSelection([node]);
        }
        else if (card.NodeId is null)
        {
            ReplaceSelection([]);
        }
    }

    private void InspectOverviewCard(OverviewCard? card)
    {
        if (card is null || !card.ShowVerbs)
        {
            return;
        }

        ActivateOverviewCard(card);
        if (CompactLayout)
        {
            CompactInspect = true;
        }
    }

    private async Task OpenOverviewCardAsync(OverviewCard? card)
    {
        if (card is null || !card.ShowVerbs)
        {
            return;
        }

        ActivateOverviewCard(card);
        if (!CanOpenFolder())
        {
            return;
        }

        await OpenFolderAsync().ConfigureAwait(true);
    }

    private bool CanAnalyzeGit()
    {
        return CanWriteSession &&
            scanCompleted &&
            GitCardSelected &&
            lastRecipeCards.Any(IsGitRepoCard);
    }

    private static bool IsGitRepoCard(RecipeCard card)
    {
        return card.RecipeId.Equals("git", StringComparison.Ordinal) &&
            card.Facts.GetValueOrDefault("kind") == "repo";
    }

    private static IEnumerable<string> RecipeRiskLines(RecipeCard recipe)
    {
        if (recipe.Facts.TryGetValue("risk", out string? risk) &&
            !string.IsNullOrWhiteSpace(risk))
        {
            yield return "Risk: " + risk;
        }
    }

    private async Task AnalyzeGitAsync()
    {
        if (!CanAnalyzeGit())
        {
            return;
        }

        ScanStatus = "Analyzing Git repositories…";
        List<RecipeCard> updated = [.. lastRecipeCards];
        for (int index = 0; index < updated.Count; index++)
        {
            RecipeCard card = updated[index];
            if (!IsGitRepoCard(card) ||
                card.Facts.GetValueOrDefault("vendored") == "1" ||
                !card.Facts.TryGetValue("source", out string? source) ||
                string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            GitAnalyzeResult result = await GitAnalyze.AnalyzeAsync(
                    processRunner,
                    safeFs,
                    source,
                    LiveProfileRoot,
                    workspace.TemporaryPath)
                .ConfigureAwait(true);
            Dictionary<string, string> facts = new(card.Facts, StringComparer.Ordinal);
            facts["risk"] = result.Badge;
            Decision suggested = string.Equals(result.Badge, "Git: clean, pushed", StringComparison.Ordinal)
                ? Decision.Undecided
                : Decision.Restore;
            RecipeComponent[] components = card.Components
                .Select(component => component.Key == "repo"
                    ? component with { SuggestedDefault = suggested, Summary = result.Badge }
                    : component)
                .ToArray();
            updated[index] = card with { Facts = facts, Components = components };

            if (SourceRoot is not null)
            {
                string relative = Path.GetRelativePath(SourceRoot, source);
                if (!relative.StartsWith("..", StringComparison.Ordinal) &&
                    sessionDb.FindNodeId(workspace.SessionId, relative) is long nodeId)
                {
                    await sessionDb.ReplaceKindBadgesAsync(nodeId, "Git", [result.Badge])
                        .ConfigureAwait(true);
                }
            }
        }

        lastRecipeCards = updated;
        RebuildCards(lastProfiles);
        ReloadView();
        OnPropertyChanged(nameof(SelectedRecipeCard));
        OnPropertyChanged(nameof(DetailText));
        ScanStatus = "Git repositories analyzed. Risk badges are on the cards and in the tree.";
    }

    private async Task DecideOverviewCardAsync(OverviewCard? card, Decision decision)
    {
        if (card is null || !card.ShowVerbs)
        {
            return;
        }

        ActivateOverviewCard(card);
        if (!CanMutateSelection())
        {
            return;
        }

        await ApplyDecisionAsync(decision).ConfigureAwait(true);
    }

    private async Task ApplyDecisionAsync(Decision decision)
    {
        if (SelectedRecipeCard is RecipeCard card)
        {
            await ApplyRecipeComponentDecisionAsync(card, decision).ConfigureAwait(true);
            ReloadRecipeComponents();
            RebuildCards(lastProfiles);
            return;
        }

        IReadOnlyList<TreeNodeRow> targets = DecisionTargets().Where(static row => row.CanRestore).ToArray();
        if (targets.Count == 0)
        {
            return;
        }

        await decisionEngine.ApplyUserDecisionsAsync(
                targets.Select(static row => row.Id).ToArray(),
                decision)
            .ConfigureAwait(true);
        RefreshAfterDecision();
        UndoDecisionCommand.NotifyCanExecuteChanged();
    }

    private async Task ClearDecisionAsync()
    {
        if (SelectedRecipeCard is RecipeCard card)
        {
            await ApplyRecipeComponentDecisionAsync(card, Decision.Undecided).ConfigureAwait(true);
            ReloadRecipeComponents();
            RebuildCards(lastProfiles);
            return;
        }
        IReadOnlyList<TreeNodeRow> targets = DecisionTargets();
        if (targets.Count == 0)
        {
            return;
        }

        foreach (TreeNodeRow row in targets)
        {
            await decisionEngine.ClearUserDecisionAsync(row.Id).ConfigureAwait(true);
        }

        RefreshAfterDecision();
        UndoDecisionCommand.NotifyCanExecuteChanged();
    }

    private async Task UndoDecisionAsync()
    {
        if (!decisionEngine.CanUndo)
        {
            return;
        }

        await decisionEngine.UndoAsync().ConfigureAwait(true);
        RefreshAfterDecision();
        UndoDecisionCommand.NotifyCanExecuteChanged();
    }

    private async Task ApplyRecipeComponentDecisionAsync(RecipeCard card, Decision decision)
    {
        foreach (RecipeComponent component in card.Components)
        {
            if (component.Fixed)
            {
                continue;
            }

            await sessionDb.SetKvAsync(
                    workspace.SessionId,
                    StoredRecipeDecisions.KvKey(card.InstanceKey, component.Key),
                    decision.ToString())
                .ConfigureAwait(true);
        }
    }

    private void ReloadRecipeComponents()
    {
        foreach (RecipeComponentChoice row in RecipeComponents)
        {
            row.Changed -= OnRecipeComponentChanged;
        }

        RecipeComponents.Clear();
        if (SelectedRecipeCard is not RecipeCard card)
        {
            return;
        }

        Dictionary<string, Decision> stored = StoredRecipeDecisions.Load(sessionDb, workspace.SessionId, card);
        foreach (RecipeComponent component in card.Components)
        {
            RecipeComponentChoice choice = new(component, stored[component.Key]);
            choice.Changed += OnRecipeComponentChanged;
            RecipeComponents.Add(choice);
        }
    }

    private void OnRecipeComponentChanged(object? sender, EventArgs e)
    {
        if (sender is not RecipeComponentChoice choice || SelectedRecipeCard is not RecipeCard card)
        {
            return;
        }

        _ = sessionDb.SetKvAsync(
            workspace.SessionId,
            StoredRecipeDecisions.KvKey(card.InstanceKey, choice.Key),
            choice.Decision.ToString());
        RebuildCards(lastProfiles);
    }

    private async Task RestoreExceptRegeneratableAsync()
    {
        await ApplyMatchingAsync(filesOnly: false, excludeRegeneratable: true, minMtimeUtc: null)
            .ConfigureAwait(true);
    }

    private async Task RestoreNewerAsync()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-RecentDays);
        await ApplyMatchingAsync(filesOnly: true, excludeRegeneratable: false, cutoff)
            .ConfigureAwait(true);
    }

    private async Task ApplyMatchingAsync(
        bool filesOnly,
        bool excludeRegeneratable,
        DateTimeOffset? minMtimeUtc)
    {
        IReadOnlyList<TreeNodeRow> targets = DecisionTargets().Where(static row => row.CanRestore).ToArray();
        if (targets.Count == 0)
        {
            return;
        }

        foreach (TreeNodeRow row in targets)
        {
            await decisionEngine.ApplyUserDecisionToMatchingAsync(
                    row.Id,
                    Decision.Restore,
                    filesOnly,
                    excludeRegeneratable,
                    minMtimeUtc)
                .ConfigureAwait(true);
        }

        RefreshAfterDecision();
    }

    private bool CanOpenFolder()
    {
        if (SelectedRecipeCard is RecipeCard card && RecipeSourcePaths.OpenPath(card) is not null)
        {
            return true;
        }

        return SelectedNode is not null && SourceRoot is not null;
    }

    private async Task OpenFolderAsync()
    {
        string? path = SelectedRecipeCard is RecipeCard card
            ? RecipeSourcePaths.OpenPath(card)
            : null;
        if (path is null)
        {
            if (SelectedNode is null || SourceRoot is null)
            {
                return;
            }

            path = Path.Combine(SourceRoot, SelectedNode.RelPath);
        }

        await processRunner.RunAsync(
                new ProcessRequest("explorer.exe", [ExplorerSelect.BuildSelectArgument(path)]))
            .ConfigureAwait(true);
    }

    private void ReloadView()
    {
        TreeRows.Clear();
        NodePage page = FilesViewMode switch
        {
            FilesViewMode.Largest => CombineLargest(),
            FilesViewMode.Recent => nodeBrowser.GetRecent(RecentDays, null),
            FilesViewMode.Search => string.IsNullOrWhiteSpace(SearchText)
                ? new NodePage([], 0, false)
                : nodeBrowser.Search(SearchText, null),
            FilesViewMode.Unknown => nodeBrowser.GetUnknown(null),
            FilesViewMode.Problems => nodeBrowser.GetProblems(),
            _ => nodeBrowser.GetChildren(null),
        };

        foreach (TreeNodeRow row in PresentRows(page.Rows, append: false))
        {
            TreeRows.Add(row);
        }

        ApplyPaging(page, null);
        OnPropertyChanged(nameof(TreeRows));
        if (SelectedNode is not null)
        {
            SelectedNode = TreeRows.FirstOrDefault(row => row.Id == SelectedNode.Id) ?? SelectedNode;
        }
    }

    private IReadOnlyList<TreeNodeRow> PresentRows(IReadOnlyList<TreeNodeRow> rows, bool append)
    {
        if (FilesViewMode != FilesViewMode.Recent)
        {
            return rows;
        }

        long? continueParent = null;
        if (append)
        {
            for (int i = TreeRows.Count - 1; i >= 0; i--)
            {
                if (!TreeRows[i].IsGroupHeader)
                {
                    continueParent = TreeRows[i].ParentId;
                    break;
                }
            }
        }

        return RecentGroups.InsertHeaders(rows, nodeBrowser, continueParent);
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
        string? keepTitle = selectedCard?.Title;
        string? keepKind = selectedCard?.Kind;
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
                    DecisionDisplay.Label(Decision.Undecided, ownUser: false, suggested: false),
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
                        node?.DecisionLabel ?? DecisionDisplay.Label(Decision.Undecided, ownUser: false, suggested: false),
                        node?.Id,
                        "PersonalFolder",
                        node?.DecisionTooltip ?? string.Empty));
            }
        }

        if (lastClassification is not null)
        {
            Cards.Add(
                new OverviewCard(
                    "High-value items",
                    "Password vaults, libraries, VM disks, and similar files.",
                    $"{lastClassification.HighValueCount} items, {lastClassification.HighValueBytes} bytes",
                    DecisionDisplay.Label(Decision.Undecided, ownUser: false, suggested: false),
                    NodeId: null,
                    "HighValue"));
            Cards.Add(
                new OverviewCard(
                    "Regeneratable",
                    "Caches and installers. Badged only — never left behind automatically.",
                    $"{lastClassification.RegeneratableCount} folders or files, {lastClassification.RegeneratableBytes} bytes",
                    DecisionDisplay.Label(Decision.Undecided, ownUser: false, suggested: false),
                    NodeId: null,
                    "Regeneratable"));
        }

        foreach (RecipeCard recipe in lastRecipeCards)
        {
            bool restoreSuggested = recipe.Components.Any(
                static component => component.SuggestedDefault == Decision.Restore);
            string facts = recipe.WhyItMatters;
            if (recipe.Facts.TryGetValue("risk", out string? risk) &&
                !string.IsNullOrWhiteSpace(risk))
            {
                facts = "[" + risk + "] " + facts;
            }

            Cards.Add(
                new OverviewCard(
                    recipe.Title,
                    recipe.What,
                    facts,
                    restoreSuggested
                        ? DecisionDisplay.Label(Decision.Restore, ownUser: false, suggested: true)
                        : DecisionDisplay.Label(Decision.Undecided, ownUser: false, suggested: false),
                    NodeId: null,
                    recipe.RecipeId,
                    restoreSuggested ? DecisionDisplay.SuggestedTooltip : string.Empty));
        }

        if (recipeHost is not null)
        {
            HashSet<string> found = lastRecipeCards
                .Select(static card => card.RecipeId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach ((string id, string title) in RecipeCatalog.AbsentLabels)
            {
                if (found.Contains(id))
                {
                    continue;
                }

                Cards.Add(
                    new OverviewCard(
                        title + ": none found in this profile",
                        string.Empty,
                        string.Empty,
                        "Looked",
                        NodeId: null,
                        "Absent"));
            }
        }

        if (keepTitle is not null && keepKind is not null)
        {
            OverviewCard? match = Cards.FirstOrDefault(card =>
                string.Equals(card.Title, keepTitle, StringComparison.Ordinal) &&
                string.Equals(card.Kind, keepKind, StringComparison.Ordinal));
            if (!Equals(selectedCard, match))
            {
                selectedCard = match;
                OnPropertyChanged(nameof(SelectedCard));
                OnPropertyChanged(nameof(SelectedRecipeCard));
                OnPropertyChanged(nameof(RecipeCardSelected));
                OnPropertyChanged(nameof(GitCardSelected));
                ReloadRecipeComponents();
                AnalyzeGitCommand.NotifyCanExecuteChanged();
            }
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
        int index = IndexOfRow(parent.Id);
        if (index < 0)
        {
            return;
        }

        int end = ExclusiveSubtreeEnd(index);
        for (int i = end - 1; i > index; i--)
        {
            TreeRows.RemoveAt(i);
        }

        int insertAt = index + 1;
        foreach (TreeNodeRow child in children)
        {
            TreeRows.Insert(insertAt++, child);
        }

        OnPropertyChanged(nameof(TreeRows));
    }

    private void AppendRowsUnder(TreeNodeRow parent, IReadOnlyList<TreeNodeRow> children)
    {
        int index = IndexOfRow(parent.Id);
        if (index < 0)
        {
            return;
        }

        int insertAt = ExclusiveSubtreeEnd(index);
        foreach (TreeNodeRow child in children)
        {
            TreeRows.Insert(insertAt++, child);
        }
    }

    private int IndexOfRow(long id)
    {
        for (int i = 0; i < TreeRows.Count; i++)
        {
            if (TreeRows[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private int ExclusiveSubtreeEnd(int parentIndex)
    {
        HashSet<long> inside = [TreeRows[parentIndex].Id];
        int i = parentIndex + 1;
        while (i < TreeRows.Count)
        {
            long? parentId = TreeRows[i].ParentId;
            if (parentId is long id && inside.Contains(id))
            {
                inside.Add(TreeRows[i].Id);
                i++;
                continue;
            }

            break;
        }

        return i;
    }

    private void ApplyPaging(NodePage page, long? parentId)
    {
        pagedParentId = parentId;
        pagedLoaded = page.Rows.Count;
        pagedTotal = page.TotalCount;
        TreeTruncated = page.Truncated;
        OnPropertyChanged(nameof(PagingStatus));
    }

    private void ExpandUntil(TreeNodeRow parent, long childId)
    {
        Expand(parent);
        while (TreeRows.All(row => row.Id != childId) && TreeTruncated && pagedParentId == parent.Id)
        {
            ShowMore();
        }
    }

    private TreeNodeRow? PageUntilRootVisible(long id)
    {
        TreeNodeRow? row = TreeRows.FirstOrDefault(item => item.Id == id);
        while (row is null && TreeTruncated && pagedParentId is null)
        {
            ShowMore();
            row = TreeRows.FirstOrDefault(item => item.Id == id);
        }

        return row;
    }

    private void RefreshAfterDecision()
    {
        if (FilesViewMode == FilesViewMode.Tree)
        {
            for (int i = 0; i < TreeRows.Count; i++)
            {
                TreeNodeRow? fresh = nodeBrowser.GetNode(TreeRows[i].Id);
                if (fresh is not null)
                {
                    TreeRows[i] = fresh;
                }
            }

            OnPropertyChanged(nameof(TreeRows));
        }
        else
        {
            ReloadView();
        }

        if (SelectedNode is not null)
        {
            SelectedNode = nodeBrowser.GetNode(SelectedNode.Id) ?? SelectedNode;
        }

        RebuildCards(lastProfiles);
        OnPropertyChanged(nameof(DetailText));
        UndoDecisionCommand.NotifyCanExecuteChanged();
    }

    private void OnConflictApprovedChanged()
    {
        OnPropertyChanged(nameof(OverwriteButtonLabel));
        ApproveOverwritesCommand.NotifyCanExecuteChanged();
    }

    private void SetRestoring(bool restoring)
    {
        isRestoring = restoring;
        OnPropertyChanged(nameof(CanWriteSession));
        NotifyWriteCommands();
        CancelRestoreCommand.NotifyCanExecuteChanged();
        PauseRestoreCommand.NotifyCanExecuteChanged();
        GoToPurgeCommand.NotifyCanExecuteChanged();
    }

    private void SetPurging(bool purging)
    {
        isPurging = purging;
        OnPropertyChanged(nameof(IsPurging));
        OnPropertyChanged(nameof(CanWriteSession));
        NotifyWriteCommands();
        CancelPurgeCommand.NotifyCanExecuteChanged();
        ExecutePurgeCommand.NotifyCanExecuteChanged();
        GoToPurgeCommand.NotifyCanExecuteChanged();
    }

    internal void SetRestoringForTests(bool restoring)
    {
        SetRestoring(restoring);
    }

    internal void ArmRestorePauseForTests()
    {
        restorePause = new CancellationTokenSource();
        SetRestoring(true);
    }

    internal void UnlockPurgeForTests()
    {
        restoreCompleted = true;
        verifyCompleted = true;
        ExecuteVerifyCommand.NotifyCanExecuteChanged();
        ExecutePurgeCommand.NotifyCanExecuteChanged();
        CancelPurgeCommand.NotifyCanExecuteChanged();
        CreateSupportBundleCommand.NotifyCanExecuteChanged();
        AcknowledgeVerifyCommand.NotifyCanExecuteChanged();
    }

    internal void ArmPurgeForTests()
    {
        purgeCancellation = new CancellationTokenSource();
        SetPurging(true);
    }

    internal void MarkRestoreCompletedForTests()
    {
        restoreCompleted = true;
        verifyCompleted = false;
        ExecuteVerifyCommand.NotifyCanExecuteChanged();
        AcknowledgeVerifyCommand.NotifyCanExecuteChanged();
        ExecutePurgeCommand.NotifyCanExecuteChanged();
    }

    internal void RefreshSessionLockForTests()
    {
        ApplySessionLockFromStore();
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
                new PlanRequest(
                    workspace.SessionId,
                    SourceRoot,
                    DestinationRoot,
                    ConflictPolicy: selectedConflictPolicy,
                    DestinationByRelPath: destinationByRelPath.Count == 0 ? null : destinationByRelPath))
            .ConfigureAwait(true);
        await sessionDb.SetKvAsync(workspace.SessionId, InterruptedRestore.SourceRootKey, SourceRoot)
            .ConfigureAwait(true);
        await sessionDb.SetKvAsync(workspace.SessionId, InterruptedRestore.DestinationRootKey, DestinationRoot)
            .ConfigureAwait(true);
        ApplyPreflight();
        Conflicts.Clear();
        if (lastPreflight is not null)
        {
            foreach (PlanConflict conflict in lastPreflight.Conflicts)
            {
                Conflicts.Add(
                    new ConflictRow(
                        conflict.DestinationPath,
                        conflict.ExistingSize,
                        conflict.ExistingWriteUtc,
                        OnConflictApprovedChanged));
            }
        }

        int recipeWrites = 0;
        List<string> recipeLines = [];
        List<string> running = [];
        ClearSyncthingMappings(hideEditor: false);
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

                PlanResult recipePlan = recipeHost.PlanCard(recipe, card, destination, workspace.SessionId);
                recipeWrites += recipePlan.Writes.Count;
                if (card.RecipeId.Equals("syncthing", StringComparison.Ordinal))
                {
                    recipeLines.Add(FormatSyncthingPreviewLine(card, destination));
                    AddSyncthingMappings(card, destination);
                }
                else
                {
                    recipeLines.Add(card.Title + ": " + card.WhatIsRestored);
                }

                foreach (Prerequisite prerequisite in recipe.Prerequisites(recipePlan))
                {
                    if (processPresence.IsRunning(prerequisite.ProcessName))
                    {
                        running.Add(prerequisite.Message);
                    }
                }
            }
        }

        OnPropertyChanged(nameof(HasSyncthingMappings));
        EditSyncthingMappingCommand.NotifyCanExecuteChanged();
        BrowseSyncthingMappingCommand.NotifyCanExecuteChanged();

        runningApps = running;
        PreviewInventory inventory = sessionDb.GetPreviewInventory(workspace.SessionId);
        PreviewSummaryText = PreviewSummary.Format(lastPlan, lastPreflight!, inventory, recipeLines, running);
        (SpaceBudgetText, SpaceBudgetLevel) = StatusStrip.FormatSpaceBudget(
            lastPlan.TotalBytes,
            lastPreflight!.RequiredBytes,
            lastPreflight.FreeBytes);
        ScanStatus = lastPreflight.CanProceed && running.Count == 0
            ? $"Preview: {lastPlan.Items.Count} copy operations, {Conflicts.Count} conflicts, and {recipeWrites} app writes. Windows.old has not been changed."
            : running.Count > 0
                ? string.Join(" ", running)
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

    private void ClearSyncthingMappings(bool hideEditor)
    {
        SyncthingMappings.Clear();
        if (hideEditor)
        {
            SyncthingMappingEditorVisible = false;
        }

        OnPropertyChanged(nameof(HasSyncthingMappings));
    }

    private IReadOnlyList<SyncthingFolderMapping> LoadSyncthingMappings(
        RecipeCard card,
        DestinationContext destination)
    {
        if (!card.Facts.TryGetValue("source", out string? home) ||
            string.IsNullOrWhiteSpace(home))
        {
            return [];
        }

        string config = Path.Combine(home, "config.xml");
        if (!safeFs.FileExists(config))
        {
            return [];
        }

        IReadOnlyDictionary<string, string> overrides = RecipeFolderMap.Parse(
            sessionDb.GetKv(workspace.SessionId, RecipeFolderMap.KvKey(card.InstanceKey)));
        return SyncthingConfig.PlanMappings(
            safeFs.ReadAllText(config),
            card.Facts.GetValueOrDefault("oldProfile") ?? string.Empty,
            destination.DestinationProfileRoot,
            overrides.Count == 0 ? null : overrides);
    }

    private string FormatSyncthingPreviewLine(RecipeCard card, DestinationContext destination)
    {
        IReadOnlyList<SyncthingFolderMapping> mappings = LoadSyncthingMappings(card, destination);
        int remapped = mappings.Count(static mapping =>
            !string.Equals(mapping.SourcePath, mapping.PlannedPath, StringComparison.OrdinalIgnoreCase));
        string remapText;
        if (remapped == 0)
        {
            remapText = "no profile paths remapped";
        }
        else
        {
            SyncthingFolderMapping first = mappings.First(static mapping =>
                !string.Equals(mapping.SourcePath, mapping.PlannedPath, StringComparison.OrdinalIgnoreCase));
            remapText = remapped + " folder paths remapped (" + first.SourcePath + " → " + first.PlannedPath + ")";
        }

        return card.Title +
            ": identity + config; " +
            mappings.Count +
            " folders will be PAUSED; " +
            remapText;
    }

    private void AddSyncthingMappings(RecipeCard card, DestinationContext destination)
    {
        foreach (SyncthingFolderMapping mapping in LoadSyncthingMappings(card, destination))
        {
            SyncthingMappings.Add(
                new SyncthingMappingRow(
                    card.InstanceKey,
                    mapping.Id,
                    mapping.Label,
                    mapping.SourcePath,
                    mapping.PlannedPath,
                    mapping.ExistsOnDestination,
                    OnSyncthingMappingChanged));
        }
    }

    private void OnSyncthingMappingChanged()
    {
        if (!CanWriteSession)
        {
            return;
        }

        mappingsPersist = PersistSyncthingMappingsAsync();
    }

    internal Task PersistSyncthingMappingsForTestsAsync()
    {
        return mappingsPersist;
    }

    private async Task PersistSyncthingMappingsAsync()
    {
        if (!CanWriteSession)
        {
            return;
        }

        foreach (IGrouping<string, SyncthingMappingRow> group in SyncthingMappings.GroupBy(static row => row.InstanceKey))
        {
            Dictionary<string, string> map = new(StringComparer.Ordinal);
            foreach (SyncthingMappingRow row in group)
            {
                if (!string.IsNullOrWhiteSpace(row.PlannedPath))
                {
                    map[row.FolderId] = row.PlannedPath;
                }
            }

            await sessionDb.SetKvAsync(
                    workspace.SessionId,
                    RecipeFolderMap.KvKey(group.Key),
                    RecipeFolderMap.Format(map))
                .ConfigureAwait(true);
        }

        RefreshPreviewRecipeLines();
    }

    private void RefreshPreviewRecipeLines()
    {
        if (lastPlan is null || lastPreflight is null || recipeHost is null)
        {
            return;
        }

        DestinationContext destination = new(LiveProfileRoot, workspace.ExportsPath, safeFs, processRunner);
        List<string> recipeLines = [];
        foreach (RecipeCard card in lastRecipeCards)
        {
            if (recipeHost.Find(card.RecipeId) is null)
            {
                continue;
            }

            recipeLines.Add(
                card.RecipeId.Equals("syncthing", StringComparison.Ordinal)
                    ? FormatSyncthingPreviewLine(card, destination)
                    : card.Title + ": " + card.WhatIsRestored);
        }

        PreviewSummaryText = PreviewSummary.Format(
            lastPlan,
            lastPreflight,
            sessionDb.GetPreviewInventory(workspace.SessionId),
            recipeLines,
            runningApps);
    }

    private void BrowseSyncthingMapping(SyncthingMappingRow? row)
    {
        if (row is null || !CanWriteSession)
        {
            return;
        }

        string? folder = folderPicker.PickFolder();
        if (!string.IsNullOrWhiteSpace(folder))
        {
            row.PlannedPath = folder;
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
        return CanWriteSession &&
            selectedConflictPolicy == ConflictPolicy.OverwriteApproved &&
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

        RestorePlan pending = PlanProgress.Pending(sessionDb, lastPlan);
        IReadOnlySet<string> approved = OverwriteApprovals.Parse(
            sessionDb.GetKv(workspace.SessionId, OverwriteApprovals.KvKey));
        lastPreflight = new PreflightChecker().Check(pending, selectedConflictPolicy, approved);
    }

    private async Task ExecuteRestoreAsync()
    {
        if (lastPlan is null)
        {
            return;
        }

        try
        {
        SetRestoring(true);
        restoreCancellation = new CancellationTokenSource();
        restorePause = new CancellationTokenSource();
        PauseRestoreCommand.NotifyCanExecuteChanged();
        RebuildRestoreJobs(lastPlan);
        ApplyRestoreSkips(null);
        DateTimeOffset restoreStartedAt = DateTimeOffset.UtcNow;
        Progress<RestoreProgress> progress = new(report =>
        {
            RestoreProgress = RestoreProgressFormat.Line(report, DateTimeOffset.UtcNow - restoreStartedAt);
            RefreshRestoreJobStatuses(report.CompletedItems, report.CurrentName);
        });
        RestoreRunner runner = new(new CopyEngine(sessionDb, safeFs), sessionDb);
        RestoreResult result = await runner.RunAsync(
                lastPlan,
                restoreCancellation.Token,
                progress,
                restorePause.Token)
            .ConfigureAwait(true);
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

                PlanResult recipePlan = recipeHost.PlanCard(recipe, card, destination, workspace.SessionId);
                if (recipePlan.Writes.Count == 0)
                {
                    continue;
                }

                await recipeHost.ExecuteAsync(workspace.SessionId, recipe, recipePlan).ConfigureAwait(true);
            }
        }

        restoreCompleted = result.Completed;
        RefreshRestoreJobStatuses();
        ApplyRestoreSkips(result.Skips);
        if (result.PausedDiskFull)
        {
            RestorePlan pending = PlanProgress.Pending(sessionDb, lastPlan);
            ApplyPreflight();
            DiskFullText = DiskFullPause.Format(
                DestinationRoot,
                pending.TotalBytes,
                lastPreflight?.FreeBytes ?? 0);
            DiskFullVisible = true;
            ResumeDiskFullCommand.NotifyCanExecuteChanged();
            ScanStatus = DiskFullText + " Nothing was deleted to make room. A redacted log is at " +
                workspace.LogPath + ".";
        }
        else if (result.PausedByUser)
        {
            DiskFullVisible = false;
            InterruptedRestoreText =
                "Restore paused. Already copied files were kept. Windows.old was not changed. Resume?";
            InterruptedRestoreVisible = true;
            ResumeInterruptedCommand.NotifyCanExecuteChanged();
            ScanStatus = InterruptedRestoreText + " A redacted log is at " + workspace.LogPath + ".";
        }
        else
        {
            DiskFullVisible = false;
            ScanStatus = result.Completed
                ? "Restore finished. Verify the copies before considering a purge."
                : "Restore did not finish. A redacted log is at " + workspace.LogPath + ".";
        }
        ExecuteVerifyCommand.NotifyCanExecuteChanged();
        SetRestoring(false);
        CurrentStep = WorkflowStep.Restore;
        }
        catch (OperationCanceledException)
        {
            restoreCompleted = false;
            ScanStatus = "Restore cancelled. Already copied files were kept. Windows.old was not changed.";
            CurrentStep = WorkflowStep.Restore;
        }
        catch (Exception exception)
        {
            ShowHandledFailure(exception);
        }
        finally
        {
            restoreCancellation?.Dispose();
            restoreCancellation = null;
            restorePause?.Dispose();
            restorePause = null;
            SetRestoring(false);
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
        List<string> recipeLines = [];
        IReadOnlyList<VerifyResultRow> level3 = [];
        if (recipeHost is not null)
        {
            DestinationContext destination = new(LiveProfileRoot, workspace.ExportsPath, safeFs, processRunner);
            level3 = await recipeHost.CollectLevel3Async(
                    workspace.SessionId,
                    report.ReportId,
                    lastRecipeCards,
                    destination)
                .ConfigureAwait(true);
            await sessionDb.InsertVerifyResultsAsync(level3).ConfigureAwait(true);
            foreach (VerifyResultRow row in level3)
            {
                recipeLines.Add(row.Detail + (row.Ok ? "  ✔" : "  failed"));
            }
        }

        verifyCompleted = report.AllOk && level3.All(static row => row.Ok);

        VerifyFailureText = FormatVerifyFailures(recipeLines);
        string counts = VerifyReportFormat.Counts(report);
        ScanStatus = verifyCompleted
            ? "Verify report:" + Environment.NewLine + counts +
              (recipeLines.Count == 0 ? string.Empty : Environment.NewLine + string.Join(Environment.NewLine, recipeLines))
            : "Verify report: at least one item failed. " + counts +
              " Purge stays locked until you re-restore, re-verify, or acknowledge with a reason. A redacted log is at " +
              workspace.LogPath + ".";
        ExecutePurgeCommand.NotifyCanExecuteChanged();
        CreateSupportBundleCommand.NotifyCanExecuteChanged();
        AcknowledgeVerifyCommand.NotifyCanExecuteChanged();
        CurrentStep = WorkflowStep.Verify;
        }
        catch (Exception exception)
        {
            ShowHandledFailure(exception);
        }
    }

    private async Task ExecutePurgeAsync()
    {
        if (SourceRoot is null || isPurging)
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
                RestoreJobActive: IsScanning || isRestoring,
                PurgeTypedFolderName,
                folderName,
                canonical,
                Environment.ProcessPath,
                workspace.RootPath,
                lastPlan is null ? [destinationRoot] : lastPlan.Items.Select(static item => item.DestinationPath).ToArray(),
                customRootConfirmed),
            sessionDb,
            workspace.SessionId);
        if (!gate.Authorized || gate.Token is null)
        {
            ScanStatus = "Purge blocked: " + string.Join(", ", gate.BlockedGates);
            return;
        }

        purgeCancellation = new CancellationTokenSource();
        CancellationToken token = purgeCancellation.Token;
        Progress<PurgeProgress> progress = new(reported =>
        {
            PurgeProgressText = PurgeProgressFormat.Line(reported);
            ScanStatus = PurgeProgressText;
        });
        SetPurging(true);
        SourceIntegrityText = "Deleting…";
        PurgeProgressText = "Deleting…";
        ScanStatus = "Deleting…";
        SessionRecordExport.Write(safeFs, workspace, sessionDb);
        PurgeExecuteResult result = await new PurgeExecutor()
            .ExecuteAsync(
                new PurgeExecuteRequest(
                    canonical,
                    gate.Token,
                    safeFs,
                    sourceGuard,
                    processRunner,
                    workspace.RootPath,
                    preferCleanupHandler,
                    Progress: progress),
                token)
            .ConfigureAwait(true);
        ScanStatus = result.Completed
            ? "Purge finished (" + result.Method + "). Session records remain in this app's data folder."
            : "Purge did not finish: " + result.Detail + " A redacted log is at " + workspace.LogPath + ".";
        PurgeProgressText = result.Completed ? string.Empty : result.Detail;
        SourceIntegrityText = result.Completed ? "Windows.old removed" : "Windows.old partly deleted";
        CurrentStep = WorkflowStep.Purge;
        if (result.Completed)
        {
            await sessionDb.SetKvAsync(workspace.SessionId, SessionLock.PurgedKvKey, SessionLock.PurgedValue)
                .ConfigureAwait(true);
            ApplySessionLockFromStore();
        }
        }
        catch (Exception exception)
        {
            ShowHandledFailure(exception);
            if (string.Equals(SourceIntegrityText, "Deleting…", StringComparison.Ordinal))
            {
                SourceIntegrityText = "Windows.old partly deleted";
            }
        }
        finally
        {
            purgeCancellation?.Dispose();
            purgeCancellation = null;
            SetPurging(false);
        }
    }

    private void ApplySessionLockFromStore()
    {
        bool locked = SessionLock.IsPurged(sessionDb.GetKv(workspace.SessionId, SessionLock.PurgedKvKey));
        if (sessionReadOnly == locked && locked)
        {
            SourceIntegrityText = "Windows.old removed";
            NotifyWriteCommands();
            return;
        }

        sessionReadOnly = locked;
        if (locked)
        {
            SourceIntegrityText = "Windows.old removed";
        }

        OnPropertyChanged(nameof(SessionReadOnly));
        OnPropertyChanged(nameof(CanMutateSession));
        OnPropertyChanged(nameof(CanWriteSession));
        OnPropertyChanged(nameof(CanEditDestination));
        NotifyWriteCommands();
    }

    private void NotifyWriteCommands()
    {
        ScanCommand.NotifyCanExecuteChanged();
        BrowseSourceCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
        LeaveBehindCommand.NotifyCanExecuteChanged();
        UndecidedCommand.NotifyCanExecuteChanged();
        UndoDecisionCommand.NotifyCanExecuteChanged();
        RestoreExceptRegeneratableCommand.NotifyCanExecuteChanged();
        RestoreNewerCommand.NotifyCanExecuteChanged();
        PreparePreviewCommand.NotifyCanExecuteChanged();
        ExecuteRestoreCommand.NotifyCanExecuteChanged();
        ExecuteVerifyCommand.NotifyCanExecuteChanged();
        ExecutePurgeCommand.NotifyCanExecuteChanged();
        CancelPurgeCommand.NotifyCanExecuteChanged();
        AcknowledgeVerifyCommand.NotifyCanExecuteChanged();
        ResumeInterruptedCommand.NotifyCanExecuteChanged();
        ResumeDiskFullCommand.NotifyCanExecuteChanged();
        BrowseDestinationCommand.NotifyCanExecuteChanged();
        GoToPurgeCommand.NotifyCanExecuteChanged();
        AnalyzeGitCommand.NotifyCanExecuteChanged();
        BrowseSyncthingMappingCommand.NotifyCanExecuteChanged();
    }

    private bool CanAcknowledgeVerify()
    {
        return CanWriteSession &&
            restoreCompleted &&
            !verifyCompleted &&
            VerifyAcknowledgement.IsValidReason(verifyAckReason) &&
            sessionDb.LastVerifyReportId(workspace.SessionId) is not null;
    }

    private async Task AcknowledgeVerifyAsync()
    {
        if (!CanAcknowledgeVerify())
        {
            return;
        }

        string? reportId = sessionDb.LastVerifyReportId(workspace.SessionId);
        if (reportId is null)
        {
            return;
        }

        await sessionDb.SetKvAsync(
                workspace.SessionId,
                VerifyAcknowledgement.KvKey(reportId),
                verifyAckReason.Trim())
            .ConfigureAwait(true);
        verifyCompleted = sessionDb.LastVerifyJobsSettled(workspace.SessionId);
        OnPropertyChanged(nameof(VerifyCompleted));
        ScanStatus = verifyCompleted
            ? "Verify failures acknowledged. Purge can proceed after the remaining gates."
            : "Acknowledgement was stored but verify is still unsettled. Re-run verify or add a longer reason.";
        ExecutePurgeCommand.NotifyCanExecuteChanged();
        AcknowledgeVerifyCommand.NotifyCanExecuteChanged();
        CurrentStep = WorkflowStep.Verify;
    }

    private string FormatVerifyFailures(IReadOnlyList<string> recipeLines)
    {
        List<string> lines = [.. sessionDb.LastVerifyFailureDetails(workspace.SessionId)];
        foreach (string line in recipeLines)
        {
            if (line.Contains("failed", StringComparison.Ordinal))
            {
                lines.Add(line);
            }
        }

        return lines.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, lines);
    }

    private void RebuildRestoreJobs(RestorePlan plan)
    {
        RestoreJobs.Clear();
        foreach (PlanItem item in plan.Items)
        {
            string leaf = Path.GetFileName(item.SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string name = string.IsNullOrWhiteSpace(item.RecipeId)
                ? (string.IsNullOrWhiteSpace(leaf) ? item.SourcePath : leaf)
                : item.RecipeId + " › " + leaf;
            long key = item.Id ?? item.JobId;
            RestoreJobs.Add(new RestoreJobRow(key, name, "waiting"));
        }
    }

    private void RefreshRestoreJobStatuses(int completedItems = -1, string? currentName = null)
    {
        if (lastPlan is null)
        {
            return;
        }

        for (int i = 0; i < RestoreJobs.Count && i < lastPlan.Items.Count; i++)
        {
            PlanItem item = lastPlan.Items[i];
            string? state = item.Id is long id ? sessionDb.GetLatestJournalState(id) : null;
            string status = state switch
            {
                "Completed" or "Skipped" => "done",
                "Failed" => "failed",
                "Started" => "running",
                "Paused" => "paused",
                _ when completedItems >= 0 && i == completedItems => "running",
                _ when completedItems >= 0 && i < completedItems => "done",
                _ when !string.IsNullOrEmpty(currentName) &&
                    RestoreJobs[i].Name.EndsWith(currentName, StringComparison.OrdinalIgnoreCase) &&
                    completedItems == i => "running",
                _ => "waiting",
            };
            RestoreJobs[i].SetStatus(status);
        }
    }

    private void ApplyRestoreSkips(RestoreSkipCounts? skips)
    {
        RestoreSkipCounts counts = skips ?? new RestoreSkipCounts();
        restoreWarningDetail = RestoreSkipFormat.Detail(counts);
        RestoreWarningsExpanded = false;
        RestoreWarningSummary = RestoreSkipFormat.Summary(counts);
        OnPropertyChanged(nameof(RestoreWarningDetail));
    }

    private void RefreshPurgeSummary()
    {
        if (SourceRoot is null)
        {
            PurgeSummaryText = string.Empty;
            return;
        }

        int jobs = sessionDb.ListPlanItems(workspace.SessionId).Count;
        bool settled = sessionDb.LastVerifyJobsSettled(workspace.SessionId) || jobs == 0;
        long sourceBytes = 0;
        foreach (TreeNodeRow row in nodeBrowser.GetChildren(null).Rows)
        {
            sourceBytes += row.AggSize;
        }

        long free = 0;
        try
        {
            free = new Win32FreeSpaceProvider().GetFreeBytes(SourceRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        PurgeSummaryText = PurgeSummary.Format(
            jobs,
            settled,
            sessionDb.LastVerifyRecordedAtUtc(workspace.SessionId),
            sessionDb.GetPreviewInventory(workspace.SessionId),
            sourceBytes,
            free,
            workspace.RootPath);
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
