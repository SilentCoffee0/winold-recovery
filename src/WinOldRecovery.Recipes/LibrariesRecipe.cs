using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.Recipes;

namespace WinOldRecovery.Recipes;

public sealed class LibrariesRecipe : IRecipe
{
    public string Id => "libraries";

    public DetectResult Detect(ProfileContext context)
    {
        List<RecipeCard> cards = [];
        List<(string RelativePath, string Kind, string Detail)> badges = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string source, string kind, string title, string marker) in FindLibraries(context))
        {
            if (!seen.Add(source))
            {
                continue;
            }

            string relative = DetectorWalk.RelativeUnder(context.OldProfileRoot, source);
            if (string.IsNullOrWhiteSpace(relative) ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                Path.IsPathRooted(relative))
            {
                continue;
            }

            cards.Add(
                new RecipeCard(
                    Id,
                    title,
                    "A personal library: notes, books, or a citation database that lives as a folder plus a catalog file.",
                    "The catalog is often the only local copy after a reinstall.",
                    "The whole library folder, including the catalog database.",
                    "Zotero/Calibre/Joplin/Logseq sync, if you used one.",
                    "Caches regenerate. The library does not.",
                    "You keep an empty app and lose the items that lived only here.",
                    [
                        new RecipeComponent("library", "Library folder", Path.GetFileName(source), Decision.Restore, false, null, false),
                    ],
                    context.ProfileName + ":" + kind + ":" + relative.Replace('\\', '/'),
                    new Dictionary<string, string>
                    {
                        ["source"] = source,
                        ["relative"] = relative,
                        ["kind"] = kind,
                        ["marker"] = marker,
                    }));
            DetectorWalk.AddTreeBadge(badges, context.OldProfileRoot, source, "Library", Path.GetFileName(source));
        }

        return new DetectResult(cards, badges);
    }

    public PlanResult Plan(CardDecisions decisions, DestinationContext destination)
    {
        if (!RecipeDecisions.ShouldRestore(decisions, "library"))
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
        AnkiRecipe.AddTree(destination.SafeFs, source, dest, source, "library", writes, static _ => false);
        return new PlanResult(decisions.Card, writes);
    }

    public Task ExecuteAsync(PlanResult plan, IRecipeJournal journal, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public RecipeVerifyResult Verify(PlanResult plan)
    {
        RecipeVerifyResult present = DetectorWalk.FilesPresentMatchingSourceLength(
            plan,
            "Library present",
            "Library destination missing",
            "Library destination size does not match the source");
        if (!present.Ok || plan.Writes.Count == 0)
        {
            return present;
        }

        string marker = plan.Card.Facts.GetValueOrDefault("marker") ?? string.Empty;
        return DestinationHasMarker(plan, marker)
            ? present
            : new RecipeVerifyResult(false, "Library destination missing " + marker);
    }

    public IReadOnlyList<Prerequisite> Prerequisites(PlanResult plan)
    {
        string kind = plan.Card.Facts.GetValueOrDefault("kind") ?? string.Empty;
        return kind switch
        {
            "zotero" => [new Prerequisite("zotero", "Close Zotero before restoring the library.")],
            "calibre" => [new Prerequisite("calibre", "Close Calibre before restoring the library.")],
            "joplin" => [new Prerequisite("Joplin", "Close Joplin before restoring notes.")],
            "logseq" => [new Prerequisite("Logseq", "Close Logseq before restoring a graph.")],
            _ => [],
        };
    }

    private static bool DestinationHasMarker(PlanResult plan, string marker)
    {
        if (string.IsNullOrWhiteSpace(marker))
        {
            return true;
        }

        foreach (RecipeWrite write in plan.Writes)
        {
            if (Path.GetFileName(write.DestinationPath).Equals(marker, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(write.DestinationPath))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<(string Source, string Kind, string Title, string Marker)> FindLibraries(
        ProfileContext context)
    {
        foreach (string source in KnownFolders(context.OldProfileRoot))
        {
            if (TryClassify(context, source, out string kind, out string title, out string marker))
            {
                yield return (source, kind, title, marker);
            }
        }

        if (context.Index is { } index)
        {
            foreach (string parent in index.ParentsOfChildNamed("zotero.sqlite"))
            {
                if (TryClassify(context, parent, out string kind, out string title, out string marker))
                {
                    yield return (parent, kind, title, marker);
                }
            }

            foreach (string parent in index.ParentsOfChildNamed("metadata.db"))
            {
                if (TryClassify(context, parent, out string kind, out string title, out string marker) &&
                    kind == "calibre")
                {
                    yield return (parent, kind, title, marker);
                }
            }

            foreach (string parent in index.ParentsOfChildNamed("database.sqlite"))
            {
                if (TryClassify(context, parent, out string kind, out string title, out string marker) &&
                    kind == "joplin")
                {
                    yield return (parent, kind, title, marker);
                }
            }

            foreach (string logseqDir in index.ParentsOfChildNamed("config.edn"))
            {
                if (!Path.GetFileName(logseqDir).Equals("logseq", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? graph = Path.GetDirectoryName(logseqDir);
                if (graph is not null &&
                    TryClassify(context, graph, out string kind, out string title, out string marker))
                {
                    yield return (graph, kind, title, marker);
                }
            }

            yield break;
        }

        foreach (string graph in FindLogseqGraphs(context))
        {
            yield return (graph, "logseq", "Logseq graph — " + Path.GetFileName(graph), "config.edn");
        }
    }

    private static IEnumerable<string> KnownFolders(string oldProfileRoot)
    {
        yield return Path.Combine(oldProfileRoot, "Zotero");
        yield return Path.Combine(oldProfileRoot, "Calibre Library");
        yield return Path.Combine(oldProfileRoot, "Documents", "Zotero");
        yield return Path.Combine(oldProfileRoot, "Documents", "Calibre Library");
        yield return Path.Combine(oldProfileRoot, "AppData", "Roaming", "Joplin");
        yield return Path.Combine(oldProfileRoot, "AppData", "Roaming", "logseq");
    }

    private static bool TryClassify(
        ProfileContext context,
        string source,
        out string kind,
        out string title,
        out string marker)
    {
        kind = string.Empty;
        title = string.Empty;
        marker = string.Empty;
        if (!context.SafeFs.DirectoryExists(source) || DetectorWalk.IsReparse(source))
        {
            return false;
        }

        string name = Path.GetFileName(source);
        if (context.SafeFs.FileExists(Path.Combine(source, "zotero.sqlite")))
        {
            kind = "zotero";
            title = "Zotero library";
            marker = "zotero.sqlite";
            return true;
        }

        if (context.SafeFs.FileExists(Path.Combine(source, "metadata.db")) &&
            name.Contains("Calibre", StringComparison.OrdinalIgnoreCase))
        {
            kind = "calibre";
            title = "Calibre Library";
            marker = "metadata.db";
            return true;
        }

        if (context.SafeFs.FileExists(Path.Combine(source, "database.sqlite")) &&
            name.Equals("Joplin", StringComparison.OrdinalIgnoreCase))
        {
            kind = "joplin";
            title = "Joplin notes";
            marker = "database.sqlite";
            return true;
        }

        string logseqConfig = Path.Combine(source, "logseq", "config.edn");
        if (context.SafeFs.FileExists(logseqConfig) ||
            (name.Equals("logseq", StringComparison.OrdinalIgnoreCase) &&
             context.SafeFs.FileExists(Path.Combine(source, "config.edn"))))
        {
            kind = "logseq";
            title = "Logseq graph — " + name;
            marker = "config.edn";
            return true;
        }

        return false;
    }

    private static IEnumerable<string> FindLogseqGraphs(ProfileContext context)
    {
        string[] roots =
        [
            Path.Combine(context.OldProfileRoot, "Documents"),
            Path.Combine(context.OldProfileRoot, "Desktop"),
            context.OldProfileRoot,
        ];
        foreach (string root in roots)
        {
            if (!context.SafeFs.DirectoryExists(root) || DetectorWalk.IsReparse(root))
            {
                continue;
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

                List<string> children = [];
                foreach (string entry in entries)
                {
                    if (DetectorWalk.IsReparse(entry) || !context.SafeFs.DirectoryExists(entry))
                    {
                        continue;
                    }

                    string name = Path.GetFileName(entry);
                    if (name.Equals("logseq", StringComparison.OrdinalIgnoreCase) &&
                        context.SafeFs.FileExists(Path.Combine(entry, "config.edn")))
                    {
                        yield return directory;
                        children.Clear();
                        break;
                    }

                    children.Add(entry);
                }

                if (depth >= 4)
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
}
