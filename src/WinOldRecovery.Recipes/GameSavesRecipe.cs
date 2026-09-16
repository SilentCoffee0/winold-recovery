using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.Recipes;

namespace WinOldRecovery.Recipes;

public sealed class GameSavesRecipe : IRecipe
{
    public string Id => "game-saves";

    private static readonly (string Relative, string Title, Decision Default)[] KnownFolders =
    [
        ("Saved Games", "Saved Games", Decision.Restore),
        (Path.Combine("Documents", "My Games"), "My Games", Decision.Restore),
        (Path.Combine("AppData", "Roaming", ".minecraft"), "Minecraft", Decision.Restore),
        (Path.Combine("AppData", "Local", "EpicGamesLauncher"), "Epic Games", Decision.Undecided),
        (Path.Combine("AppData", "Local", "Epic Games"), "Epic Games", Decision.Undecided),
        (Path.Combine("AppData", "Roaming", "Epic Games"), "Epic Games", Decision.Undecided),
        (Path.Combine("AppData", "Local", "Ubisoft Game Launcher"), "Ubisoft", Decision.Undecided),
        (Path.Combine("AppData", "Roaming", "Ubisoft"), "Ubisoft", Decision.Undecided),
        (Path.Combine("AppData", "Roaming", "Electronic Arts"), "EA / Origin", Decision.Undecided),
        (Path.Combine("AppData", "Local", "Origin"), "EA / Origin", Decision.Undecided),
        (Path.Combine("AppData", "Local", "Battle.net"), "Battle.net", Decision.Undecided),
        (Path.Combine("AppData", "Roaming", "Battle.net"), "Battle.net", Decision.Undecided),
        (Path.Combine("AppData", "Local", "Blizzard Entertainment"), "Blizzard", Decision.Undecided),
        (Path.Combine("AppData", "Roaming", "Blizzard Entertainment"), "Blizzard", Decision.Undecided),
        (Path.Combine("AppData", "Local", "Riot Games"), "Riot Games", Decision.Undecided),
        (Path.Combine("AppData", "Roaming", "Riot Games"), "Riot Games", Decision.Undecided),
        (Path.Combine("AppData", "Roaming", "Goldberg SteamEmu Saves"), "Steam-compatible saves", Decision.Restore),
        (Path.Combine("AppData", "Roaming", "GSE Saves"), "Steam-compatible saves", Decision.Restore),
        (Path.Combine("AppData", "Roaming", "CODEX"), "Steam-compatible saves", Decision.Restore),
        (Path.Combine("AppData", "Roaming", "SmartSteamEmu"), "Steam-compatible saves", Decision.Restore),
        (Path.Combine("AppData", "Roaming", "Player"), "Player saves", Decision.Restore),
        (Path.Combine("AppData", "Roaming", "GOG.com", "Galaxy", "Applications"), "GOG Galaxy", Decision.Undecided),
        (Path.Combine("AppData", "LocalLow", "Epic Games"), "Epic Games", Decision.Undecided),
        (Path.Combine("AppData", "LocalLow", "Ubisoft"), "Ubisoft", Decision.Undecided),
        (Path.Combine("AppData", "LocalLow", "Electronic Arts"), "EA / Origin", Decision.Undecided),
        (Path.Combine("AppData", "LocalLow", "Origin"), "EA / Origin", Decision.Undecided),
        (Path.Combine("AppData", "LocalLow", "Battle.net"), "Battle.net", Decision.Undecided),
        (Path.Combine("AppData", "LocalLow", "Blizzard Entertainment"), "Blizzard", Decision.Undecided),
        (Path.Combine("AppData", "LocalLow", "Riot Games"), "Riot Games", Decision.Undecided),
        (Path.Combine("AppData", "LocalLow", "Goldberg SteamEmu Saves"), "Steam-compatible saves", Decision.Restore),
        (Path.Combine("AppData", "LocalLow", "GSE Saves"), "Steam-compatible saves", Decision.Restore),
        (Path.Combine("AppData", "LocalLow", "CODEX"), "Steam-compatible saves", Decision.Restore),
        (Path.Combine("AppData", "LocalLow", "SmartSteamEmu"), "Steam-compatible saves", Decision.Restore),
        (Path.Combine("AppData", "LocalLow", "Player"), "Player saves", Decision.Restore),
        (Path.Combine("AppData", "Local", "Goldberg SteamEmu Saves"), "Steam-compatible saves", Decision.Restore),
        (Path.Combine("AppData", "Local", "GSE Saves"), "Steam-compatible saves", Decision.Restore),
        (Path.Combine("AppData", "Local", "CODEX"), "Steam-compatible saves", Decision.Restore),
        (Path.Combine("AppData", "Local", "SmartSteamEmu"), "Steam-compatible saves", Decision.Restore),
    ];

    private static readonly string[] SteamLibrarySaveRelatives =
    [
        Path.Combine("Saved", "SaveGames"),
        "SaveGames",
        "saves",
        "save",
    ];

