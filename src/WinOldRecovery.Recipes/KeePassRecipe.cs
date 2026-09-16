using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.Recipes;

namespace WinOldRecovery.Recipes;

public sealed class KeePassRecipe : IRecipe
{
    public string Id => "keepass";

    public DetectResult Detect(ProfileContext context)
    {
        List<RecipeCard> cards = [];
        List<(string RelativePath, string Kind, string Detail)> badges = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string vault in FindVaults(context))
        {
            if (!seen.Add(vault))
            {
                continue;
            }

            string name = Path.GetFileName(vault);
            string stem = Path.GetFileNameWithoutExtension(vault);
            string directory = Path.GetDirectoryName(vault) ?? context.OldProfileRoot;
            List<string> keys = [];
            foreach (string extension in new[] { ".keyx", ".key" })
            {
                string sibling = Path.Combine(directory, stem + extension);
                if (context.SafeFs.FileExists(sibling))
                {
                    keys.Add(sibling);
                }
            }

            cards.Add(
                new RecipeCard(
                    Id,
                    "KeePass — " + name,
                    "Your password vault. Needs your master password and any key file.",
                    "This is often the only copy of site passwords after a reinstall.",
                    "The database file" + (keys.Count > 0 ? " and its sibling key file" : string.Empty) + ".",
                    "A cloud backup of the vault, if you kept one.",
                    "Nothing regenerates. The vault is unique.",
                    "You cannot open the passwords that lived only in this file.",
                    [
                        new RecipeComponent("vault", "Database", name, Decision.Restore, false, null, true),
                    ],
                    context.ProfileName + ":" + DetectorWalk.RelativeUnder(context.OldProfileRoot, vault),
                    new Dictionary<string, string>
                    {
                        ["source"] = vault,
                        ["keys"] = string.Join('|', keys),
                        ["relative"] = DetectorWalk.RelativeUnder(context.OldProfileRoot, vault),
                    }));
            DetectorWalk.AddTreeBadge(badges, context.OldProfileRoot, vault, "KeePass", name);
        }

        return new DetectResult(cards, badges);
    }

    public PlanResult Plan(CardDecisions decisions, DestinationContext destination)
    {
        if (!RecipeDecisions.ShouldRestore(decisions, "vault"))
        {
            return new PlanResult(decisions.Card, []);
        }

        List<RecipeWrite> writes = [];
        string source = decisions.Card.Facts["source"];
        string relative = decisions.Card.Facts["relative"];
        string dest = Path.Combine(destination.DestinationProfileRoot, relative);
        DetectorWalk.CopyFileKeepBoth(destination.SafeFs, source, dest, "vault", writes);
        foreach (string key in decisions.Card.Facts["keys"].Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            string keyDest = Path.Combine(
                Path.GetDirectoryName(dest) ?? destination.DestinationProfileRoot,
                Path.GetFileName(key));
            DetectorWalk.CopyFileKeepBoth(destination.SafeFs, key, keyDest, "vault", writes);
        }

        return new PlanResult(decisions.Card, writes);
    }

    public Task ExecuteAsync(PlanResult plan, IRecipeJournal journal, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public RecipeVerifyResult Verify(PlanResult plan)
    {
        if (plan.Writes.Count > 0 &&
            plan.Card.Facts.TryGetValue("keys", out string? packed) &&
            !string.IsNullOrEmpty(packed))
        {
            RecipeWrite? vault = plan.Writes.FirstOrDefault(static write =>
            {
                string extension = Path.GetExtension(write.DestinationPath);
                return extension.Equals(".kdbx", StringComparison.OrdinalIgnoreCase) ||
                       extension.Equals(".kdb", StringComparison.OrdinalIgnoreCase);
            });
            string? directory = Path.GetDirectoryName(vault?.DestinationPath);
            if (string.IsNullOrEmpty(directory))
            {
                return new RecipeVerifyResult(false, "KeePass key file missing");
            }

            foreach (string key in packed.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                string destKey = Path.Combine(directory, Path.GetFileName(key));
                if (!File.Exists(destKey))
                {
                    return new RecipeVerifyResult(false, "KeePass key file missing");
                }
            }
        }

        return DetectorWalk.FilesPresent(plan, "KeePass files present", "KeePass destination missing");
    }

    public IReadOnlyList<Prerequisite> Prerequisites(PlanResult plan) => [];

    private static IEnumerable<string> FindVaults(ProfileContext context)
    {
        if (context.Index is { } index)
        {
            foreach (string vault in index.FilesWithExtensions(".kdbx", ".kdb"))
            {
                yield return vault;
            }

            yield break;
        }

        string[] roots =
        [
            Path.Combine(context.OldProfileRoot, "Documents"),
            Path.Combine(context.OldProfileRoot, "Desktop"),
            Path.Combine(context.OldProfileRoot, "Downloads"),
            context.OldProfileRoot,
        ];

        foreach (string root in roots)
        {
            foreach (string file in DetectorWalk.EnumerateFiles(context.SafeFs, root, maxDepth: 6))
            {
                string extension = Path.GetExtension(file);
                if (extension.Equals(".kdbx", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".kdb", StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }
    }
}
