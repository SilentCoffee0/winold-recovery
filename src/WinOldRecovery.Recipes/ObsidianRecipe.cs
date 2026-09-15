using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.Recipes;

namespace WinOldRecovery.Recipes;

public sealed class ObsidianRecipe : IRecipe
{
    public string Id => "obsidian";

    public DetectResult Detect(ProfileContext context)
    {
        List<RecipeCard> cards = [];
        List<(string RelativePath, string Kind, string Detail)> badges = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        string[] roots =
        [
            Path.Combine(context.OldProfileRoot, "Documents"),
            Path.Combine(context.OldProfileRoot, "Desktop"),
            context.OldProfileRoot,
        ];

        foreach (string root in roots)
        {
            foreach (string vault in FindVaults(context, root))
            {
                if (!seen.Add(vault))
                {
                    continue;
                }

                string name = Path.GetFileName(vault);
                cards.Add(
                    new RecipeCard(
                        Id,
                        "Obsidian vault — " + name,
                        "A notes vault. The .obsidian folder carries plugins and workspace layout.",
                        "Vaults are often the only local copy of personal notes.",
                        "The whole vault folder, including .obsidian plugins.",
                        "Obsidian Sync or a git remote, if you used one.",
                        "Workspace layout can be rebuilt. The notes cannot.",
                        "You keep an empty Obsidian and lose the notes that lived only here.",
                        [
                            new RecipeComponent("vault", "Vault", name, Decision.Restore, false, null, false),
                        ],
                        context.ProfileName + ":" + DetectorWalk.RelativeUnder(context.OldProfileRoot, vault),
                        new Dictionary<string, string>
                        {
                            ["source"] = vault,
                            ["relative"] = DetectorWalk.RelativeUnder(context.OldProfileRoot, vault),
                        }));
                DetectorWalk.AddTreeBadge(badges, context.OldProfileRoot, vault, "Obsidian", name);
            }
        }

        return new DetectResult(cards, badges);
    }

    public PlanResult Plan(CardDecisions decisions, DestinationContext destination)
    {
        if (!RecipeDecisions.ShouldRestore(decisions, "vault"))
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
        AnkiRecipe.AddTree(destination.SafeFs, source, dest, source, "vault", writes, static _ => false);
        return new PlanResult(decisions.Card, writes);
    }

    public Task ExecuteAsync(PlanResult plan, IRecipeJournal journal, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public RecipeVerifyResult Verify(PlanResult plan) =>
        DetectorWalk.FilesPresent(plan, "Obsidian vault present", "Obsidian vault missing");

    public IReadOnlyList<Prerequisite> Prerequisites(PlanResult plan) =>
        [new Prerequisite("Obsidian", "Close Obsidian before restoring a vault.")];

    private static IEnumerable<string> FindVaults(ProfileContext context, string root)
    {
        if (!context.SafeFs.DirectoryExists(root) || DetectorWalk.IsReparse(root))
        {
            yield break;
        }

        Stack<(string Path, int Depth)> stack = new();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            (string directory, int depth) = stack.Pop();
            IReadOnlyList<string> entries;
            try
            {
                entries = context.SafeFs.EnumerateFileSystemEntries(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            bool isVault = false;
            List<string> children = [];
            foreach (string entry in entries)
            {
                if (DetectorWalk.IsReparse(entry))
                {
                    continue;
                }

                if (context.SafeFs.DirectoryExists(entry))
                {
                    if (Path.GetFileName(entry).Equals(".obsidian", StringComparison.OrdinalIgnoreCase))
                    {
                        isVault = true;
                    }
                    else
                    {
                        children.Add(entry);
                    }
                }
            }

            if (isVault)
            {
                yield return directory;
                continue;
            }

            if (depth >= 5)
            {
                continue;
            }

            foreach (string child in children)
            {
                string name = Path.GetFileName(child);
                if (name.Equals("AppData", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                stack.Push((child, depth + 1));
            }
        }
    }
}
