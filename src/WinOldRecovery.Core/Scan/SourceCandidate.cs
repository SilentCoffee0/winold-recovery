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
    bool CleanupTaskPresent,
    DateTimeOffset? CleanupTaskNextRunAt = null,
    int UserProfileCount = 0)
{
    public string PathLabel => PathDisplay.MiddleEllipsis(PathCanonicalizer.WithoutExtendedPrefix(Path), 48);

    public string KindLabel => Kind switch
    {
        SourceCandidateKind.WindowsOld => "Windows.old",
        SourceCandidateKind.OldSystemVolume => "Old system volume",
        SourceCandidateKind.BrowsedFolder => "Browsed folder",
        _ => Kind.ToString(),
    };

    public string DetailLabel
    {
        get
        {
            string created = CreatedAt.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
            string profiles = UserProfileCount == 1
                ? "1 profile"
                : UserProfileCount.ToString(CultureInfo.InvariantCulture) + " profiles";
            string deletion = EstimatedAutoDeleteAt is DateTimeOffset estimated
                ? " · estimated deletion " + estimated.ToString("d MMM yyyy", CultureInfo.InvariantCulture)
                : string.Empty;
            string missingUsers = HasUsersFolder ? string.Empty : " · no Users folder";
            string countPart = UserProfileCount > 0 ? " · " + profiles : string.Empty;
            return KindLabel + " · created " + created + countPart + deletion + missingUsers;
        }
    }

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

    public string DeletionWarning
    {
        get
        {
            if (EstimatedAutoDeleteAt is not DateTimeOffset estimated)
            {
                return string.Empty;
            }

            string age =
                "Windows automatically deletes Windows.old about 10 days after setup. Estimated deletion from folder age: " +
                estimated.ToString("d MMM yyyy", CultureInfo.InvariantCulture) +
                ".";
            if (!CleanupTaskPresent)
            {
                return age + " The Setup Cleanup task was not found. Finish recovery before then.";
            }

            if (CleanupTaskNextRunAt is DateTimeOffset next)
            {
                return age +
                    " The Setup Cleanup task is present; next run " +
                    next.ToString("d MMM yyyy", CultureInfo.InvariantCulture) +
                    ". Finish recovery before then.";
            }

            return age +
                " The Setup Cleanup task is present (next run was not reported). Finish recovery before then.";
        }
    }
}

public enum SourceCandidateKind
{
    WindowsOld,
    OldSystemVolume,
    BrowsedFolder,
}
