using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Processes;
using WinOldRecovery.Core.Recipes;

namespace WinOldRecovery.Recipes;

public sealed class GpgRecipe : IRecipe
{
    public string Id => "gpg";

    public DetectResult Detect(ProfileContext context)
    {
        List<RecipeCard> cards = [];
        List<(string RelativePath, string Kind, string Detail)> badges = [];
        foreach (string home in CandidateHomes(context))
        {
            if (!context.SafeFs.DirectoryExists(home))
            {
                continue;
            }

            bool hasKeys = context.SafeFs.DirectoryExists(Path.Combine(home, "private-keys-v1.d")) ||
                context.SafeFs.FileExists(Path.Combine(home, "pubring.kbx")) ||
                context.SafeFs.FileExists(Path.Combine(home, "secring.gpg"));
            if (!hasKeys)
            {
                continue;
            }

            bool destExists = context.SafeFs.DirectoryExists(
                Path.Combine(context.DestinationProfileRoot, "AppData", "Roaming", "gnupg"));
            cards.Add(
                new RecipeCard(
                    Id,
                    "GPG keyring (" + context.ProfileName + ")",
                    "OpenPGP keys used to sign and decrypt. Passphrase-protected keys stay protected.",
                    "Without this ring you cannot decrypt old mail or verify your previous signatures.",
                    destExists
                        ? "The gnupg folder except random_seed, sockets, locks, and crls.d. Destination already has a ring: restore as gnupg.from-windows-old, then gpg --import."
                        : "The gnupg folder except random_seed, sockets, locks, and crls.d. If a destination ring exists, the old one is written as gnupg.from-windows-old.",
                    "Generate new keys and re-share them.",
                    "random_seed regenerates.",
                    "You lose keys that were never backed up elsewhere.",
                    [
                        new RecipeComponent("ring", "GPG home", home, Decision.Restore, false, null, true),
                    ],
                    context.ProfileName + ":" + home,
                    new Dictionary<string, string>
                    {
                        ["source"] = home,
                        ["mergeHint"] = destExists ? "gpg --import" : string.Empty,
                    }));
            DetectorWalk.AddTreeBadge(badges, context.OldProfileRoot, home, "GPG", "keyring");
        }

        return new DetectResult(cards, badges);
    }

    public PlanResult Plan(CardDecisions decisions, DestinationContext destination)
    {
        if (!RecipeDecisions.ShouldRestore(decisions, "ring"))
        {
            return new PlanResult(decisions.Card, []);
        }

        string source = decisions.Card.Facts["source"];
        string dest = Path.Combine(destination.DestinationProfileRoot, "AppData", "Roaming", "gnupg");
        if (destination.SafeFs.DirectoryExists(dest))
        {
            dest = Path.Combine(destination.DestinationProfileRoot, "AppData", "Roaming", "gnupg.from-windows-old");
        }

        List<RecipeWrite> writes = [];
        AnkiRecipe.AddTree(destination.SafeFs, source, dest, source, "ring", writes, Skip);
        return new PlanResult(decisions.Card, writes);
    }

    public Task ExecuteAsync(PlanResult plan, IRecipeJournal journal, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public RecipeVerifyResult Verify(PlanResult plan)
    {
        if (plan.Writes.Any(static write =>
                Path.GetFileName(write.DestinationPath).Equals("random_seed", StringComparison.OrdinalIgnoreCase)))
        {
            return new RecipeVerifyResult(false, "random_seed must not be restored");
        }

        return DetectorWalk.FilesPresentMatchingSourceLength(
            plan,
            "GPG files present",
            "GPG destination missing",
            "GPG destination size does not match the source");
    }

    public async Task<RecipeVerifyResult> VerifyAsync(
        PlanResult plan,
        CancellationToken cancellationToken = default)
    {
        RecipeVerifyResult files = Verify(plan);
        if (!files.Ok || plan.Destination is null)
        {
            return files;
        }

        string? home = DestinationHome(plan);
        Dictionary<string, string?> environment = [];
        if (!string.IsNullOrEmpty(home))
        {
            environment["GNUPGHOME"] = home;
        }

        ProcessResult result;
        try
        {
            result = await plan.Destination.ProcessRunner
                .RunAsync(
                    new ProcessRequest(
                        "gpg.exe",
                        ["--list-secret-keys"],
                        Environment: environment.Count == 0 ? null : environment,
                        Timeout: TimeSpan.FromSeconds(30)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not StackOverflowException)
        {
            return files;
        }

        if (result.ExitCode != 0)
        {
            return new RecipeVerifyResult(false, "gpg --list-secret-keys failed");
        }

        return new RecipeVerifyResult(true, "gpg --list-secret-keys succeeded");
    }

    public IReadOnlyList<Prerequisite> Prerequisites(PlanResult plan) =>
        [new Prerequisite("gpg-agent", "Stop gpg-agent (gpgconf --kill all) before restoring the keyring.")];

    private static string? DestinationHome(PlanResult plan)
    {
        foreach (RecipeWrite write in plan.Writes)
        {
            string? current = Path.GetDirectoryName(write.DestinationPath);
            while (!string.IsNullOrEmpty(current))
            {
                string name = Path.GetFileName(current);
                if (name.Equals("gnupg", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("gnupg.from-windows-old", StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }

                current = Path.GetDirectoryName(current);
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateHomes(ProfileContext context)
    {
        yield return Path.Combine(context.OldProfileRoot, "AppData", "Roaming", "gnupg");
        yield return Path.Combine(context.OldProfileRoot, ".gnupg");
    }

    private static bool Skip(string relative)
    {
        string name = Path.GetFileName(relative);
        return name.Equals("random_seed", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("S.", StringComparison.Ordinal) ||
            relative.Contains("crls.d", StringComparison.OrdinalIgnoreCase);
    }
}
