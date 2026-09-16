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
        const string firefoxRel = @"AppData\Roaming\Mozilla\Firefox";
        IReadOnlyList<DiscoveredFirefoxProfile> profiles = FirefoxIni.Discover(
            context.SafeFs,
            firefox,
            context.OldProfileRoot,
            FirefoxIni.IndexedDirectories(context, firefoxRel, "places.sqlite", "prefs.js"),
            skipProfilesDirectoryWalk: context.Index is not null);
        string destFirefox = Path.Combine(
            context.DestinationProfileRoot,
            "AppData",
            "Roaming",
            "Mozilla",
            "Firefox");
        bool profileGroups = FirefoxProfileGroups.Detected(context.SafeFs, firefox) ||
            FirefoxProfileGroups.Detected(context.SafeFs, destFirefox);
        List<RecipeCard> cards = [];
        List<(string RelativePath, string Kind, string Detail)> badges = [];
        foreach (DiscoveredFirefoxProfile discovered in profiles)
        {
            string profile = discovered.Directory;
            List<string> present;
            List<string> folders;
            bool hasTabs;
            if (DetectorWalk.TryIndexedRelative(context, profile, out RecipeIndex index, out string relative))
            {
                present = DetectorWalk.IndexedImmediateFiles(index, profile, relative, AllowList);
                folders = [];
                foreach (string name in FolderAllowList)
                {
                    if (DetectorWalk.IndexedChildFolder(index, relative, name))
                    {
                        folders.Add(name);
                    }
                }

                hasTabs = DetectorWalk.IndexedFirefoxSession(index, profile, relative);
            }
            else
            {
                present = [];
                foreach (string name in AllowList)
                {
                    if (context.SafeFs.FileExists(Path.Combine(profile, name)))
                    {
                        present.Add(name);
                    }
                }

                folders = [];
                foreach (string name in FolderAllowList)
                {
                    if (context.SafeFs.DirectoryExists(Path.Combine(profile, name)) &&
                        !DetectorWalk.IsReparse(Path.Combine(profile, name)))
                    {
                        folders.Add(name);
                    }
                }

                hasTabs = FirefoxExports.HasSession(context.SafeFs, profile);
            }

            bool hasPlaces = present.Contains("places.sqlite");
            string folder = Path.GetFileName(profile);
            string firefoxTemp = Path.Combine(context.SessionTemporaryDirectory, "firefox", folder);

            string primaryPassword = present.Contains("key4.db")
                ? Key4PrimaryPassword.Detect(
                    context.SafeFs,
                    Path.Combine(profile, "key4.db"),
                    firefoxTemp)
                : Key4PrimaryPassword.Missing;
            bool hasExtensions = present.Contains("extensions.json");
            string titleName = discovered.IsDefault ? discovered.Name + " (default)" : discovered.Name;

            cards.Add(
                new RecipeCard(
                    Id,
                    "Firefox — " + titleName,
                    "Everything Firefox knows: bookmarks, history, open tabs, saved passwords, cookies, add-ons and settings.",
                    "Firefox does not tie passwords to the Windows account, so they can come back.",
                    "Copied as a new profile so nothing in your current Firefox is touched. " +
                    PrimaryPasswordRestoreCopy(primaryPassword) +
                    (profileGroups
                        ? " Firefox 135+ profile groups (StoreID) were found, so profiles.ini is not edited. Use about:profiles → Create, then copy the recovered folder in."
                        : string.Empty),
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
                            hasPlaces ? "places.sqlite found" : "No places.sqlite",
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
                            hasTabs ? "Session file found" : "No session file",
                            hasTabs ? Decision.Restore : Decision.LeaveBehind,
                            false,
                            null,
                            false),
                        new RecipeComponent(
                            "extensions-export",
                            "Extensions list",
                            hasExtensions ? "extensions.json found" : "No extensions.json",
                            hasExtensions ? Decision.Restore : Decision.LeaveBehind,
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
                        ["primaryPassword"] = primaryPassword,
                        ["profileGroups"] = profileGroups ? "1" : "0",
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

            string destFirefox = Path.Combine(
                destination.DestinationProfileRoot,
                "AppData",
                "Roaming",
                "Mozilla",
                "Firefox");
            bool skipIni = decisions.Card.Facts.GetValueOrDefault("profileGroups") == "1" ||
                FirefoxProfileGroups.Detected(destination.SafeFs, destFirefox);
            if (!skipIni)
            {
                writes.Add(
                    new RecipeWrite(
                        RecipeWriteKind.WriteContent,
                        null,
                        Path.Combine(destFirefox, "profiles.ini"),
                        BuildProfilesIni(destination, folder),
                        1,
                        "transplant"));
            }
        }

        string exportRoot = Path.Combine(
            destination.SessionExportsDirectory,
            Id,
            decisions.Card.InstanceKey.Replace(':', '_'));
        string bookmarksHtmlReady = string.Empty;
        string historyCsvReady = "url,title\n";
        if (RecipeDecisions.ShouldRestore(decisions, "bookmarks-export") ||
            RecipeDecisions.ShouldRestore(decisions, "history-export"))
        {
            LoadPlacesExports(decisions, destination, ref bookmarksHtmlReady, ref historyCsvReady);
        }

        if (RecipeDecisions.ShouldRestore(decisions, "bookmarks-export"))
        {
            string html = bookmarksHtmlReady;
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
            string csv = historyCsvReady;
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
            (_, string html) = FirefoxExports.Tabs(destination.SafeFs, decisions.Card.Facts["source"]);
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
            (_, string html) = FirefoxExports.Extensions(
                destination.SafeFs,
                Path.Combine(decisions.Card.Facts["source"], "extensions.json"));
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

    private static void LoadPlacesExports(
        CardDecisions decisions,
        DestinationContext destination,
        ref string bookmarksHtml,
        ref string historyCsv)
    {
        if (!string.IsNullOrEmpty(bookmarksHtml) && historyCsv.Length > "url,title\n".Length)
        {
            return;
        }

        string source = Path.Combine(decisions.Card.Facts["source"], "places.sqlite");
        if (!destination.SafeFs.FileExists(source))
        {
            return;
        }

        string work = string.IsNullOrWhiteSpace(destination.SessionTemporaryDirectory)
            ? Path.Combine(destination.SessionExportsDirectory, ".work")
            : destination.SessionTemporaryDirectory;

        try
        {
            string copy = ReadOnlySqlite.CopyToTemp(
                destination.SafeFs,
                source,
                Path.Combine(work, "firefox", decisions.Card.Facts["folder"]),
                "places.sqlite");
            if (string.IsNullOrEmpty(bookmarksHtml))
            {
                (_, bookmarksHtml) = SqliteExports.FirefoxBookmarks(copy);
            }

            if (historyCsv.Length <= "url,title\n".Length)
            {
                historyCsv = SqliteExports.FirefoxHistoryCsv(copy);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
        }
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
