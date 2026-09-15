using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.Recipes;

namespace WinOldRecovery.Recipes;

public sealed class FirefoxRecipe : IRecipe
{
    private static readonly string[] AllowList =
    [
        "places.sqlite",
        "places.sqlite-wal",
        "places.sqlite-shm",
        "favicons.sqlite",
        "logins.json",
        "logins-backup.json",
        "logins.db",
        "key4.db",
        "cookies.sqlite",
        "cookies.sqlite-wal",
        "formhistory.sqlite",
        "permissions.sqlite",
        "cert9.db",
        "prefs.js",
        "user.js",
        "xulstore.json",
        "containers.json",
        "handlers.json",
        "extensions.json",
        "sessionstore.jsonlz4",
        "search.json.mozlz4",
        "addonStartup.json.lz4",
        "storage-sync-v2.sqlite",
        "storage.sqlite",
        "webappsstore.sqlite",
        "cookies.sqlite-shm",
        "formhistory.sqlite-wal",
        "formhistory.sqlite-shm",
        "permissions.sqlite-wal",
        "permissions.sqlite-shm",
        "favicons.sqlite-wal",
        "favicons.sqlite-shm",
    ];

    private static readonly string[] FolderAllowList =
    [
        "sessionstore-backups",
        "bookmarkbackups",
        "extensions",
        "storage",
    ];

    public string Id => "firefox";

