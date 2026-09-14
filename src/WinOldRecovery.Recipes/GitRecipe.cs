using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Recipes;

namespace WinOldRecovery.Recipes;

public sealed class GitRecipe : IRecipe
{
    public string Id => "git";

    public DetectResult Detect(ProfileContext context)
    {
        List<RecipeCard> cards = [];
        List<(string RelativePath, string Kind, string Detail)> badges = [];
        string gitconfig = Path.Combine(context.OldProfileRoot, ".gitconfig");
        if (!context.SafeFs.FileExists(gitconfig))
        {
            gitconfig = Path.Combine(context.OldProfileRoot, ".config", "git", "config");
        }

        if (context.SafeFs.FileExists(gitconfig))
        {
            string text = context.SafeFs.ReadAllText(gitconfig);
            cards.Add(ConfigCard(context, gitconfig, Scrub(text)));
        }

        string cred = Path.Combine(context.OldProfileRoot, ".git-credentials");
        if (cards.Count > 0 && context.SafeFs.FileExists(cred))
        {
            RecipeCard config = cards[0];
            cards[0] = config with
            {
                Facts = new Dictionary<string, string>(config.Facts) { ["credentials"] = cred },
            };
        }

        List<string> trees = DetectorWalk.EnumerateGitWorkingTrees(context.SafeFs, context.OldProfileRoot, 8)
            .ToList();
        foreach (string entry in DetectorWalk.OutermostDirectories(trees))
        {
            string? gitDir = ResolveGitDir(context.SafeFs, entry);
            string headPath = gitDir is null ? Path.Combine(entry, ".git", "HEAD") : Path.Combine(gitDir, "HEAD");
            string head = context.SafeFs.FileExists(headPath)
                ? context.SafeFs.ReadAllText(headPath).Trim()
                : "unknown";
            string relative = DetectorWalk.RelativeUnder(context.OldProfileRoot, entry);
            bool hasRemote = gitDir is not null && HasRemote(context.SafeFs, gitDir);
            string risk = hasRemote ? "Git: local-only work" : "Git: no remote";
            badges.Add((relative, "Git", risk));
            cards.Add(
                new RecipeCard(
                    Id,
                    "Git repository — " + Path.GetFileName(entry),
                    "A Git repository found in the old profile.",
                    "Unpushed commits and uncommitted work cannot be cloned from a remote.",
                    "The whole working tree including .git. Offline analysis cannot see uncommitted changes until Git is installed.",
                    "Re-clone if a remote exists and there is no local-only work.",
                    "node_modules and build folders may regenerate.",
                    "Local-only work is gone.",
                    [
                        new RecipeComponent(
                            "repo",
                            "Restore repository",
                            head,
                            Decision.Restore,
                            false,
                            null,
                            false),
                    ],
                    context.ProfileName + ":" + relative,
                    new Dictionary<string, string>
                    {
                        ["source"] = entry,
                        ["head"] = head,
                        ["kind"] = "repo",
                        ["risk"] = risk,
                    }));
        }

        return new DetectResult(cards, badges);
    }

