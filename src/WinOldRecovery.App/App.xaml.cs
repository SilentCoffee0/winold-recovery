using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using WinOldRecovery.App.ViewModels;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Logging;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Processes;
using WinOldRecovery.Core.Restore;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Core.Scan;
using WinOldRecovery.Core.Sessions;
using WinOldRecovery.Native;
using WinOldRecovery.Recipes;

namespace WinOldRecovery.App;

public partial class App : Application
{
    private const int FindLatestBudgetMilliseconds = 2000;
    private static readonly string SingleInstanceName = @"Local\WinOldRecovery.App";

    private SessionDb? sessionDatabase;
    private ILoggerFactory? loggerFactory;
    private ILogger? logger;
    private SensitiveDataRedactor? redactor;
    private string? sessionLogPath;
    private Mutex? instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        try
        {
            Privileges.EnableBackupAndRestore();
            SourceGuard sourceGuard = new();
            SafeFs safeFs = new(sourceGuard);
            DateTimeOffset startedAt = DateTimeOffset.Now;
            bool publishedScan = PublishedScanArgs.TryParse(e.Args, out string scanRoot, out string scanReport);
            bool publishedRestore = PublishedRestoreArgs.TryParse(
                e.Args,
                out string restoreSource,
                out string restoreDest,
                out string restoreReport);

            if (publishedScan || publishedRestore)
            {
                StartHeadless(
                    safeFs,
                    sourceGuard,
                    startedAt,
                    publishedScan,
                    scanRoot,
                    scanReport,
                    publishedRestore,
                    restoreSource,
                    restoreDest,
                    restoreReport);
                base.OnStartup(e);
                return;
            }

            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceName, out bool createdNew);
            if (!createdNew)
            {
                instanceMutex.Dispose();
                instanceMutex = null;
                MessageBox.Show(
                    "WinOld Recovery is already running.",
                    "WinOld Recovery",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown(0);
                return;
            }

            // Show a window before probing leftover session databases. FindLatest
            // opens SQLite (and can stall under AV on 1M-scan leftovers). A
            // synchronous probe here would keep the dispatcher from painting.
            Window startupWindow = CreateStartupWindow();
            MainWindow = startupWindow;
            startupWindow.Show();
            base.OnStartup(e);
            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => ContinueGuiStartup(startupWindow, safeFs, sourceGuard, startedAt)));
        }
        catch (Exception exception)
        {
            ShowCrash(exception);
            Shutdown(exitCode: 1);
        }
    }

    private void StartHeadless(
        SafeFs safeFs,
        SourceGuard sourceGuard,
        DateTimeOffset startedAt,
        bool publishedScan,
        string scanRoot,
        string scanReport,
        bool publishedRestore,
        string restoreSource,
        string restoreDest,
        string restoreReport)
    {
        (SessionWorkspace workspace, SessionDb database) = Task.Run(
                () =>
                {
                    SessionWorkspace created = SessionWorkspace.Create(safeFs, now: startedAt);
                    SessionDb opened = SessionDb.OpenAsync(created.DatabasePath, safeFs)
                        .GetAwaiter()
                        .GetResult();
                    opened.CreateSessionAsync(
                            new SessionRecord(
                                created.SessionId,
                                startedAt,
                                "Created",
                                typeof(App).Assembly.GetName().Version?.ToString() ?? "0.1.0"))
                        .GetAwaiter()
                        .GetResult();
                    return (created, opened);
                })
            .GetAwaiter()
            .GetResult();
        sessionDatabase = database;
        InitializeSessionLogging(workspace, safeFs);

        if (publishedRestore)
        {
            string restoreText = Task.Run(
                    () => PublishedRestoreProbe.RunAsync(
                            database,
                            safeFs,
                            sourceGuard,
                            workspace.SessionId,
                            restoreSource,
                            restoreDest)
                        .GetAwaiter()
                        .GetResult())
                .GetAwaiter()
                .GetResult();
            safeFs.WriteAllText(restoreReport, restoreText);
            Shutdown(restoreText.Contains("Passed: true", StringComparison.Ordinal) ? 0 : 2);
            return;
        }

        string report = Task.Run(
                () => PublishedScanProbe.RunAsync(
                        database,
                        safeFs,
                        sourceGuard,
                        workspace.SessionId,
                        scanRoot,
                        workspace.TemporaryPath)
                    .GetAwaiter()
                    .GetResult())
            .GetAwaiter()
            .GetResult();
        safeFs.WriteAllText(scanReport, report);
        Shutdown(report.Contains("Passed: true", StringComparison.Ordinal) ? 0 : 2);
    }

    private async void ContinueGuiStartup(
        Window startupWindow,
        SafeFs safeFs,
        SourceGuard sourceGuard,
        DateTimeOffset startedAt)
    {
        try
        {
            using CancellationTokenSource findCts = new(TimeSpan.FromMilliseconds(FindLatestBudgetMilliseconds));
            Stopwatch findBudget = Stopwatch.StartNew();
            Task<InterruptedRestoreReport?> findTask = Task.Run(
                () => InterruptedRestore.FindLatest(safeFs, cancellationToken: findCts.Token),
                findCts.Token);
            Task<(SessionWorkspace Workspace, SessionDb Database)> freshTask = Task.Run(
                async () =>
                {
                    SessionWorkspace created = SessionWorkspace.Create(safeFs, now: startedAt);
                    SessionDb opened = await SessionDb.OpenAsync(created.DatabasePath, safeFs)
                        .ConfigureAwait(false);
                    await opened.CreateSessionAsync(
                            new SessionRecord(
                                created.SessionId,
                                startedAt,
                                "Created",
                                typeof(App).Assembly.GetName().Version?.ToString() ?? "0.1.0"))
                        .ConfigureAwait(false);
                    return (created, opened);
                });
            (SessionWorkspace createdWorkspace, SessionDb createdDatabase) =
                await freshTask.ConfigureAwait(true);
            InterruptedRestoreReport? interrupted = await TryTakeFindLatestAsync(findTask, findCts, findBudget)
                .ConfigureAwait(true);

            SessionWorkspace workspace;
            SessionDb database;
            if (interrupted is null)
            {
                workspace = createdWorkspace;
                database = createdDatabase;
            }
            else
            {
                workspace = SessionWorkspace.Open(interrupted.WorkspaceRoot, interrupted.SessionId);
                database = await SessionDb.OpenAsync(workspace.DatabasePath, safeFs).ConfigureAwait(true);
                try
                {
                    await createdDatabase.DisposeAsync().ConfigureAwait(true);
                    if (Directory.Exists(createdWorkspace.RootPath))
                    {
                        safeFs.DeleteDirectory(createdWorkspace.RootPath, recursive: true);
                    }
                }
                catch (Exception exception) when (
                    exception is not OutOfMemoryException and not StackOverflowException)
                {
                }
            }

            sessionDatabase = database;
            InitializeSessionLogging(workspace, safeFs);

            ProcessRunner processRunner = new();
            SourceDiscovery discovery = new(new DriveInfoVolumeRootProvider(), processRunner);
            ScanOrchestrator orchestrator = new(database, safeFs, sourceGuard);
            ShellViewModel viewModel = new(
                database,
                workspace,
                discovery,
                orchestrator,
                processRunner,
                safeFs,
                sourceGuard,
                RecipeCatalog.All,
                folderPicker: new WpfFolderPicker(),
                textClipboard: new WpfTextClipboard());

            if (interrupted is not null)
            {
                try
                {
                    viewModel.OfferInterruptedRestore(interrupted);
                }
                catch (Exception exception) when (
                    exception is not OutOfMemoryException and not StackOverflowException)
                {
                    logger?.LogError(exception, "Interrupted-restore overlay was skipped.");
                }
            }
            else
            {
                try
                {
                    CompletedScanPointer? lastScan = CompletedScan.TryRead(
                        safeFs,
                        CompletedScan.PointerPathFromWorkspace(createdWorkspace.RootPath));
                    if (lastScan is not null)
                    {
                        viewModel.OfferCompletedScan(lastScan);
                    }
                }
                catch (Exception exception) when (
                    exception is not OutOfMemoryException and not StackOverflowException)
                {
                    logger?.LogError(exception, "Last-scan overlay was skipped.");
                }
            }

            ApplyOptionalSmokeFixture(viewModel);

            MainWindow window = new(viewModel);
            window.Show();
            MainWindow = window;
            startupWindow.Close();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            // schtasks /Query used to block first paint (15 s timeout). Discover
            // after Show, and skip it when smoke/resume already picked a source.
            if (string.IsNullOrEmpty(viewModel.SelectedSourcePath) &&
                !viewModel.InterruptedRestoreVisible &&
                !viewModel.CompletedScanVisible)
            {
                await viewModel.LoadSourcesAsync().ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            logger?.LogError(exception, "GUI startup failed.");
            ShowCrash(exception);
            Shutdown(exitCode: 1);
        }
    }

    private static async Task<InterruptedRestoreReport?> TryTakeFindLatestAsync(
        Task<InterruptedRestoreReport?> findTask,
        CancellationTokenSource findCts,
        Stopwatch findBudget)
    {
        TimeSpan remaining = TimeSpan.FromMilliseconds(FindLatestBudgetMilliseconds) - findBudget.Elapsed;
        try
        {
            if (remaining <= TimeSpan.Zero)
            {
                if (findTask.IsCompletedSuccessfully)
                {
                    return findTask.GetAwaiter().GetResult();
                }

                findCts.Cancel();
                return null;
            }

            return await findTask.WaitAsync(remaining).ConfigureAwait(true);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not StackOverflowException)
        {
            findCts.Cancel();
            return null;
        }
    }

    private void InitializeSessionLogging(SessionWorkspace workspace, SafeFs safeFs)
    {
        sessionLogPath = workspace.LogPath;
        RollingFileLoggerProvider fileProvider = new(
            workspace.LogPath,
            safeFs,
            redactor = new SensitiveDataRedactor());
        loggerFactory = LoggerFactory.Create(
            builder => builder
                .SetMinimumLevel(LogLevel.Information)
                .AddProvider(fileProvider));
        logger = loggerFactory.CreateLogger("WinOldRecovery.App");
        logger.LogInformation(
            "Session {SessionId} initialized. Windows.old has not been touched.",
            workspace.SessionId);
    }

    private static void ApplyOptionalSmokeFixture(ShellViewModel viewModel)
    {
        string? source = Environment.GetEnvironmentVariable("WINOLD_RECOVERY_SMOKE_SOURCE");
        string? destination = Environment.GetEnvironmentVariable("WINOLD_RECOVERY_SMOKE_DEST");
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination))
        {
            return;
        }

        viewModel.TryApplySmokeFixture(source, destination);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        logger?.LogError(e.Exception, "Dispatcher unhandled exception.");
        ShowCrash(e.Exception);
        e.Handled = true;
        Shutdown(exitCode: 1);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            logger?.LogError(exception, "Domain unhandled exception.");
            ShowCrash(exception);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        logger?.LogError(e.Exception, "Unobserved task exception.");
        e.SetObserved();
    }

    private static Window CreateStartupWindow()
    {
        Window window = new()
        {
            Title = "WinOld Recovery",
            Width = 1100,
            Height = 720,
            MinWidth = 1024,
            MinHeight = 640,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new TextBlock
            {
                Margin = new Thickness(24),
                TextWrapping = TextWrapping.Wrap,
                Text = "WinOld Recovery is starting. Windows.old has not been touched.",
            },
        };
        AutomationProperties.SetName(window, "WinOld Recovery");
        AutomationProperties.SetAutomationId(window, "WinOldRecoveryMain");
        return window;
    }

    private void ShowCrash(Exception exception)
    {
        logger?.LogError(exception, "Unhandled exception.");
        ILogRedactor active = redactor ?? new SensitiveDataRedactor();
        string text = ExceptionReport.FormatUserMessage(exception, sessionLogPath, active);
        MessageBox.Show(
            text,
            "WinOld Recovery",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (instanceMutex is not null)
        {
            instanceMutex.ReleaseMutex();
            instanceMutex.Dispose();
            instanceMutex = null;
        }

        if (e.ApplicationExitCode != 0)
        {
            logger?.LogInformation("Session closed after an error (exit {Code}).", e.ApplicationExitCode);
        }
        else
        {
            logger?.LogInformation("Session closed normally.");
        }
        SessionDb? database = sessionDatabase;
        if (MainWindow is MainWindow window)
        {
            database = window.ViewModel.SessionDatabase;
        }

        if (database is not null)
        {
            try
            {
                database.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (
                exception is not OutOfMemoryException and not StackOverflowException)
            {
            }
        }

        loggerFactory?.Dispose();
        base.OnExit(e);
    }
}
