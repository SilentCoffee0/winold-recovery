using System.IO;
using System.Windows;
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
            InterruptedRestoreReport? interrupted = publishedScan
                ? null
                : InterruptedRestore.FindLatest(safeFs);
            SessionWorkspace workspace;
            if (publishedScan || publishedRestore)
            {
                InterruptedRestoreReport? resume = publishedRestore ? interrupted : null;
                // Open SQLite off the WPF STA thread. CreateSessionAsync().GetResult()
                // on the dispatcher deadlocks the writer when OpenAsync completes inline.
                (workspace, sessionDatabase) = Task.Run(() =>
                    {
                        if (resume is not null)
                        {
                            SessionWorkspace opened = SessionWorkspace.Open(
                                resume.WorkspaceRoot,
                                resume.SessionId);
                            SessionDb existing = SessionDb.OpenAsync(opened.DatabasePath, safeFs)
                                .GetAwaiter()
                                .GetResult();
                            return (opened, existing);
                        }

                        SessionWorkspace created = SessionWorkspace.Create(safeFs, now: startedAt);
                        SessionDb database = SessionDb.OpenAsync(created.DatabasePath, safeFs)
                            .GetAwaiter()
                            .GetResult();
                        database.CreateSessionAsync(
                                new SessionRecord(
                                    created.SessionId,
                                    startedAt,
                                    "Created",
                                    typeof(App).Assembly.GetName().Version?.ToString() ?? "0.1.0"))
                            .GetAwaiter()
                            .GetResult();
                        return (created, database);
                    })
                    .GetAwaiter()
                    .GetResult();
            }
            else
            {
                workspace = interrupted is null
                    ? SessionWorkspace.Create(safeFs, now: startedAt)
                    : SessionWorkspace.Open(interrupted.WorkspaceRoot, interrupted.SessionId);
                sessionDatabase = SessionDb.OpenAsync(workspace.DatabasePath, safeFs)
                    .GetAwaiter()
                    .GetResult();
                if (interrupted is null)
                {
                    sessionDatabase.CreateSessionAsync(
                            new SessionRecord(
                                workspace.SessionId,
                                startedAt,
                                "Created",
                                typeof(App).Assembly.GetName().Version?.ToString() ?? "0.1.0"))
                        .GetAwaiter()
                        .GetResult();
                }
            }

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

            if (publishedRestore)
            {
                SessionDb restoreDatabase = sessionDatabase
                    ?? throw new InvalidOperationException("The session database was not opened.");
                string restoreText = Task.Run(
                        () => PublishedRestoreProbe.RunAsync(
                                restoreDatabase,
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
                base.OnStartup(e);
                Shutdown(restoreText.Contains("Passed: true", StringComparison.Ordinal) ? 0 : 2);
                return;
            }

            if (publishedScan)
            {
                SessionDb database = sessionDatabase
                    ?? throw new InvalidOperationException("The session database was not opened.");
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
                base.OnStartup(e);
                Shutdown(report.Contains("Passed: true", StringComparison.Ordinal) ? 0 : 2);
                return;
            }

            ProcessRunner processRunner = new();
            SourceDiscovery discovery = new(new DriveInfoVolumeRootProvider(), processRunner);
            ScanOrchestrator orchestrator = new(sessionDatabase, safeFs, sourceGuard);
            ShellViewModel viewModel = new(
                sessionDatabase,
                workspace,
                discovery,
                orchestrator,
                processRunner,
                safeFs,
                sourceGuard,
                RecipeCatalog.All,
                folderPicker: new WpfFolderPicker());
            viewModel.LoadSourcesAsync().GetAwaiter().GetResult();

            if (interrupted is not null)
            {
                viewModel.OfferInterruptedRestore(interrupted);
            }

            MainWindow window = new(viewModel);
            window.Show();
        }
        catch (Exception exception)
        {
            ShowCrash(exception);
            Shutdown(exitCode: 1);
            return;
        }

        base.OnStartup(e);
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
