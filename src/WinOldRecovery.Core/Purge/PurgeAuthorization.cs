namespace WinOldRecovery.Core.Purge;

public sealed record PurgeGateRequest(
    bool VerifyAllOk,
    bool FilesChecked,
    bool UndecidedAcknowledged,
    bool RestoreJobActive,
    string TypedFolderName,
    string SourceFolderName,
    string CanonicalSourceRoot,
    string? RunningExecutablePath,
    string? SessionRoot,
    IReadOnlyList<string> DestinationPaths,
    bool CustomRootConfirmed);

public sealed record PurgeGateResult(
    bool Authorized,
    WinOldRecovery.Core.Safety.PurgeToken? Token,
    IReadOnlyList<string> BlockedGates);

public sealed class PurgeAuthorization
{
    public static PurgeGateResult Evaluate(PurgeGateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> blocked = [];
        if (!request.VerifyAllOk)
        {
            blocked.Add("verify");
        }

        if (!request.FilesChecked)
        {
            blocked.Add("files-checked");
        }

        if (!request.UndecidedAcknowledged)
        {
            blocked.Add("undecided");
        }

        if (request.RestoreJobActive)
        {
            blocked.Add("restore-active");
        }

        if (!string.Equals(
                request.TypedFolderName.Trim(),
                request.SourceFolderName.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("folder-name");
        }

        if (!IsPreviousInstallationName(request.SourceFolderName) && !request.CustomRootConfirmed)
        {
            blocked.Add("custom-root");
        }

        if (IsInsideSource(request.CanonicalSourceRoot, request.RunningExecutablePath))
        {
            blocked.Add("executable-inside-source");
        }

        if (IsInsideSource(request.CanonicalSourceRoot, request.SessionRoot))
        {
            blocked.Add("session-inside-source");
        }

        foreach (string destination in request.DestinationPaths)
        {
            if (IsInsideSource(request.CanonicalSourceRoot, destination))
            {
                blocked.Add("destination-inside-source");
                break;
            }
        }

        if (blocked.Count > 0)
        {
            return new PurgeGateResult(false, null, blocked);
        }

        return new PurgeGateResult(
            true,
            new WinOldRecovery.Core.Safety.PurgeToken(request.CanonicalSourceRoot),
            []);
    }

    public static bool IsPreviousInstallationName(string folderName)
    {
        return folderName.StartsWith("Windows.old", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInsideSource(string canonicalSourceRoot, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string canonical = WinOldRecovery.Core.IO.PathCanonicalizer.Canonicalize(path);
            string root = canonicalSourceRoot.TrimEnd('\\');
            return canonical.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                canonical.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
