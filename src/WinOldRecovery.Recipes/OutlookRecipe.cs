using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.Recipes;

namespace WinOldRecovery.Recipes;

public sealed class OutlookRecipe : IRecipe
{
    public string Id => "outlook";

    public DetectResult Detect(ProfileContext context)
    {
        List<RecipeCard> cards = [];
        foreach (string pst in DetectorWalk.EnumerateFiles(
                     context.SafeFs,
                     Path.Combine(context.OldProfileRoot, "Documents"),
                     6))
        {
            if (!Path.GetExtension(pst).Equals(".pst", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

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
        }

        return new DetectResult(cards, []);
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

    public RecipeVerifyResult Verify(PlanResult plan) =>
        DetectorWalk.FilesPresent(plan, "Outlook files present", "Outlook destination missing");

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
}