    public PlanResult Plan(CardDecisions decisions, DestinationContext destination)
    {
        if (decisions.Card.Facts.GetValueOrDefault("kind") == "repo")
        {
            if (!RecipeDecisions.ShouldRestore(decisions, "repo"))
            {
                return new PlanResult(decisions.Card, []);
            }

            string source = decisions.Card.Facts["source"];
            string dest = Path.Combine(destination.DestinationProfileRoot, "Recovered", Path.GetFileName(source));
            return new PlanResult(
                decisions.Card,
                [new RecipeWrite(RecipeWriteKind.CopyTree, source, dest, null, 1, "repo")]);
        }

        List<RecipeWrite> writes = [];
        if (RecipeDecisions.ShouldRestore(decisions, "config"))
        {
            string destConfig = Path.Combine(destination.DestinationProfileRoot, ".gitconfig");
            if (destination.SafeFs.FileExists(destConfig))
            {
                destConfig = Path.Combine(destination.DestinationProfileRoot, ".gitconfig.from-windows-old");
            }

            string content = decisions.Card.Facts.GetValueOrDefault("preview") ?? string.Empty;
            writes.Add(
                new RecipeWrite(
                    RecipeWriteKind.WriteContent,
                    null,
                    destConfig,
                    content,
                    content.Length,
                    "config"));
        }

        if (RecipeDecisions.ShouldRestore(decisions, "git-credentials") &&
            decisions.Card.Facts.TryGetValue("credentials", out string? credPath))
        {
            string destCred = Path.Combine(destination.DestinationProfileRoot, ".git-credentials");
            if (destination.SafeFs.FileExists(destCred))
            {
                destCred += ".from-windows-old";
            }

            writes.Add(new RecipeWrite(RecipeWriteKind.CopyFile, credPath, destCred, null, 1, "git-credentials"));
        }

        return new PlanResult(decisions.Card, writes);
    }

    public Task ExecuteAsync(PlanResult plan, IRecipeJournal journal, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public RecipeVerifyResult Verify(PlanResult plan)
    {
        bool ok = plan.Writes.All(static write =>
            File.Exists(write.DestinationPath) || Directory.Exists(write.DestinationPath));
        return new RecipeVerifyResult(ok, ok ? "Git files present" : "Git restore missing");
    }

    public IReadOnlyList<Prerequisite> Prerequisites(PlanResult plan) => [];

    public static string Scrub(string text)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        bool credentialSection = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                credentialSection = trimmed.Equals("[credential]", StringComparison.OrdinalIgnoreCase);
            }

            if (trimmed.StartsWith("token", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("password", StringComparison.OrdinalIgnoreCase) ||
                credentialSection && trimmed.Contains('=', StringComparison.Ordinal))
            {
                int equals = lines[i].IndexOf('=', StringComparison.Ordinal);
                lines[i] = equals < 0 ? "    helper = ***" : lines[i][..equals] + "= ***";
            }
        }

        return string.Join('\n', lines);
    }

    private static RecipeCard ConfigCard(ProfileContext context, string path, string scrubbed)
    {
        return new RecipeCard(
            "git",
            "Git configuration (" + context.ProfileName + ")",
            "Your Git user name, email and local settings.",
            "Without this, new clones do not know who you are.",
            "Copy of .gitconfig with credential helper values hidden. Existing files are kept; the old copy is named .gitconfig.from-windows-old.",
            "Re-enter your name and sign in to Git Credential Manager again.",
            "A new .gitconfig can be written by hand.",
            "You keep typing your name on the first commit.",
            [
                new RecipeComponent("config", "Git config", "user settings", Decision.Restore, false, null, false),
                new RecipeComponent(
                    "git-credentials",
                    ".git-credentials",
                    "Plaintext secrets if present",
                    Decision.Undecided,
                    false,
                    null,
                    true),
            ],
            context.ProfileName,
            new Dictionary<string, string>
            {
                ["source"] = path,
                ["preview"] = scrubbed,
                ["kind"] = "config",
            });
    }

    private static string? ResolveGitDir(SafeFs safeFs, string workTree)
    {
        string git = Path.Combine(workTree, ".git");
        if (safeFs.DirectoryExists(git))
        {
            return git;
        }

        if (!safeFs.FileExists(git))
        {
            return null;
        }

        foreach (string line in safeFs.ReadAllText(git).Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string target = trimmed["gitdir:".Length..].Trim();
            return Path.IsPathRooted(target)
                ? target
                : Path.GetFullPath(Path.Combine(workTree, target));
        }

        return null;
    }

    private static bool HasRemote(SafeFs safeFs, string gitDir)
    {
        string config = Path.Combine(gitDir, "config");
        if (!safeFs.FileExists(config))
        {
            return false;
        }

        return safeFs.ReadAllText(config).Contains("[remote ", StringComparison.OrdinalIgnoreCase);
    }
}
