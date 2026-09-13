namespace WinOldRecovery.Core.Scan;

public sealed record SourceCandidate(
    string Path,
    SourceCandidateKind Kind,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EstimatedAutoDeleteAt,
    bool LooksLikeWindowsInstallation,
    bool HasUsersFolder,
    bool CleanupTaskPresent);

public enum SourceCandidateKind
{
    WindowsOld,
    OldSystemVolume,
    BrowsedFolder,
}
