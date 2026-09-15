using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Recipes;

namespace WinOldRecovery.Recipes;

public sealed class AnkiRecipe : IRecipe
{
    public string Id => "anki";

    public DetectResult Detect(ProfileContext context)
    {
        List<RecipeCard> cards = [];
        List<(string RelativePath, string Kind, string Detail)> badges = [];
        foreach (string baseFolder in CandidateBases(context))
        {
            if (!context.SafeFs.DirectoryExists(baseFolder))
            {
                continue;
            }

            foreach (string profile in context.SafeFs.EnumerateFileSystemEntries(baseFolder))
            {
                if (!context.SafeFs.DirectoryExists(profile))
                {
                    continue;
                }

                string collection = Path.Combine(profile, "collection.anki2");
                if (!context.SafeFs.FileExists(collection))
                {
                    continue;
                }

                (int notes, int cardCount, string integrity) = ReadCollectionFacts(context, collection, Path.GetFileName(profile));
                int media = CountMedia(context.SafeFs, Path.Combine(profile, "collection.media"));
                bool hasWal = context.SafeFs.FileExists(Path.Combine(profile, "collection.anki2-wal"));
                string destAnki = Path.Combine(context.DestinationProfileRoot, "AppData", "Roaming", "Anki2");
                bool destPrefs = context.SafeFs.FileExists(Path.Combine(destAnki, "prefs21.db"));
                cards.Add(
                    new RecipeCard(
                        Id,
                        "Anki — " + Path.GetFileName(profile),
                        "Your decks, cards, review history and media.",
                        "Restored as a profile that Anki lists at start-up. The media index and trash are rebuilt by Anki.",
                        "collection.anki2" + (hasWal ? " and its WAL" : string.Empty) + ", media files, deleted.txt, and backups. Not media.trash or collection.media.db2.",
                        "AnkiWeb sync restores decks except add-ons.",
                        "media.trash and collection.media.db2 regenerate.",
                        "You keep an empty Anki and lose local review history until AnkiWeb syncs.",
                        [
                            new RecipeComponent(
                                "profile",
                                "Profile",
                                notes + " notes, " + cardCount + " cards, integrity " + integrity,
                                Decision.Restore,
                                false,
                                null,
                                false),
                            new RecipeComponent(
                                "backups",
                                "Backups",
                                "Automatic .colpkg archives",
                                Decision.Restore,
                                false,
                                null,
                                false),
                            new RecipeComponent(
                                "addons",
                                "Add-ons",
                                "addons21",
                                Decision.Undecided,
                                false,
                                null,
                                false),
                            new RecipeComponent(
                                "prefs",
                                "prefs21.db",
                                destPrefs
                                    ? "Destination already has prefs; restoring can point at missing profiles"
                                    : "No destination prefs; restore is offered",
                                destPrefs ? Decision.LeaveBehind : Decision.Restore,
                                false,
                                null,
                                false),
                        ],
                        context.ProfileName + ":" + Path.GetFileName(profile),
                        new Dictionary<string, string>
                        {
                            ["source"] = profile,
                            ["base"] = baseFolder,
                            ["notes"] = notes.ToString(),
                            ["cards"] = cardCount.ToString(),
                            ["media"] = media.ToString(),
                            ["integrity"] = integrity,
                        }));
                DetectorWalk.AddTreeBadge(
                    badges,
                    context.OldProfileRoot,
                    profile,
                    "Anki",
                    Path.GetFileName(profile));
            }
        }

        return new DetectResult(cards, badges);
    }

    public PlanResult Plan(CardDecisions decisions, DestinationContext destination)
    {
        string source = decisions.Card.Facts["source"];
        string name = Path.GetFileName(source);
        string destRoot = Path.Combine(destination.DestinationProfileRoot, "AppData", "Roaming", "Anki2");
        string destProfile = Path.Combine(destRoot, name);
        if (destination.SafeFs.DirectoryExists(destProfile))
        {
            destProfile = Path.Combine(destRoot, name + " (recovered)");
        }

        List<RecipeWrite> writes = [];
        if (RecipeDecisions.ShouldRestore(decisions, "profile"))
        {
            AddTree(destination.SafeFs, source, destProfile, source, "profile", writes, SkipProfile);
        }

        if (RecipeDecisions.ShouldRestore(decisions, "backups"))
        {
            string backups = Path.Combine(source, "backups");
            if (destination.SafeFs.DirectoryExists(backups))
            {
                AddTree(destination.SafeFs, backups, Path.Combine(destProfile, "backups"), backups, "backups", writes, static _ => false);
            }
        }

        if (RecipeDecisions.ShouldRestore(decisions, "addons"))
        {
            string addons = Path.Combine(decisions.Card.Facts["base"], "addons21");
            if (destination.SafeFs.DirectoryExists(addons))
            {
                AddTree(destination.SafeFs, addons, Path.Combine(destRoot, "addons21"), addons, "addons", writes, static _ => false);
            }
        }

        if (RecipeDecisions.ShouldRestore(decisions, "prefs"))
        {
            string prefs = Path.Combine(decisions.Card.Facts["base"], "prefs21.db");
            if (destination.SafeFs.FileExists(prefs))
            {
                writes.Add(
                    new RecipeWrite(
                        RecipeWriteKind.CopyFile,
                        prefs,
                        Path.Combine(destRoot, "prefs21.db"),
                        null,
                        1,
                        "prefs"));
            }
        }

        return new PlanResult(decisions.Card, writes);
    }

