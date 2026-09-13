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
    IReadOnlyList<string>? ProtectedPaths = null);

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

        if (request.PreferCleanupHandler)
        {
            await request.ProcessRunner.RunAsync(CreateCleanupRequest(), cancellationToken)
                .ConfigureAwait(false);
            if (!Directory.Exists(Strip(request.CanonicalSourceRoot)))
            {
                return new PurgeExecuteResult(true, "cleanup-handler", "Windows Disk Cleanup removed the folder.", []);
            }
        }

        List<string> remaining = [];
        DeleteTree(
            request.SafeFs,
            request.CanonicalSourceRoot,
            request.Token,
            request.ProtectedPaths ?? [],
            remaining,
            cancellationToken);

        bool gone = !Directory.Exists(Strip(request.CanonicalSourceRoot));
        return new PurgeExecuteResult(
            gone && remaining.Count == 0,
            request.PreferCleanupHandler ? "cleanup-then-manual" : "manual",
            gone ? "Source folder deleted." : "Some items could not be deleted.",
            remaining);
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
            TryDeleteLeaf(safeFs, path, attributes, token, remaining);
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
                DeleteTree(safeFs, child, token, protectedPaths, remaining, cancellationToken);
            }

            TryDeleteLeaf(safeFs, path, attributes, token, remaining);
            return;
        }

        TryDeleteLeaf(safeFs, path, attributes, token, remaining);
    }

    private static void TryDeleteLeaf(
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
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SourceWriteDeniedException)
        {
            remaining.Add(path);
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
