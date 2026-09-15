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
            cards.Add(ConfigCard(context, gitconfig, Scrub(text), GitConfigFacts.Read(text)));
        }

        string cred = Path.Combine(context.OldProfileRoot, ".git-credentials");
        if (cards.Count > 0 && context.SafeFs.FileExists(cred))
        {
            RecipeCard config = cards[0];
            Dictionary<string, string> facts = new(config.Facts) { ["credentials"] = cred };
            AttachOptionalConfigFiles(context, facts);
            cards[0] = config with { Facts = facts };
        }
        else if (cards.Count > 0)
        {
            RecipeCard config = cards[0];
            Dictionary<string, string> facts = new(config.Facts);
            AttachOptionalConfigFiles(context, facts);
            cards[0] = config with { Facts = facts };
        }

        List<string> trees = DetectorWalk.EnumerateGitWorkingTrees(context.SafeFs, context.OldProfileRoot, 8)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<string> analysis = [];
        List<string> vendored = [];
        foreach (string tree in trees)
        {
            string relative = DetectorWalk.RelativeUnder(context.OldProfileRoot, tree);
            if (DetectorWalk.IsVendoredGitPath(relative))
            {
                vendored.Add(tree);
            }
            else
            {
                analysis.Add(tree);
            }
        }

        foreach (string entry in DetectorWalk.OutermostDirectories(analysis))
        {
            AddRepositoryCard(context, cards, badges, entry, vendored: false);
        }

        foreach (string entry in vendored)
        {
            AddRepositoryCard(context, cards, badges, entry, vendored: true);
        }

        return new DetectResult(cards, badges);
    }

    private static void AddRepositoryCard(
        ProfileContext context,
        List<RecipeCard> cards,
        List<(string RelativePath, string Kind, string Detail)> badges,
        string entry,
        bool vendored)
    {
        string relative = DetectorWalk.RelativeUnder(context.OldProfileRoot, entry);
        string badge;
        GitOfflineResult? offline = null;
        if (vendored)
        {
            badge = "Git: vendored";
        }
        else
        {
            offline = GitOffline.Analyze(context.SafeFs, ResolveGitDir(context.SafeFs, entry));
            badge = offline.Badge;
        }

        badges.Add((relative, "Git", badge));
        Dictionary<string, string> facts = new(StringComparer.Ordinal)
        {
            ["source"] = entry,
            ["head"] = offline is null || string.IsNullOrEmpty(offline.Head) ? "unknown" : offline.Head,
            ["kind"] = "repo",
            ["risk"] = badge,
            ["branch"] = offline?.CurrentBranch ?? string.Empty,
            ["branches"] = offline is null ? string.Empty : string.Join(", ", offline.Branches),
            ["remotes"] = offline is null
                ? string.Empty
                : string.Join("; ", offline.Remotes.Select(static remote => remote.Name + "=" + remote.Url)),
            ["stash"] = offline?.Stash == true ? "1" : "0",
            ["localOnly"] = offline?.LocalOnlyBranch == true ? "1" : "0",
            ["uncommitted"] = GitOffline.UnknownUntilGit,
            ["unpushed"] = GitOffline.UnknownUntilGit,
        };
        if (vendored)
        {
            facts["vendored"] = "1";
        }
        else if (offline is not null)
        {
            if (offline.Reftable)
            {
                facts["reftable"] = "1";
            }

            if (offline.LastActivity is DateTimeOffset activity)
            {
                facts["lastActivity"] = activity.ToString("O");
            }

            if (offline.IndexMtime is DateTimeOffset indexMtime)
            {
                facts["indexMtime"] = indexMtime.ToString("O");
            }
        }

        cards.Add(
            new RecipeCard(
                "git",
                "Git repository — " + Path.GetFileName(entry) + (vendored ? " (vendored)" : string.Empty),
                vendored
                    ? "A Git repository inside node_modules, cache, or another regeneratable folder."
                    : "A Git repository found in the old profile.",
                vendored
                    ? "Package installs recreate this tree. Status is not analyzed."
                    : "Unpushed commits and uncommitted work cannot be cloned from a remote.",
                "The whole working tree including .git. Offline analysis cannot see uncommitted or unpushed work until Git is installed.",
                vendored
                    ? "Reinstall the package or re-clone if you still need it."
                    : "Re-clone if a remote exists and there is no local-only work.",
                "node_modules and build folders may regenerate.",
                vendored ? "You can reinstall the package later." : "Local-only work is gone.",
                [
                    new RecipeComponent(
                        "repo",
                        "Restore repository",
                        badge,
                        vendored ? Decision.LeaveBehind : Decision.Restore,
                        false,
                        null,
                        false),
                ],
                context.ProfileName + ":" + relative,
                facts));
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

        if (RecipeDecisions.ShouldRestore(decisions, "config"))
        {
            CopySidecar(
                decisions.Card,
                destination,
                "ignore",
                Path.Combine(destination.DestinationProfileRoot, ".config", "git", "ignore"),
                writes);
            CopySidecar(
                decisions.Card,
                destination,
                "gitignore_global",
                Path.Combine(destination.DestinationProfileRoot, ".gitignore_global"),
                writes);
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
        if (!ok)
        {
            return new RecipeVerifyResult(false, "Git restore missing");
        }

        if (!HeadsMatch(plan))
        {
            return new RecipeVerifyResult(false, "HEAD does not match the source");
        }

        return new RecipeVerifyResult(true, "Git files present");
    }

    public async Task<RecipeVerifyResult> VerifyAsync(
        PlanResult plan,
        CancellationToken cancellationToken = default)
    {
        RecipeVerifyResult files = Verify(plan);
        if (!files.Ok)
        {
            return files;
        }

        RecipeWrite? repo = RepoWrite(plan);
        if (repo?.SourcePath is null || plan.Destination is null)
        {
            return files;
        }

        GitLevel3Result git = await GitAnalyze.CompareRestoredAsync(
                plan.Destination.ProcessRunner,
                repo.SourcePath,
                repo.DestinationPath,
                plan.Destination.DestinationProfileRoot,
                plan.Destination.SafeFs,
                plan.Destination.SessionExportsDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        if (!git.GitAvailable)
        {
            return files;
        }

        if (!git.Ok)
        {
            return new RecipeVerifyResult(false, "git HEAD or status does not match the source");
        }

        return new RecipeVerifyResult(true, "Git HEAD and status match the source");
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
                credentialSection = trimmed.StartsWith("[credential", StringComparison.OrdinalIgnoreCase);
                if (trimmed.Contains('@'))
                {
                    lines[i] = "[url \"***\"]";
                }

                continue;
            }

            bool secretKey = trimmed.StartsWith("token", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("password", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("username", StringComparison.OrdinalIgnoreCase);
            bool insteadOfSecret = trimmed.Contains("insteadof", StringComparison.OrdinalIgnoreCase) &&
                trimmed.Contains('@');
            if (secretKey || insteadOfSecret || (credentialSection && secretKey))
            {
                int equals = lines[i].IndexOf('=', StringComparison.Ordinal);
                lines[i] = equals < 0 ? "    *** = ***" : lines[i][..equals] + "= ***";
            }
        }

        return string.Join('\n', lines);
    }

    private static void AttachOptionalConfigFiles(ProfileContext context, Dictionary<string, string> facts)
    {
        string ignore = Path.Combine(context.OldProfileRoot, ".config", "git", "ignore");
        if (context.SafeFs.FileExists(ignore))
        {
            facts["ignore"] = ignore;
        }

        string globalIgnore = Path.Combine(context.OldProfileRoot, ".gitignore_global");
        if (context.SafeFs.FileExists(globalIgnore))
        {
            facts["gitignore_global"] = globalIgnore;
        }
    }

    private static void CopySidecar(
        RecipeCard card,
        DestinationContext destination,
        string factKey,
        string destinationPath,
        List<RecipeWrite> writes)
    {
        if (!card.Facts.TryGetValue(factKey, out string? source) || string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        DetectorWalk.CopyFileKeepBoth(destination.SafeFs, source, destinationPath, "config", writes);
    }

    private static RecipeCard ConfigCard(
        ProfileContext context,
        string path,
        string scrubbed,
        IReadOnlyDictionary<string, string> extracted)
    {
        Dictionary<string, string> facts = new(StringComparer.Ordinal)
        {
            ["source"] = path,
            ["preview"] = scrubbed,
            ["kind"] = "config",
        };
        foreach ((string key, string value) in extracted)
        {
            facts[key] = value;
        }

        return new RecipeCard(
            "git",
            "Git configuration (" + context.ProfileName + ")",
            "Your Git user name, email and local settings.",
            "Without this, new clones do not know who you are.",
            "Copy of .gitconfig with credential secrets hidden. Existing files are kept; the old copy is named .gitconfig.from-windows-old.",
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
            facts);
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

    private static RecipeWrite? RepoWrite(PlanResult plan)
    {
        return plan.Writes.FirstOrDefault(static write =>
            write.Kind == RecipeWriteKind.CopyTree && write.SourcePath is not null);
    }

    private static bool HeadsMatch(PlanResult plan)
    {
        foreach (RecipeWrite write in plan.Writes)
        {
            if (write.Kind != RecipeWriteKind.CopyTree || write.SourcePath is null)
            {
                continue;
            }

            string sourceHead = Path.Combine(write.SourcePath, ".git", "HEAD");
            if (!File.Exists(sourceHead))
            {
                continue;
            }

            string destHead = Path.Combine(write.DestinationPath, ".git", "HEAD");
            if (!File.Exists(destHead))
            {
                return false;
            }

            if (!string.Equals(
                    File.ReadAllText(sourceHead).Trim(),
                    File.ReadAllText(destHead).Trim(),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
