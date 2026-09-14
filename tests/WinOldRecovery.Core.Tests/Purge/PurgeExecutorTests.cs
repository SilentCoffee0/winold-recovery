using System.Diagnostics;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Processes;
using WinOldRecovery.Core.Purge;
using WinOldRecovery.Core.Safety;

namespace WinOldRecovery.Core.Tests.Purge;

public sealed class PurgeExecutorTests : IDisposable
{
    private readonly string testRoot;
    private readonly string sourceRoot;
    private readonly string liveTarget;
    private readonly string sessionRoot;
    private readonly SourceGuard guard = new();
    private readonly SafeFs safeFs;

    public PurgeExecutorTests()
    {
        testRoot = Path.Combine(Path.GetTempPath(), $"WinOldRecovery-Purge-{Guid.NewGuid():N}");
        sourceRoot = Path.Combine(testRoot, "Windows.old");
        liveTarget = Path.Combine(testRoot, "live-profile");
        sessionRoot = Path.Combine(testRoot, "session");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "Users", "Alice"));
        Directory.CreateDirectory(liveTarget);
        Directory.CreateDirectory(sessionRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "Users", "Alice", "notes.txt"), "source");
        File.WriteAllText(Path.Combine(liveTarget, "keep.txt"), "live");
        string readOnly = Path.Combine(sourceRoot, "Users", "Alice", "readonly.txt");
        File.WriteAllText(readOnly, "ro");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        CreateJunction(Path.Combine(sourceRoot, "Users", "Alice", "legacy-appdata"), liveTarget);
        safeFs = new SafeFs(guard);
    }

    [Fact]
    public async Task ManualDelete_RemovesSourceWithoutFollowingJunctionsAndRecordsCleanmgrWhenPreferred()
    {
        string canonical = guard.RegisterSourceRoot(sourceRoot);
        PurgeToken token = new(canonical);
        RecordingRunner runner = new();
        PurgeExecuteResult result = await new PurgeExecutor().ExecuteAsync(
            new PurgeExecuteRequest(
                canonical,
                token,
                safeFs,
                guard,
                runner,
                sessionRoot,
                PreferCleanupHandler: true,
                CleanupSage: new RecordingSage(arm: true)));

        Assert.Contains(runner.Requests, request => request.FileName == "cleanmgr.exe" && request.Arguments.Contains("/sagerun:777"));
        Assert.True(result.Completed, result.Detail + string.Join(';', result.RemainingPaths));
        Assert.False(Directory.Exists(sourceRoot));
        Assert.True(File.Exists(Path.Combine(liveTarget, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(sessionRoot, "purge-manifest.txt")));
        Assert.True(File.Exists(SupportBundle.Create(safeFs, new WinOldRecovery.Core.Sessions.SessionWorkspace(
            "s",
            sessionRoot,
            Path.Combine(sessionRoot, "session.db"),
            Path.Combine(sessionRoot, "log.txt"),
            Path.Combine(sessionRoot, "exports"),
            Path.Combine(sessionRoot, "tmp")))));
    }

    [Fact]
    public async Task Cleanmgr_IsNotInvokedWhenPreviousInstallationsFlagCannotBeArmed()
    {
        string canonical = guard.RegisterSourceRoot(sourceRoot);
        PurgeToken token = new(canonical);
        RecordingRunner runner = new();
        PurgeExecuteResult result = await new PurgeExecutor().ExecuteAsync(
            new PurgeExecuteRequest(
                canonical,
                token,
                safeFs,
                guard,
                runner,
                sessionRoot,
                PreferCleanupHandler: true,
                CleanupSage: new RecordingSage(arm: false)));

        Assert.DoesNotContain(runner.Requests, request => request.FileName == "cleanmgr.exe");
        Assert.True(result.Completed, result.Detail);
        Assert.False(Directory.Exists(sourceRoot));
        Assert.True(File.Exists(Path.Combine(liveTarget, "keep.txt")));
    }

    [Fact]
    public void DemandWrite_StillRefusesWithoutToken()
    {
        guard.RegisterSourceRoot(sourceRoot);
        Assert.Throws<SourceWriteDeniedException>(
            () => safeFs.DeleteFile(Path.Combine(sourceRoot, "Users", "Alice", "notes.txt")));
        Assert.True(File.Exists(Path.Combine(sourceRoot, "Users", "Alice", "notes.txt")));
    }

    public void Dispose()
    {
        if (!Directory.Exists(testRoot))
        {
            return;
        }

        string leftoverJunction = Path.Combine(sourceRoot, "Users", "Alice", "legacy-appdata");
        if (Directory.Exists(leftoverJunction))
        {
            try
            {
                Directory.Delete(leftoverJunction);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        ClearReadOnly(testRoot);
        Directory.Delete(testRoot, recursive: true);
    }

    private static void ClearReadOnly(string directory)
    {
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    private static void CreateJunction(string junction, string target)
    {
        ProcessStartInfo startInfo = new(
            Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junction);
        startInfo.ArgumentList.Add(target);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start mklink.");
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    private sealed class RecordingSage(bool arm) : ICleanupSage
    {
        public bool TryArmPreviousInstallations(int sageId) => arm;

        public void Disarm(int sageId)
        {
        }
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }
}
