using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.Recipes;

namespace WinOldRecovery.Recipes;

public sealed class OutlookRecipe : IRecipe
{
    public string Id => "outlook";

    public DetectResult Detect(ProfileContext context)
    {
        List<RecipeCard> cards = [];
        List<(string RelativePath, string Kind, string Detail)> badges = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        if (context.Index is { } index)
        {
            foreach (string pst in index.FilesWithExtensions([".pst"], skipAppData: false))
            {
                if (!IsUnderAny(pst, PstSearchRoots(context.OldProfileRoot)) ||
                    !seen.Add(Path.GetFullPath(pst)))
                {
                    continue;
                }

                AddPst(context, cards, badges, pst);
            }

            string localOstRoot = Path.Combine(
                context.OldProfileRoot,
                "AppData",
                "Local",
                "Microsoft",
                "Outlook");
            foreach (string ost in index.FilesWithExtensions([".ost"], skipAppData: false))
            {
                if (!IsUnderAny(ost, [localOstRoot]))
                {
                    continue;
                }

                AddOst(context, cards, badges, ost);
            }

            return new DetectResult(cards, badges);
        }

        foreach (string root in PstSearchRoots(context.OldProfileRoot))
        {
            foreach (string pst in DetectorWalk.EnumerateFiles(context.SafeFs, root, 6))
            {
                if (!Path.GetExtension(pst).Equals(".pst", StringComparison.OrdinalIgnoreCase) ||
                    !seen.Add(Path.GetFullPath(pst)))
                {
                    continue;
                }

                AddPst(context, cards, badges, pst);
            }
        }

        string ostRoot = Path.Combine(
            context.OldProfileRoot,
            "AppData",
            "Local",
            "Microsoft",
            "Outlook");
        foreach (string ost in DetectorWalk.EnumerateFiles(context.SafeFs, ostRoot, 2))
        {
            if (!Path.GetExtension(ost).Equals(".ost", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AddOst(context, cards, badges, ost);
        }

        return new DetectResult(cards, badges);
    }

    public PlanResult Plan(CardDecisions decisions, DestinationContext destination)
    {
        string kind = decisions.Card.Facts["kind"];
        if (kind == "ost" || !RecipeDecisions.ShouldRestore(decisions, "mail"))
        {
            return new PlanResult(decisions.Card, []);
        }

        List<RecipeWrite> writes = [];
        string source = decisions.Card.Facts["source"];
        string dest = Path.Combine(
            destination.DestinationProfileRoot,
            DetectorWalk.RelativeUnder(
                decisions.Card.Facts["profileRoot"],
                source));
        DetectorWalk.CopyFileKeepBoth(destination.SafeFs, source, dest, "mail", writes);
        return new PlanResult(decisions.Card, writes);
    }

    public Task ExecuteAsync(PlanResult plan, IRecipeJournal journal, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public RecipeVerifyResult Verify(PlanResult plan)
    {
        if (plan.Card.Facts.TryGetValue("kind", out string? kind) &&
            kind.Equals("ost", StringComparison.OrdinalIgnoreCase))
        {
            if (plan.Writes.Count > 0)
            {
                return new RecipeVerifyResult(false, "Outlook OST must not be restored");
            }

            return new RecipeVerifyResult(true, "OST left behind");
        }

        if (plan.Writes.Any(static write =>
                Path.GetExtension(write.DestinationPath)
                    .Equals(".ost", StringComparison.OrdinalIgnoreCase)))
        {
            return new RecipeVerifyResult(false, "Outlook OST must not be restored");
        }

        return DetectorWalk.FilesPresent(plan, "Outlook files present", "Outlook destination missing");
    }

    public IReadOnlyList<Prerequisite> Prerequisites(PlanResult plan) =>
        [new Prerequisite("outlook", "Close Outlook before opening a restored PST.")];

    private static RecipeCard CreateCard(
        ProfileContext context,
        string title,
        string kind,
        string source,
        Decision decision,
        string why,
        string restored,
        string cloud,
        string leftBehind)
    {
        return new RecipeCard(
            "outlook",
            title,
            why,
            why,
            restored,
            cloud,
            kind == "ost" ? "The OST cache regenerates from the server." : "Nothing regenerates.",
            leftBehind,
            [
                new RecipeComponent("mail", kind.ToUpperInvariant(), Path.GetFileName(source), decision, false, null, true),
            ],
            context.ProfileName + ":" + kind + ":" + DetectorWalk.RelativeUnder(context.OldProfileRoot, source),
            new Dictionary<string, string>
            {
                ["source"] = source,
                ["kind"] = kind,
                ["profileRoot"] = context.OldProfileRoot,
            });
    }

    private static void AddPst(
        ProfileContext context,
        List<RecipeCard> cards,
        List<(string RelativePath, string Kind, string Detail)> badges,
        string pst)
    {
        string name = Path.GetFileName(pst);
        cards.Add(
            CreateCard(
                context,
                "Outlook PST — " + name,
                "pst",
                pst,
                Decision.Restore,
                "Open PST in Outlook: File → Open & Export → Open Outlook Data File",
                "The PST data file.",
                "A copy on another computer or the server mailbox.",
                "The archive is unique; Outlook will not recreate it."));
        DetectorWalk.AddTreeBadge(badges, context.OldProfileRoot, pst, "Outlook", name);
    }

    private static void AddOst(
        ProfileContext context,
        List<RecipeCard> cards,
        List<(string RelativePath, string Kind, string Detail)> badges,
        string ost)
    {
        cards.Add(
            CreateCard(
                context,
                "Outlook OST — " + Path.GetFileName(ost),
                "ost",
                ost,
                Decision.LeaveBehind,
                "OST files are rebuilt from the mail server. Leave them behind unless you have no server copy.",
                "Nothing by default. OST is regenerable from the server.",
                "Sign in to the same mailbox.",
                "Cached mail regenerates after you connect."));
        DetectorWalk.AddTreeBadge(
            badges,
            context.OldProfileRoot,
            ost,
            "Outlook",
            Path.GetFileName(ost));
    }

    private static bool IsUnderAny(string path, IEnumerable<string> roots)
    {
        string full = Path.GetFullPath(path);
        foreach (string root in roots)
        {
            string rootFull = Path.GetFullPath(root);
            if (full.Equals(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (full.StartsWith(rootFull.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> PstSearchRoots(string profileRoot)
    {
        yield return Path.Combine(profileRoot, "Documents");
        yield return Path.Combine(profileRoot, "Desktop");
        yield return Path.Combine(profileRoot, "Downloads");
        yield return Path.Combine(profileRoot, "AppData", "Local", "Microsoft", "Outlook");
        yield return Path.Combine(profileRoot, "AppData", "Roaming", "Microsoft", "Outlook");
    }
}
