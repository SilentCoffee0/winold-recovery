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
    ];

    public string Id => "firefox";

    public DetectResult Detect(ProfileContext context)
    {
        string firefox = Path.Combine(context.OldProfileRoot, "AppData", "Roaming", "Mozilla", "Firefox");
        string profilesDir = Path.Combine(firefox, "Profiles");
        if (!context.SafeFs.DirectoryExists(profilesDir))
        {
            return new DetectResult([], []);
        }

        List<RecipeCard> cards = [];
        foreach (string profile in context.SafeFs.EnumerateFileSystemEntries(profilesDir))
        {
            if (!context.SafeFs.DirectoryExists(profile))
            {
                continue;
            }

            List<string> present = [];
            foreach (string name in AllowList)
            {
                if (context.SafeFs.FileExists(Path.Combine(profile, name)))
                {
                    present.Add(name);
                }
            }

            if (present.Count == 0)
            {
                continue;
            }

            bool hasPlaces = present.Contains("places.sqlite");
            cards.Add(
                new RecipeCard(
                    Id,
                    "Firefox — " + Path.GetFileName(profile),
                    "Everything Firefox knows: bookmarks, history, open tabs, saved passwords, cookies, add-ons and settings.",
                    "Firefox does not tie passwords to the Windows account, so they can come back.",
                    "Copied as a new profile so nothing in your current Firefox is touched. Passwords stay behind your Primary Password if you set one.",
                    "Firefox Account sync, if it was enabled.",
                    "Caches regenerate. The profile does not.",
                    "You keep the current Firefox profile and lose the old bookmarks, passwords and tabs.",
                    [
                        new RecipeComponent(
                            "transplant",
                            "Profile transplant",
                            string.Join(", ", present),
                            hasPlaces ? Decision.Restore : Decision.LeaveBehind,
                            false,
                            null,
                            true),
                    ],
                    context.ProfileName + ":" + Path.GetFileName(profile),
                    new Dictionary<string, string>
                    {
                        ["source"] = profile,
                        ["files"] = string.Join("|", present),
                        ["folder"] = Path.GetFileName(profile),
                    }));
        }

        return new DetectResult(cards, []);
    }

    public PlanResult Plan(CardDecisions decisions, DestinationContext destination)
    {
        if (!RecipeDecisions.ShouldRestore(decisions, "transplant"))
        {
            return new PlanResult(decisions.Card, []);
        }

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
        List<RecipeWrite> writes = [];
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

        return new PlanResult(decisions.Card, writes);
    }

    public Task ExecuteAsync(PlanResult plan, IRecipeJournal journal, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public RecipeVerifyResult Verify(PlanResult plan)
    {
        bool ok = plan.Writes.All(static write => File.Exists(write.DestinationPath));
        return new RecipeVerifyResult(ok, ok ? "Firefox files present" : "Firefox transplant missing");
    }

    public IReadOnlyList<Prerequisite> Prerequisites(PlanResult plan) =>
        [new Prerequisite("firefox", "Firefox must be closed before the profile is transplanted.")];

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
