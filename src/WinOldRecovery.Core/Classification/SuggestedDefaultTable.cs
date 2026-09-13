using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.Scan;

namespace WinOldRecovery.Core.Classification;

public sealed record BuiltInSuggestedDefault(string Id, string FolderName, Decision Decision);

public static class SuggestedDefaultTable
{
    public static readonly IReadOnlyList<string> RestoreStandardFolders =
    [
        "Desktop",
        "Documents",
        "Pictures",
        "Videos",
        "Music",
        "Saved Games",
        "Favorites",
        "Contacts",
        "Links",
        "Searches",
    ];

    public static readonly IReadOnlyList<BuiltInSuggestedDefault> Entries =
    [
        new("standard-desktop", "Desktop", Decision.Restore),
        new("standard-documents", "Documents", Decision.Restore),
        new("standard-pictures", "Pictures", Decision.Restore),
        new("standard-videos", "Videos", Decision.Restore),
        new("standard-music", "Music", Decision.Restore),
        new("standard-saved-games", "Saved Games", Decision.Restore),
        new("standard-favorites", "Favorites", Decision.Restore),
        new("standard-contacts", "Contacts", Decision.Restore),
        new("standard-links", "Links", Decision.Restore),
        new("standard-searches", "Searches", Decision.Restore),
        new("standard-downloads", "Downloads", Decision.Undecided),
        new("appdata-whole", "AppData", Decision.LeaveBehind),
    ];

    public static bool AllowsLeaveBehind(string id, ClassificationKind? kind)
    {
        return id == "appdata-whole" && kind is null;
    }

    public static bool IsRestoreStandardFolder(string knownName)
    {
        return RestoreStandardFolders.Contains(knownName, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsSkippedStandardFolder(string knownName)
    {
        return knownName.Equals("Downloads", StringComparison.OrdinalIgnoreCase);
    }
}
