using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Processes;
using WinOldRecovery.Core.Safety;

namespace WinOldRecovery.Core.Purge;

public sealed record PurgeExecuteRequest(
    string CanonicalSourceRoot,
    PurgeToken Token,
    SafeFs SafeFs,
    SourceGuard SourceGuard,
    IProcessRunner ProcessRunner,
    string SessionRoot,
    bool PreferCleanupHandler,
    IReadOnlyList<string>? ProtectedPaths = null,
    ICleanupSage? CleanupSage = null,
    IProgress<PurgeProgress>? Progress = null);

public sealed record PurgeExecuteResult(
    bool Completed,
    string Method,
    string Detail,
    IReadOnlyList<string> RemainingPaths);

public sealed class PurgeExecutor
{
    public static ProcessRequest CreateCleanupRequest()
    {
        return new ProcessRequest(
            "cleanmgr.exe",
            ["/sagerun:777"],
            Timeout: TimeSpan.FromMinutes(30));
    }

    public async Task<PurgeExecuteResult> ExecuteAsync(
        PurgeExecuteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.SourceGuard.DemandWriteAllowed(request.CanonicalSourceRoot, request.Token);
        WriteManifest(request);

        const int sageId = 777;
        ICleanupSage sage = request.CleanupSage ?? new RegistryCleanupSage();
        bool armed = false;
        string method = "manual";
        List<string> remaining = [];
        ProgressClock clock = new(request.Progress);
        int deleted = 0;

        try
        {
            if (request.PreferCleanupHandler)
            {
                armed = sage.TryArmPreviousInstallations(sageId);
                if (armed)
                {
                    method = "cleanup-handler";
                    try
                    {
                        await request.ProcessRunner.RunAsync(CreateCleanupRequest(), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        sage.Disarm(sageId);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Directory.Exists(Strip(request.CanonicalSourceRoot)))
                    {
                        clock.Report(deleted, request.CanonicalSourceRoot, force: true);
                        return new PurgeExecuteResult(
                            true,
                            "cleanup-handler",
                            "Windows Disk Cleanup removed the folder.",
                            []);
                    }

                    method = "cleanup-then-manual";
                }
            }

            int deletedCount = deleted;
            await Task.Run(
                    () =>
                    {
                        int localDeleted = deletedCount;
                        DeleteTree(
                            request.SafeFs,
                            request.CanonicalSourceRoot,
                            request.Token,
                            request.ProtectedPaths ?? [],
                            remaining,
                            clock,
                            ref localDeleted,
                            cancellationToken);
                        deletedCount = localDeleted;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            deleted = deletedCount;

            bool gone = !Directory.Exists(Strip(request.CanonicalSourceRoot));
            clock.Report(deleted, request.CanonicalSourceRoot, force: true);
            return new PurgeExecuteResult(
                gone && remaining.Count == 0,
                armed ? "cleanup-then-manual" : "manual",
                gone ? "Source folder deleted." : "Some items could not be deleted.",
                remaining);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            clock.Report(deleted, request.CanonicalSourceRoot, force: true);
            return new PurgeExecuteResult(false, method, PurgeProgressFormat.CancelledDetail, remaining);
        }
    }

    private static void WriteManifest(PurgeExecuteRequest request)
    {
        string manifest = Path.Combine(request.SessionRoot, "purge-manifest.txt");
        request.SafeFs.WriteAllText(
            manifest,
            "source=" + request.CanonicalSourceRoot + Environment.NewLine +
            "startedUtc=" + DateTimeOffset.UtcNow.ToString("O") + Environment.NewLine);
    }

    private static void DeleteTree(
        SafeFs safeFs,
        string path,
        PurgeToken token,
        IReadOnlyList<string> protectedPaths,
        List<string> remaining,
        ProgressClock clock,
        ref int deleted,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsProtected(path, protectedPaths))
        {
            remaining.Add(path);
            return;
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(Strip(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            remaining.Add(path);
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            NoteDeleted(TryDeleteLeaf(safeFs, path, attributes, token, remaining), clock, ref deleted, path);
            return;
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            IReadOnlyList<string> children;
            try
            {
                children = safeFs.EnumerateFileSystemEntries(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                remaining.Add(path);
                return;
            }

            foreach (string child in children)
            {
                DeleteTree(
                    safeFs,
                    child,
                    token,
                    protectedPaths,
                    remaining,
                    clock,
                    ref deleted,
                    cancellationToken);
            }

            NoteDeleted(TryDeleteLeaf(safeFs, path, attributes, token, remaining), clock, ref deleted, path);
            return;
        }

        NoteDeleted(TryDeleteLeaf(safeFs, path, attributes, token, remaining), clock, ref deleted, path);
    }

    private static void NoteDeleted(bool deletedLeaf, ProgressClock clock, ref int deleted, string path)
    {
        if (!deletedLeaf)
        {
            return;
        }

        deleted++;
        clock.Report(deleted, path, force: false);
    }

    private static bool TryDeleteLeaf(
        SafeFs safeFs,
        string path,
        FileAttributes attributes,
        PurgeToken token,
        List<string> remaining)
    {
        try
        {
            FileAttributes cleared = attributes & ~FileAttributes.ReadOnly;
            if (cleared != attributes)
            {
                safeFs.SetAttributes(path, cleared, token);
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                safeFs.DeleteDirectory(path, recursive: false, token);
            }
            else
            {
                safeFs.DeleteFile(path, token);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SourceWriteDeniedException)
        {
            remaining.Add(path);
            return false;
        }
    }

    private sealed class ProgressClock(IProgress<PurgeProgress>? progress)
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
        private DateTimeOffset last = DateTimeOffset.MinValue;

        public void Report(int deletedLeaves, string currentPath, bool force)
        {
            if (progress is null)
            {
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!force && last != DateTimeOffset.MinValue && now - last < Interval)
            {
                return;
            }

            last = now;
            progress.Report(new PurgeProgress(deletedLeaves, currentPath));
        }
    }

    private static bool IsProtected(string path, IReadOnlyList<string> protectedPaths)
    {
        string candidate = Strip(path);
        foreach (string protectedPath in protectedPaths)
        {
            string other = Strip(protectedPath);
            if (candidate.Equals(other, StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(other + "\\", StringComparison.OrdinalIgnoreCase) ||
                other.StartsWith(candidate + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Strip(string path)
    {
        const string prefix = @"\\?\";
        return path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
    }
}
