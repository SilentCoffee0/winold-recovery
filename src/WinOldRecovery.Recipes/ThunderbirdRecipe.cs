using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.Recipes;

namespace WinOldRecovery.Recipes;

public sealed class ThunderbirdRecipe : IRecipe
{
    private static readonly string[] AllowList =
    [
        "abook.sqlite",
        "history.mab",
        "key4.db",
        "logins.json",
        "logins-backup.json",
        "logins.db",
        "prefs.js",
        "user.js",
        "cert9.db",
        "places.sqlite",
        "permissions.sqlite",
        "handlers.json",
        "extensions.json",
        "xulstore.json",
    ];

    public string Id => "thunderbird";

    public DetectResult Detect(ProfileContext context)
    {
        string thunderbird = Path.Combine(
            context.OldProfileRoot,
            "AppData",
            "Roaming",
            "Thunderbird");
        List<RecipeCard> cards = [];
        List<(string RelativePath, string Kind, string Detail)> badges = [];
        foreach (DiscoveredFirefoxProfile discovered in FirefoxIni.Discover(
                     context.SafeFs,
                     thunderbird,
                     context.OldProfileRoot))
        {
            string profile = discovered.Directory;
            if (!context.SafeFs.DirectoryExists(profile) || DetectorWalk.IsReparse(profile))
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

            bool hasMail = context.SafeFs.DirectoryExists(Path.Combine(profile, "Mail")) ||
                context.SafeFs.DirectoryExists(Path.Combine(profile, "ImapMail"));
            if (present.Count == 0 && !hasMail)
            {
                continue;
            }

            string folder = Path.GetFileName(profile);
            string titleName = discovered.IsDefault ? discovered.Name + " (default)" : discovered.Name;
            cards.Add(
                new RecipeCard(
                    Id,
                    "Thunderbird — " + titleName,
                    "Address books, mail folders, saved passwords, and settings from the old Thunderbird profile.",
                    "Local folders and IMAP caches may be the only copy of older mail.",
                    "Allow-listed profile files plus Mail and ImapMail. panacea.dat and global-messages-db.sqlite are left behind.",
                    "An IMAP account will refetch server mail.",
                    "panacea.dat and the global message database regenerate.",
                    "You keep the current Thunderbird profile and lose the old local folders.",
                    [
                        new RecipeComponent(
                            "transplant",
                            "Profile transplant",
                            string.Join(", ", present),
                            Decision.Restore,
                            false,
                            null,
                            true),
                    ],
                    context.ProfileName + ":" + folder,
                    new Dictionary<string, string>
                    {
                        ["source"] = profile,
                        ["files"] = string.Join('|', present),
                        ["folder"] = folder,
                        ["name"] = discovered.Name,
                    }));
            DetectorWalk.AddTreeBadge(
                badges,
                context.OldProfileRoot,
                profile,
                "Thunderbird",
                string.IsNullOrWhiteSpace(discovered.Name) ? folder : discovered.Name);
        }

        return new DetectResult(cards, badges);
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
            "Thunderbird",
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

        foreach (string mail in new[] { "Mail", "ImapMail" })
        {
            string mailSource = Path.Combine(source, mail);
            if (destination.SafeFs.DirectoryExists(mailSource))
            {
                AnkiRecipe.AddTree(
                    destination.SafeFs,
                    mailSource,
                    Path.Combine(dest, mail),
                    mailSource,
                    "transplant",
                    writes,
                    SkipMail);
            }
        }

        string iniPath = Path.Combine(
            destination.DestinationProfileRoot,
            "AppData",
            "Roaming",
            "Thunderbird",
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
        RecipeVerifyResult present = DetectorWalk.FilesPresentMatchingSourceLength(
            plan,
            "Thunderbird profile registered",
            "Thunderbird transplant missing",
            "Thunderbird destination size does not match the source");
        if (!present.Ok)
        {
            return present;
        }

        RecipeVerifyResult? registration = FirefoxVerify.CheckRegistration(plan);
        if (registration is not null)
        {
            return registration;
        }

        RecipeVerifyResult? logins = FirefoxVerify.CheckKey4LoginPair(plan);
        if (logins is not null)
        {
            return logins;
        }

        return new RecipeVerifyResult(true, "Thunderbird profile registered");
    }

    public IReadOnlyList<Prerequisite> Prerequisites(PlanResult plan) =>
        [new Prerequisite("thunderbird", "Thunderbird must be closed before the profile is transplanted.")];

    private static bool SkipMail(string relative)
    {
        string name = Path.GetFileName(relative);
        return name.Equals("panacea.dat", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("global-messages-db.sqlite", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildProfilesIni(DestinationContext destination, string recoveredFolder)
    {
        string iniPath = Path.Combine(
            destination.DestinationProfileRoot,
            "AppData",
            "Roaming",
            "Thunderbird",
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