    public Task ExecuteAsync(PlanResult plan, IRecipeJournal journal, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public RecipeVerifyResult Verify(PlanResult plan)
    {
        if (plan.Writes.Any(static write =>
                write.DestinationPath.Contains("media.trash", StringComparison.OrdinalIgnoreCase) ||
                write.DestinationPath.EndsWith("collection.media.db2", StringComparison.OrdinalIgnoreCase)))
        {
            return new RecipeVerifyResult(false, "Regeneratable Anki media index or trash was planned");
        }

        bool ok = plan.Writes.All(static write => File.Exists(write.DestinationPath));
        return new RecipeVerifyResult(ok, ok ? "Anki profile files present" : "Anki destination missing");
    }

    public IReadOnlyList<Prerequisite> Prerequisites(PlanResult plan) =>
        [new Prerequisite("anki", "Anki must be closed before the profile is restored.")];

    private static IEnumerable<string> CandidateBases(ProfileContext context)
    {
        yield return Path.Combine(context.OldProfileRoot, "AppData", "Roaming", "Anki2");
        yield return Path.Combine(context.OldProfileRoot, "Documents", "Anki");
    }

    private static bool SkipProfile(string relative)
    {
        return relative.Contains("media.trash", StringComparison.OrdinalIgnoreCase) ||
            relative.Contains("backups" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            relative.Equals("backups", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(relative).Equals("collection.media.db2", StringComparison.OrdinalIgnoreCase);
    }

    private static (int Notes, int Cards, string Integrity) ReadCollectionFacts(
        ProfileContext context,
        string collection,
        string profileName)
    {
        try
        {
            string copy = ReadOnlySqlite.CopyToTemp(
                context.SafeFs,
                collection,
                Path.Combine(context.SessionTemporaryDirectory, "anki", profileName),
                "collection.anki2");
            using SqliteConnection connection = ReadOnlySqlite.OpenReadOnly(copy);
            string integrity = Scalar(connection, "PRAGMA integrity_check;") ?? "unknown";
            int notes = TableCount(connection, "notes");
            int cards = TableCount(connection, "cards");
            return (notes, cards, integrity);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            return (0, 0, "unreadable");
        }
    }

    private static int TableCount(SqliteConnection connection, string table)
    {
        try
        {
            return int.Parse(Scalar(connection, "SELECT COUNT(*) FROM " + table + ";") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    private static string? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString();
    }

    private static int CountMedia(SafeFs safeFs, string media)
    {
        if (!safeFs.DirectoryExists(media))
        {
            return 0;
        }

        int count = 0;
        CountFiles(safeFs, media, ref count);
        return count;
    }

    private static void CountFiles(SafeFs safeFs, string directory, ref int count)
    {
        foreach (string entry in safeFs.EnumerateFileSystemEntries(directory))
        {
            if (Path.GetFileName(entry).Equals("media.trash", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (safeFs.DirectoryExists(entry))
            {
                CountFiles(safeFs, entry, ref count);
            }
            else
            {
                count++;
            }
        }
    }

    internal static void AddTree(
        SafeFs safeFs,
        string source,
        string dest,
        string sourceRoot,
        string component,
        List<RecipeWrite> writes,
        Func<string, bool> skip)
    {
        if (safeFs.FileExists(source))
        {
            writes.Add(new RecipeWrite(RecipeWriteKind.CopyFile, source, dest, null, 1, component));
            return;
        }

        if (!safeFs.DirectoryExists(source))
        {
            return;
        }

        foreach (string entry in safeFs.EnumerateFileSystemEntries(source))
        {
            string relative = Path.GetRelativePath(StripExtended(sourceRoot), StripExtended(entry));
            if (skip(relative))
            {
                continue;
            }

            string target = Path.Combine(dest, Path.GetRelativePath(StripExtended(source), StripExtended(entry)));
            if (safeFs.DirectoryExists(entry))
            {
                AddTree(safeFs, entry, target, sourceRoot, component, writes, skip);
            }
            else
            {
                writes.Add(new RecipeWrite(RecipeWriteKind.CopyFile, entry, target, null, 1, component));
            }
        }
    }

    private static string StripExtended(string path)
    {
        const string prefix = @"\\?\";
        return path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
    }
}
