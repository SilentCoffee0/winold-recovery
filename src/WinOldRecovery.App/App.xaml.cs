using System.Windows;
using Microsoft.Extensions.Logging;
using WinOldRecovery.App.ViewModels;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Logging;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Processes;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            Privileges.EnableBackupAndRestore();
            SourceGuard sourceGuard = new();
            SafeFs safeFs = new(sourceGuard);
            DateTimeOffset startedAt = DateTimeOffset.Now;
            SessionWorkspace workspace = SessionWorkspace.Create(safeFs, now: startedAt);
            sessionDatabase = SessionDb.OpenAsync(workspace.DatabasePath, safeFs)
                .GetAwaiter()
                .GetResult();
            sessionDatabase.CreateSessionAsync(
                    new SessionRecord(
                        workspace.SessionId,
                        startedAt,
                        "Created",
                        typeof(App).Assembly.GetName().Version?.ToString() ?? "0.1.0"))
                .GetAwaiter()
                .GetResult();

            RollingFileLoggerProvider fileProvider = new(
                workspace.LogPath,
                safeFs,
                new SensitiveDataRedactor());
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
                RecipeCatalog.All);
            viewModel.LoadSourcesAsync().GetAwaiter().GetResult();

            MainWindow window = new(viewModel);
            window.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"WinOld Recovery could not initialize its protected recovery session.{Environment.NewLine}{Environment.NewLine}{exception.Message}",
                "WinOld Recovery could not start",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(exitCode: 1);
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        logger?.LogInformation("Session closed normally.");
        sessionDatabase?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        loggerFactory?.Dispose();
        base.OnExit(e);
    }
}
