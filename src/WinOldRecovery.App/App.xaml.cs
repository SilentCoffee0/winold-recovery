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
            InterruptedRestoreReport? interrupted = publishedScan
                ? null
                : InterruptedRestore.FindLatest(safeFs);
            SessionWorkspace workspace = interrupted is null
                ? SessionWorkspace.Create(safeFs, now: startedAt)
                : SessionWorkspace.Open(interrupted.WorkspaceRoot, interrupted.SessionId);
            sessionLogPath = workspace.LogPath;
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
            if (!publishedScan)
            {
                // --scan must not enumerate every DriveInfo root: a disconnected
                // network volume can stall startup before MainWindow.Loaded.
                viewModel.LoadSourcesAsync().GetAwaiter().GetResult();
            }

            if (interrupted is not null)
            {
                viewModel.OfferInterruptedRestore(interrupted);
            }

            MainWindow window = new(viewModel);
            if (publishedScan)
            {
                window.Loaded += async (_, _) =>
                {
                    try
                    {
                        string report = await viewModel.RunPublishedMemoryProbeAsync(scanRoot)
                            .ConfigureAwait(true);
                        File.WriteAllText(scanReport, report);
                        Shutdown(report.Contains("Passed: true", StringComparison.Ordinal) ? 0 : 2);
                    }
                    catch (Exception exception)
                    {
                        File.WriteAllText(
                            scanReport,
                            "Passed: false" + Environment.NewLine + exception.Message);
                        Shutdown(1);
                    }
                };
            }

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
