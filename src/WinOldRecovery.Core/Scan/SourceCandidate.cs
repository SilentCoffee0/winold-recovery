using System.Globalization;
using WinOldRecovery.Core.IO;

namespace WinOldRecovery.Core.Scan;

public sealed record SourceCandidate(
    string Path,
    SourceCandidateKind Kind,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EstimatedAutoDeleteAt,
    bool LooksLikeWindowsInstallation,
    bool HasUsersFolder,
    bool CleanupTaskPresent)
{
    public string DisplayLabel
    {
        get
        {
            string created = CreatedAt.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
            string deletion = EstimatedAutoDeleteAt is DateTimeOffset estimated
                ? " — estimated deletion " + estimated.ToString("d MMM yyyy", CultureInfo.InvariantCulture)
                : string.Empty;
            string users = HasUsersFolder ? string.Empty : " (no Users folder)";
            return PathDisplay.MiddleEllipsis(Path, 40) + "  " + created + deletion + users;
        }
    }
}

public enum SourceCandidateKind
{
    WindowsOld,
    OldSystemVolume,
    BrowsedFolder,
}
