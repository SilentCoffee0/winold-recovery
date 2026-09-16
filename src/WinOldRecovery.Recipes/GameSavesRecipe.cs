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

    public DetectResult Detect(ProfileContext context)
    {
        List<RecipeCard> cards = [];
        List<(string RelativePath, string Kind, string Detail)> badges = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string relative, string title, Decision suggested) in KnownFolders)
        {
            string source = Path.Combine(context.OldProfileRoot, relative);
            if (!context.SafeFs.DirectoryExists(source) || DetectorWalk.IsReparse(source) || !seen.Add(source))
            {
                continue;
            }

            AddCard(context, cards, badges, source, relative, title, suggested);
        }

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

        IEnumerable<string> extraParents = context.Index is { } index
            ? index.ParentsOfChildNamed("userdata")
                .Concat(index.ParentsOfChildNamed("SaveGames"))
            : [];
        foreach (string parent in extraParents)
        {
            string userdata = Path.Combine(parent, "userdata");
            if (context.SafeFs.DirectoryExists(userdata) &&
                !DetectorWalk.IsReparse(userdata) &&
                seen.Add(userdata))
            {
                string relative = DetectorWalk.RelativeUnder(context.OldProfileRoot, userdata);
                if (relative.Contains("Steam", StringComparison.OrdinalIgnoreCase))
                {
                    AddCard(context, cards, badges, userdata, relative, "Steam userdata", Decision.Restore);
                }
            }

            string saveGames = Path.Combine(parent, "SaveGames");
            if (context.SafeFs.DirectoryExists(saveGames) &&
                !DetectorWalk.IsReparse(saveGames) &&
                seen.Add(saveGames))
            {
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
        DetectorWalk.FilesPresent(plan, "Game saves present", "Game save destination missing");

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

    private static IEnumerable<string> SteamUserdataFolders(string oldProfileRoot)
    {
        yield return Path.GetFullPath(
            Path.Combine(oldProfileRoot, "..", "..", "Program Files (x86)", "Steam", "userdata"));
        yield return Path.GetFullPath(
            Path.Combine(oldProfileRoot, "..", "..", "Program Files", "Steam", "userdata"));
    }
}