    private static readonly string[] SteamCommonRelatives =
    [
        Path.Combine("Program Files (x86)", "Steam", "steamapps", "common"),
        Path.Combine("Program Files", "Steam", "steamapps", "common"),
        Path.Combine("Steam", "steamapps", "common"),
    ];

    private static readonly string[] SteamLibrarySaveNames = ["SaveGames", "saves", "save"];

    public DetectResult Detect(ProfileContext context)
    {
        List<RecipeCard> cards = [];
        List<(string RelativePath, string Kind, string Detail)> badges = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string relative, string title, Decision suggested) in KnownFolders)
        {
            string source = Path.Combine(context.OldProfileRoot, relative);
            if (!FolderPresent(context, source, relative) || !seen.Add(source))
            {
                continue;
            }

            AddCard(context, cards, badges, source, relative, title, suggested);
        }

        if (context.Index is { } steamIndex)
        {
            foreach (string steamRel in new[]
                     {
                         Path.Combine("Program Files (x86)", "Steam"),
                         Path.Combine("Program Files", "Steam"),
                     })
            {
                foreach (string parent in steamIndex.ParentsOfChildNamedUnder(steamRel, "userdata"))
                {
                    string userdata = Path.Combine(parent, "userdata");
                    if (!seen.Add(userdata))
                    {
                        continue;
                    }

                    AddCard(
                        context,
                        cards,
                        badges,
                        userdata,
                        Path.Combine("Saved Games", "Steam userdata"),
                        "Steam userdata",
                        Decision.Restore);
                }
            }
        }
        else
        {
            foreach (string steamUserdata in SteamUserdataFolders(context.OldProfileRoot))
            {
                if (!context.SafeFs.DirectoryExists(steamUserdata) ||
                    DetectorWalk.IsReparse(steamUserdata) ||
                    !seen.Add(steamUserdata))
                {
                    continue;
                }

                AddCard(
                    context,
                    cards,
                    badges,
                    steamUserdata,
                    Path.Combine("Saved Games", "Steam userdata"),
                    "Steam userdata",
                    Decision.Restore);
            }
        }

        foreach ((string source, string relative, string title) in SteamLibrarySaves(context))
        {
            if (!seen.Add(source))
            {
                continue;
            }

            AddCard(context, cards, badges, source, relative, title, Decision.Restore);
        }

        if (context.Index is { } extraIndex)
        {
            foreach (string parent in extraIndex.ParentsOfChildNamed("userdata"))
            {
                string userdata = Path.Combine(parent, "userdata");
                if (!seen.Add(userdata))
                {
                    continue;
                }

                string relative = DetectorWalk.RelativeUnder(context.OldProfileRoot, userdata);
                if (relative.Contains("Steam", StringComparison.OrdinalIgnoreCase))
                {
                    AddCard(context, cards, badges, userdata, relative, "Steam userdata", Decision.Restore);
                }
            }

            foreach (string parent in extraIndex.ParentsOfChildNamed("SaveGames"))
            {
                string saveGames = Path.Combine(parent, "SaveGames");
                if (!seen.Add(saveGames))
                {
                    continue;
                }

                string relative = DetectorWalk.RelativeUnder(context.OldProfileRoot, saveGames);
                if (relative.Contains(
                        "Saved" + Path.DirectorySeparatorChar + "SaveGames",
                        StringComparison.OrdinalIgnoreCase))
                {
                    AddCard(
                        context,
                        cards,
                        badges,
                        saveGames,
                        relative,
                        "Unreal saves — " + Path.GetFileName(parent),
                        Decision.Restore);
                }
            }
        }

        return new DetectResult(cards, badges);
    }

    public PlanResult Plan(CardDecisions decisions, DestinationContext destination)
    {
        if (!RecipeDecisions.ShouldRestore(decisions, "saves"))
        {
            return new PlanResult(decisions.Card, []);
        }

        string source = decisions.Card.Facts["source"];
        string dest = Path.Combine(destination.DestinationProfileRoot, decisions.Card.Facts["relative"]);
        if (destination.SafeFs.DirectoryExists(dest))
        {
            dest = RecipeDecisions.ConflictName(dest);
        }

        List<RecipeWrite> writes = [];
        AnkiRecipe.AddTree(destination.SafeFs, source, dest, source, "saves", writes, static _ => false);
        return new PlanResult(decisions.Card, writes);
    }

    public Task ExecuteAsync(PlanResult plan, IRecipeJournal journal, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public RecipeVerifyResult Verify(PlanResult plan) =>
        DetectorWalk.FilesPresentMatchingSourceLength(
            plan,
            "Game saves present",
            "Game save destination missing",
            "Game save destination size does not match the source");

    public IReadOnlyList<Prerequisite> Prerequisites(PlanResult plan) => [];

    private static void AddCard(
        ProfileContext context,
        List<RecipeCard> cards,
        List<(string RelativePath, string Kind, string Detail)> badges,
        string source,
        string relative,
        string title,
        Decision suggested)
    {
        cards.Add(
            new RecipeCard(
                "game-saves",
                "Game saves — " + title,
                "Save files, screenshots, and settings from games you played on the old install.",
                "Saves are often the only local copy of progress, including titles that did not use Steam Cloud.",
                "The whole save folder, merged with keep-both names when the destination already exists.",
                "Steam Cloud, Xbox Cloud, or the publisher launcher, if that game used one.",
                "Launcher caches regenerate. Save files do not.",
                "You keep a clean profile and lose progress that lived only here.",
                [
                    new RecipeComponent("saves", "Save folder", Path.GetFileName(source), suggested, false, null, false),
                ],
                context.ProfileName + ":" + source.Replace('\\', '/'),
                new Dictionary<string, string>
                {
                    ["source"] = source,
                    ["relative"] = relative,
                }));
        DetectorWalk.AddTreeBadge(badges, context.OldProfileRoot, source, "Game save", title);
    }

    private static bool FolderPresent(ProfileContext context, string source, string relative)
    {
        if (context.Index is { } index)
        {
            string name = Path.GetFileName(relative);
            string? parent = Path.GetDirectoryName(relative);
            if (string.IsNullOrEmpty(parent) || parent == ".")
            {
                return index.CountFilesUnder(relative) > 0 || index.ParentsOfChildNamed(name).Count > 0;
            }

            return DetectorWalk.IndexedChildFolder(index, parent, name);
        }

        return context.SafeFs.DirectoryExists(source) && !DetectorWalk.IsReparse(source);
    }

    private static IEnumerable<string> SteamUserdataFolders(string oldProfileRoot)
    {
        yield return Path.GetFullPath(
            Path.Combine(oldProfileRoot, "..", "..", "Program Files (x86)", "Steam", "userdata"));
        yield return Path.GetFullPath(
            Path.Combine(oldProfileRoot, "..", "..", "Program Files", "Steam", "userdata"));
    }

    private static IEnumerable<(string Source, string Relative, string Title)> SteamLibrarySaves(
        ProfileContext context)
    {
        if (context.Index is { } index)
        {
            foreach (string commonRel in SteamCommonRelatives)
            {
                foreach (string saveName in SteamLibrarySaveNames)
                {
                    foreach (string parent in index.ParentsOfChildNamedUnder(commonRel, saveName))
                    {
                        string source = Path.Combine(parent, saveName);
                        if (!TrySteamLibraryCard(source, saveName, out string relative, out string title))
                        {
                            continue;
                        }

                        yield return (source, relative, title);
                    }
                }
            }

            yield break;
        }

        foreach (string common in SteamCommonFolders(context.OldProfileRoot))
        {
            if (!context.SafeFs.DirectoryExists(common) || DetectorWalk.IsReparse(common))
            {
                continue;
            }

            foreach (string game in context.SafeFs.EnumerateDirectories(common))
            {
                if (DetectorWalk.IsReparse(game))
                {
                    continue;
                }

                string name = Path.GetFileName(DetectorWalk.StripExtended(game));
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                foreach (string saveRelative in SteamLibrarySaveRelatives)
                {
                    string source = DetectorWalk.StripExtended(Path.Combine(game, saveRelative));
                    if (!context.SafeFs.DirectoryExists(source) || DetectorWalk.IsReparse(source))
                    {
                        continue;
                    }

                    yield return (
                        source,
                        Path.Combine("Saved Games", name, Path.GetFileName(source)),
                        "Steam library — " + name);
                }
            }
        }
    }

    private static bool TrySteamLibraryCard(
        string source,
        string saveName,
        out string relative,
        out string title)
    {
        relative = string.Empty;
        title = string.Empty;
        string normalized = DetectorWalk.StripExtended(source).Replace('/', '\\');
        if (!normalized.Contains(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? parent = Path.GetDirectoryName(normalized);
        if (string.IsNullOrEmpty(parent))
        {
            return false;
        }

        string game = Path.GetFileName(parent);
        if (saveName.Equals("SaveGames", StringComparison.OrdinalIgnoreCase) &&
            game.Equals("Saved", StringComparison.OrdinalIgnoreCase))
        {
            string? gameDir = Path.GetDirectoryName(parent);
            if (string.IsNullOrEmpty(gameDir))
            {
                return false;
            }

            game = Path.GetFileName(gameDir);
        }

        if (string.IsNullOrEmpty(game))
        {
            return false;
        }

        relative = Path.Combine("Saved Games", game, Path.GetFileName(normalized));
        title = "Steam library — " + game;
        return true;
    }

    private static IEnumerable<string> SteamCommonFolders(string oldProfileRoot)
    {
        yield return Path.GetFullPath(
            Path.Combine(oldProfileRoot, "..", "..", "Program Files (x86)", "Steam", "steamapps", "common"));
        yield return Path.GetFullPath(
            Path.Combine(oldProfileRoot, "..", "..", "Program Files", "Steam", "steamapps", "common"));
        yield return Path.GetFullPath(
            Path.Combine(oldProfileRoot, "..", "..", "Steam", "steamapps", "common"));
    }
}