    public DetectResult Detect(ProfileContext context)
    {
        string firefox = Path.Combine(context.OldProfileRoot, "AppData", "Roaming", "Mozilla", "Firefox");
        IReadOnlyList<DiscoveredFirefoxProfile> profiles = FirefoxIni.Discover(
            context.SafeFs,
            firefox,
            context.OldProfileRoot);
        List<RecipeCard> cards = [];
        List<(string RelativePath, string Kind, string Detail)> badges = [];
        foreach (DiscoveredFirefoxProfile discovered in profiles)
        {
            string profile = discovered.Directory;
            List<string> present = [];
            foreach (string name in AllowList)
            {
                if (context.SafeFs.FileExists(Path.Combine(profile, name)))
                {
                    present.Add(name);
                }
            }

            List<string> folders = [];
            foreach (string name in FolderAllowList)
            {
                if (context.SafeFs.DirectoryExists(Path.Combine(profile, name)) &&
                    !DetectorWalk.IsReparse(Path.Combine(profile, name)))
                {
                    folders.Add(name);
                }
            }

            bool hasPlaces = present.Contains("places.sqlite");
            string bookmarksHtml = string.Empty;
            string historyCsv = "url,title\n";
            int bookmarkCount = 0;
            string folder = Path.GetFileName(profile);
            string firefoxTemp = Path.Combine(context.SessionTemporaryDirectory, "firefox", folder);
            if (hasPlaces)
            {
                try
                {
                    string copy = ReadOnlySqlite.CopyToTemp(
                        context.SafeFs,
                        Path.Combine(profile, "places.sqlite"),
                        firefoxTemp,
                        "places.sqlite");
                    (bookmarkCount, bookmarksHtml) = SqliteExports.FirefoxBookmarks(copy);
                    historyCsv = SqliteExports.FirefoxHistoryCsv(copy);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
                {
                }
            }

            string primaryPassword = present.Contains("key4.db")
                ? Key4PrimaryPassword.Detect(
                    context.SafeFs,
                    Path.Combine(profile, "key4.db"),
                    firefoxTemp)
                : Key4PrimaryPassword.Missing;
            (int tabCount, string tabsHtml) = FirefoxExports.Tabs(context.SafeFs, profile);
            (int extensionCount, string extensionsHtml) = FirefoxExports.Extensions(
                context.SafeFs,
                Path.Combine(profile, "extensions.json"));
            string titleName = discovered.IsDefault ? discovered.Name + " (default)" : discovered.Name;

            cards.Add(
                new RecipeCard(
                    Id,
                    "Firefox — " + titleName,
                    "Everything Firefox knows: bookmarks, history, open tabs, saved passwords, cookies, add-ons and settings.",
                    "Firefox does not tie passwords to the Windows account, so they can come back.",
                    "Copied as a new profile so nothing in your current Firefox is touched. " +
                    PrimaryPasswordRestoreCopy(primaryPassword),
                    "Firefox Account sync, if it was enabled.",
                    "Caches regenerate. The profile does not.",
                    "You keep the current Firefox profile and lose the old bookmarks, passwords and tabs.",
                    [
                        new RecipeComponent(
                            "transplant",
                            "Profile transplant",
                            TransplantSummary(present, folders),
                            hasPlaces ? Decision.Restore : Decision.LeaveBehind,
                            false,
                            null,
                            true),
                        new RecipeComponent(
                            "bookmarks-export",
                            "Bookmarks HTML export",
                            bookmarkCount + " bookmarks",
                            hasPlaces ? Decision.Restore : Decision.LeaveBehind,
                            false,
                            null,
                            false),
                        new RecipeComponent(
                            "history-export",
                            "History CSV export",
                            "From places.sqlite",
                            hasPlaces ? Decision.Restore : Decision.LeaveBehind,
                            false,
                            null,
                            true),
                        new RecipeComponent(
                            "tabs-export",
                            "Open tabs list",
                            tabCount + " tabs",
                            tabCount > 0 ? Decision.Restore : Decision.LeaveBehind,
                            false,
                            null,
                            false),
                        new RecipeComponent(
                            "extensions-export",
                            "Extensions list",
                            extensionCount + " extensions",
                            extensionCount > 0 ? Decision.Restore : Decision.LeaveBehind,
                            false,
                            null,
                            false),
                    ],
                    context.ProfileName + ":" + folder,
                    new Dictionary<string, string>
                    {
                        ["source"] = profile,
                        ["files"] = string.Join("|", present),
                        ["folders"] = string.Join("|", folders),
                        ["folder"] = folder,
                        ["name"] = discovered.Name,
                        ["isDefault"] = discovered.IsDefault ? "1" : "0",
                        ["bookmarkCount"] = bookmarkCount.ToString(),
                        ["bookmarksHtml"] = bookmarksHtml,
                        ["historyCsv"] = historyCsv,
                        ["primaryPassword"] = primaryPassword,
                        ["tabsHtml"] = tabsHtml,
                        ["extensionsHtml"] = extensionsHtml,
                    }));
            DetectorWalk.AddTreeBadge(
                badges,
                context.OldProfileRoot,
                profile,
                "Firefox",
                string.IsNullOrWhiteSpace(discovered.Name) ? folder : discovered.Name);
        }

        return new DetectResult(cards, badges);
    }

    public PlanResult Plan(CardDecisions decisions, DestinationContext destination)
    {
        List<RecipeWrite> writes = [];
        if (RecipeDecisions.ShouldRestore(decisions, "transplant"))
        {
            string source = decisions.Card.Facts["source"];
            string folder = decisions.Card.Facts["folder"] + "-recovered";
            string dest = Path.Combine(
                destination.DestinationProfileRoot,
                "AppData",
                "Roaming",
                "Mozilla",
                "Firefox",
                "Profiles",
                folder);
            foreach (string name in decisions.Card.Facts["files"].Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                writes.Add(
                    new RecipeWrite(
                        RecipeWriteKind.CopyFile,
                        Path.Combine(source, name),
                        Path.Combine(dest, name),
                        null,
                        1,
                        "transplant"));
            }

            foreach (string name in FolderNames(decisions.Card))
            {
                string folderSource = Path.Combine(source, name);
                if (destination.SafeFs.DirectoryExists(folderSource) && !DetectorWalk.IsReparse(folderSource))
                {
                    AnkiRecipe.AddTree(
                        destination.SafeFs,
                        folderSource,
                        Path.Combine(dest, name),
                        folderSource,
                        "transplant",
                        writes,
                        SkipRegenerated);
                }
            }

            string iniPath = Path.Combine(
                destination.DestinationProfileRoot,
                "AppData",
                "Roaming",
                "Mozilla",
                "Firefox",
                "profiles.ini");
            writes.Add(
                new RecipeWrite(
                    RecipeWriteKind.WriteContent,
                    null,
                    iniPath,
                    BuildProfilesIni(destination, folder),
                    1,
                    "transplant"));
        }

        string exportRoot = Path.Combine(
            destination.SessionExportsDirectory,
            Id,
            decisions.Card.InstanceKey.Replace(':', '_'));
        if (RecipeDecisions.ShouldRestore(decisions, "bookmarks-export"))
        {
            string html = decisions.Card.Facts.GetValueOrDefault("bookmarksHtml") ?? string.Empty;
            writes.Add(
                new RecipeWrite(
                    RecipeWriteKind.WriteContent,
                    null,
                    Path.Combine(exportRoot, "bookmarks.html"),
                    html,
                    html.Length,
                    "bookmarks-export"));
        }

        if (RecipeDecisions.ShouldRestore(decisions, "history-export"))
        {
            string csv = decisions.Card.Facts.GetValueOrDefault("historyCsv") ?? "url,title\n";
            writes.Add(
                new RecipeWrite(
                    RecipeWriteKind.WriteContent,
                    null,
                    Path.Combine(exportRoot, "history.csv"),
                    csv,
                    csv.Length,
                    "history-export"));
        }

        if (RecipeDecisions.ShouldRestore(decisions, "tabs-export"))
        {
            string html = decisions.Card.Facts.GetValueOrDefault("tabsHtml") ??
                "<!DOCTYPE html><title>Open tabs</title>";
            writes.Add(
                new RecipeWrite(
                    RecipeWriteKind.WriteContent,
                    null,
                    Path.Combine(exportRoot, "tabs.html"),
                    html,
                    html.Length,
                    "tabs-export"));
        }

        if (RecipeDecisions.ShouldRestore(decisions, "extensions-export"))
        {
            string html = decisions.Card.Facts.GetValueOrDefault("extensionsHtml") ??
                "<!DOCTYPE html><title>Extensions</title>";
            writes.Add(
                new RecipeWrite(
                    RecipeWriteKind.WriteContent,
                    null,
                    Path.Combine(exportRoot, "extensions.html"),
                    html,
                    html.Length,
                    "extensions-export"));
        }

        return new PlanResult(decisions.Card, writes);
    }

    public Task ExecuteAsync(PlanResult plan, IRecipeJournal journal, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public RecipeVerifyResult Verify(PlanResult plan) => FirefoxVerify.Check(plan);

    public IReadOnlyList<Prerequisite> Prerequisites(PlanResult plan) =>
        [new Prerequisite("firefox", "Firefox must be closed before the profile is transplanted.")];

    private static string TransplantSummary(List<string> present, List<string> folders)
    {
        if (present.Count == 0 && folders.Count == 0)
        {
            return "Empty profile";
        }

        return string.Join(", ", present.Concat(folders));
    }

    private static IEnumerable<string> FolderNames(RecipeCard card)
    {
        string stored = card.Facts.GetValueOrDefault("folders") ?? string.Empty;
        if (stored.Length > 0)
        {
            return stored.Split('|', StringSplitOptions.RemoveEmptyEntries);
        }

        return FolderAllowList;
    }

    private static bool SkipRegenerated(string relative)
    {
        string name = Path.GetFileName(relative);
        return name.Equals("lock", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("parent.lock", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("sessionCheckpoints.json", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("times.json", StringComparison.OrdinalIgnoreCase);
    }

    private static string PrimaryPasswordRestoreCopy(string primaryPassword)
    {
        if (primaryPassword == Key4PrimaryPassword.Set)
        {
            return "A Primary Password is set; Firefox will ask for it. The tool cannot bypass it.";
        }

        if (primaryPassword == Key4PrimaryPassword.NotSet)
        {
            return "No Primary Password was detected.";
        }

        if (primaryPassword == Key4PrimaryPassword.Missing)
        {
            return "No key4.db was found, so saved passwords are not in this profile.";
        }

        return "Could not determine whether a Primary Password is set.";
    }

    private static string BuildProfilesIni(DestinationContext destination, string recoveredFolder)
    {
        string iniPath = Path.Combine(
            destination.DestinationProfileRoot,
            "AppData",
            "Roaming",
            "Mozilla",
            "Firefox",
            "profiles.ini");
        string existing = destination.SafeFs.FileExists(iniPath)
            ? destination.SafeFs.ReadAllText(iniPath)
            : """
              [General]
              StartWithLastProfile=1
              Version=2

              """;
        int next = 0;
        foreach (string line in existing.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("[Profile", StringComparison.OrdinalIgnoreCase) &&
                trimmed.EndsWith(']') &&
                int.TryParse(trimmed.AsSpan("[Profile".Length, trimmed.Length - "[Profile".Length - 1), out int index))
            {
                next = Math.Max(next, index + 1);
            }
        }

        return existing.TrimEnd() +
            $"""


            [Profile{next}]
            Name={recoveredFolder}
            IsRelative=1
            Path=Profiles/{recoveredFolder}

            """;
    }
}
