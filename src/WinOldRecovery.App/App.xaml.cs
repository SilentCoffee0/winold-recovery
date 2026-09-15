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
    private SessionDb? sessionDatabase;
    private ILoggerFactory? loggerFactory;
    private ILogger? logger;
    private SensitiveDataRedactor? redactor;
    private string? sessionLogPath;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
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
            File.WriteAllText(restoreReport, restoreText);
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
        File.WriteAllText(scanReport, report);
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
            InterruptedRestoreReport? interrupted = await Task.Run(
                    () => InterruptedRestore.FindLatest(safeFs))
                .ConfigureAwait(true);

            SessionWorkspace workspace;
            SessionDb database;
            if (interrupted is null)
            {
                workspace = SessionWorkspace.Create(safeFs, now: startedAt);
                database = await SessionDb.OpenAsync(workspace.DatabasePath, safeFs).ConfigureAwait(true);
                await database.CreateSessionAsync(
                        new SessionRecord(
                            workspace.SessionId,
                            startedAt,
                            "Created",
                            typeof(App).Assembly.GetName().Version?.ToString() ?? "0.1.0"))
                    .ConfigureAwait(true);
            }
            else
            {
                workspace = SessionWorkspace.Open(interrupted.WorkspaceRoot, interrupted.SessionId);
                database = await SessionDb.OpenAsync(workspace.DatabasePath, safeFs).ConfigureAwait(true);
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
                folderPicker: new WpfFolderPicker());
            await viewModel.LoadSourcesAsync().ConfigureAwait(true);

            if (interrupted is not null)
            {
                viewModel.OfferInterruptedRestore(interrupted);
            }

            MainWindow window = new(viewModel);
            window.Show();
            MainWindow = window;
            startupWindow.Close();
        }
        catch (Exception exception)
        {
            startupWindow.Close();
            ShowCrash(exception);
            Shutdown(exitCode: 1);
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
        logger?.LogInformation("Session closed normally.");
        sessionDatabase?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        loggerFactory?.Dispose();
        base.OnExit(e);
    }
}
