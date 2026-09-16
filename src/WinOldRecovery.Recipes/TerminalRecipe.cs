using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.Recipes;

namespace WinOldRecovery.Recipes;

public sealed class TerminalRecipe : IRecipe
{
    public string Id => "windows-terminal";

    public DetectResult Detect(ProfileContext context)
    {
        List<RecipeCard> cards = [];
        List<(string RelativePath, string Kind, string Detail)> badges = [];
        foreach (string settings in FindSettings(context))
        {
            string? name = PackageName(settings);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            cards.Add(
                new RecipeCard(
                    Id,
                    "Windows Terminal settings",
                    "profiles, color schemes, and keybindings from the old Terminal package.",
                    "Terminal settings are small and usually unique to your machine.",
                    "settings.json, written as settings.from-windows-old.json when a destination file already exists.",
                    "None.",
                    "Nothing regenerates.",
                    "You keep the new Terminal defaults.",
                    [
                        new RecipeComponent("settings", "settings.json", name, Decision.Restore, false, null, false),
                    ],
                    context.ProfileName + ":" + name,
                    new Dictionary<string, string>
                    {
                        ["source"] = settings,
                        ["package"] = name,
                    }));
            DetectorWalk.AddTreeBadge(badges, context.OldProfileRoot, settings, "Windows Terminal", "settings");
        }

        return new DetectResult(cards, badges);
    }

    public PlanResult Plan(CardDecisions decisions, DestinationContext destination)
    {
        if (!RecipeDecisions.ShouldRestore(decisions, "settings"))
        {
            return new PlanResult(decisions.Card, []);
        }

        string package = decisions.Card.Facts["package"];
        string destDir = Path.Combine(
            destination.DestinationProfileRoot,
            "AppData",
            "Local",
            "Packages",
            package,
            "LocalState");
        string dest = Path.Combine(destDir, "settings.json");
        if (destination.SafeFs.FileExists(dest))
        {
            dest = Path.Combine(destDir, "settings.from-windows-old.json");
        }

        return new PlanResult(
            decisions.Card,
            [
                new RecipeWrite(
                    RecipeWriteKind.CopyFile,
                    decisions.Card.Facts["source"],
                    dest,
                    null,
                    1,
                    "settings"),
            ]);
    }

    public Task ExecuteAsync(PlanResult plan, IRecipeJournal journal, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public RecipeVerifyResult Verify(PlanResult plan)
    {
        RecipeVerifyResult present = DetectorWalk.FilesPresent(
            plan,
            "Terminal settings present",
            "Terminal settings missing");
        if (!present.Ok)
        {
            return present;
        }

        foreach (RecipeWrite write in plan.Writes)
        {
            if (!Path.GetFileName(write.DestinationPath)
                    .Equals("settings.from-windows-old.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? directory = Path.GetDirectoryName(write.DestinationPath);
            if (string.IsNullOrEmpty(directory))
            {
                continue;
            }

            string live = Path.Combine(directory, "settings.json");
            if (!File.Exists(live))
            {
                return new RecipeVerifyResult(false, "Terminal existing settings.json missing");
            }

            if (write.SourcePath is string source &&
                File.Exists(source) &&
                FilesMatch(source, live))
            {
                return new RecipeVerifyResult(false, "Windows Terminal overwrote existing settings.json");
            }
        }

        return present;
    }

    private static bool FilesMatch(string left, string right)
    {
        byte[] leftBytes = File.ReadAllBytes(left);
        byte[] rightBytes = File.ReadAllBytes(right);
        return leftBytes.AsSpan().SequenceEqual(rightBytes);
    }

    public IReadOnlyList<Prerequisite> Prerequisites(PlanResult plan) =>
        [new Prerequisite("WindowsTerminal", "Close Windows Terminal before replacing settings.")];

    private static IEnumerable<string> FindSettings(ProfileContext context)
    {
        if (context.Index is { } index)
        {
            foreach (string json in index.FilesWithExtensions(
                         [".json"],
                         skipAppData: false,
                         relativeUnderProfile: Path.Combine("AppData", "Local", "Packages")))
            {
                if (IsTerminalSettings(json))
                {
                    yield return json;
                }
            }

            yield break;
        }

        string packages = Path.Combine(context.OldProfileRoot, "AppData", "Local", "Packages");
        if (!context.SafeFs.DirectoryExists(packages))
        {
            yield break;
        }

        foreach (string package in context.SafeFs.EnumerateFileSystemEntries(packages))
        {
            if (!context.SafeFs.DirectoryExists(package) || DetectorWalk.IsReparse(package))
            {
                continue;
            }

            string name = Path.GetFileName(DetectorWalk.StripExtended(package));
            if (!name.StartsWith("Microsoft.WindowsTerminal", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string settings = Path.Combine(package, "LocalState", "settings.json");
            if (context.SafeFs.FileExists(settings))
            {
                yield return DetectorWalk.StripExtended(settings);
            }
        }
    }

    private static bool IsTerminalSettings(string path)
    {
        string normalized = DetectorWalk.StripExtended(path).Replace('/', '\\');
        return Path.GetFileName(normalized).Equals("settings.json", StringComparison.OrdinalIgnoreCase) &&
            normalized.Contains("Microsoft.WindowsTerminal", StringComparison.OrdinalIgnoreCase) &&
            normalized.Contains(@"\LocalState\", StringComparison.OrdinalIgnoreCase);
    }

    private static string? PackageName(string settingsPath)
    {
        string? localState = Path.GetDirectoryName(DetectorWalk.StripExtended(settingsPath));
        if (string.IsNullOrEmpty(localState) ||
            !Path.GetFileName(localState).Equals("LocalState", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? package = Path.GetDirectoryName(localState);
        if (string.IsNullOrEmpty(package))
        {
            return null;
        }

        string name = Path.GetFileName(package);
        return name.StartsWith("Microsoft.WindowsTerminal", StringComparison.OrdinalIgnoreCase) ? name : null;
    }
}
