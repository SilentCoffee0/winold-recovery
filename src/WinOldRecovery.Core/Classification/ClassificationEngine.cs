using WinOldRecovery.Core.Browse;
using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Scan;

namespace WinOldRecovery.Core.Classification;

public sealed class ClassificationEngine
{
    public const int HeaderBytes = 4096;

    private readonly SessionDb sessionDb;
    private readonly SafeFs safeFs;
    private readonly IReadOnlyList<ClassificationRule> rules;

    public ClassificationEngine(
        SessionDb sessionDb,
        SafeFs safeFs,
        IReadOnlyList<ClassificationRule>? rules = null)
    {
        this.sessionDb = sessionDb ?? throw new ArgumentNullException(nameof(sessionDb));
        this.safeFs = safeFs ?? throw new ArgumentNullException(nameof(safeFs));
        this.rules = rules ?? ClassificationRuleCatalog.LoadEmbedded();
    }

    public IReadOnlyList<ClassificationRule> Rules => rules;

    public async Task<ClassificationSummary> ClassifyAsync(
        string sessionId,
        string sourceRoot,
        IReadOnlyList<DetectedProfile> profiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentNullException.ThrowIfNull(profiles);

        IReadOnlyList<ClassificationNodeRow> nodes = sessionDb.ListClassificationNodes(sessionId);
        Dictionary<long, List<string>> siblings = new();
        foreach (ClassificationNodeRow node in nodes)
        {
            if (node.ParentId is not long parentId)
            {
                continue;
            }

            if (!siblings.TryGetValue(parentId, out List<string>? names))
            {
                names = [];
                siblings[parentId] = names;
            }

            names.Add(node.Name);
        }

        List<NodeBadgeRow> badges = [];
        HashSet<long> sensitive = [];
        Dictionary<string, (int Count, long Bytes, ClassificationRule Rule)> hits = new(StringComparer.Ordinal);
        Dictionary<long, Decision> suggested = [];

        foreach (ClassificationNodeRow node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (ClassificationRule rule in rules)
            {
                if (!Matches(node, rule, siblings, sourceRoot))
                {
                    continue;
                }

                badges.Add(new NodeBadgeRow(node.Id, rule.Kind.ToString(), rule.Badge));
                if (rule.Sensitive)
                {
                    sensitive.Add(node.Id);
                }

                if (!hits.TryGetValue(rule.Id, out (int Count, long Bytes, ClassificationRule Rule) hit))
                {
                    hit = (0, 0, rule);
                }

                hits[rule.Id] = (hit.Count + 1, hit.Bytes + node.AggSize, rule);

                if (rule.Kind == ClassificationKind.Regeneratable)
                {
                    continue;
                }

                if (rule.SuggestedDefault is Decision.Restore)
                {
                    suggested[node.Id] = Decision.Restore;
                }
            }
        }

        DecisionEngine decisions = new(sessionDb, sessionId);
        NodeBrowser browser = new(sessionDb, sessionId);
        foreach (DetectedProfile profile in profiles)
        {
            foreach (StandardFolderMatch folder in profile.StandardFolders.Where(static item => item.PresentInSource))
            {
                if (folder.RelativePathInSource is null)
                {
                    continue;
                }

                TreeNodeRow? node = browser.FindByRelPath(folder.RelativePathInSource);
                if (node is null)
                {
                    continue;
                }

                if (SuggestedDefaultTable.IsRestoreStandardFolder(folder.KnownName))
                {
                    suggested[node.Id] = Decision.Restore;
                }
            }

            string appDataRel = Path.Combine("Users", profile.Name, "AppData");
            TreeNodeRow? appData = browser.FindByRelPath(appDataRel);
            if (appData is not null)
            {
                suggested[appData.Id] = Decision.LeaveBehind;
            }
        }

        await sessionDb.InsertBadgesAsync(badges, cancellationToken).ConfigureAwait(false);
        await sessionDb.MarkNodesSensitiveAsync(sensitive.ToArray(), cancellationToken).ConfigureAwait(false);

        foreach ((long nodeId, Decision decision) in suggested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await decisions.SetSuggestedDefaultAsync(nodeId, decision, cancellationToken)
                .ConfigureAwait(false);
        }

        int highCount = 0;
        long highBytes = 0;
        int regenCount = 0;
        long regenBytes = 0;
        List<RuleHitCount> breakdown = [];
        foreach ((string id, (int count, long bytes, ClassificationRule rule)) in hits.OrderBy(static pair => pair.Key))
        {
            breakdown.Add(new RuleHitCount(id, rule.Badge, rule.Kind, count, bytes));
            if (rule.Kind is ClassificationKind.HighValue or ClassificationKind.GameSave)
            {
                highCount += count;
                highBytes += bytes;
            }
            else if (rule.Kind == ClassificationKind.Regeneratable)
            {
                regenCount += count;
                regenBytes += bytes;
            }
        }

        return new ClassificationSummary(highCount, highBytes, regenCount, regenBytes, breakdown);
    }

    private bool Matches(
        ClassificationNodeRow node,
        ClassificationRule rule,
        IReadOnlyDictionary<long, List<string>> siblings,
        string sourceRoot)
    {
        if (rule.MinSize is { } min && node.Size < min)
        {
            return false;
        }

        bool anyConstraint =
            rule.NameGlobs.Count > 0 ||
            rule.PathGlobs.Count > 0 ||
            rule.DirectoryNames.Count > 0 ||
            rule.PathContains.Count > 0;
        if (!anyConstraint)
        {
            return false;
        }

        if (rule.NameGlobs.Count > 0 &&
            !rule.NameGlobs.Any(glob => RuleGlob.MatchesName(node.Name, glob)))
        {
            return false;
        }

        if (rule.PathGlobs.Count > 0 &&
            !rule.PathGlobs.Any(glob => RuleGlob.MatchesPath(node.RelPath, glob)))
        {
            return false;
        }

        if (rule.DirectoryNames.Count > 0 &&
            (node.Kind != NodeKind.Directory ||
                !rule.DirectoryNames.Contains(node.Name, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (rule.PathContains.Count > 0 &&
            !rule.PathContains.Any(fragment => RuleGlob.ContainsSegment(node.RelPath, fragment)))
        {
            return false;
        }

        if (rule.SiblingGlobs.Count > 0)
        {
            if (node.ParentId is not long parentId ||
                !siblings.TryGetValue(parentId, out List<string>? names) ||
                !rule.SiblingGlobs.Any(glob => names.Any(name => RuleGlob.MatchesName(name, glob))))
            {
                return false;
            }
        }

        if (rule.ChildGlobs.Count > 0)
        {
            if (!siblings.TryGetValue(node.Id, out List<string>? children) ||
                !rule.ChildGlobs.Any(glob => children.Any(name => RuleGlob.MatchesName(name, glob))))
            {
                return false;
            }
        }

        if (rule.HeaderHex is { Length: > 0 } header)
        {
            if (rule.Sensitive || node.Problem != NodeProblem.None || node.Kind != NodeKind.File)
            {
                return false;
            }

            if (!HeaderMatches(Path.Combine(sourceRoot, node.RelPath), header))
            {
                return false;
            }
        }

        return true;
    }

    private bool HeaderMatches(string path, string headerHex)
    {
        byte[] expected = Convert.FromHexString(headerHex);
        try
        {
            using FileStream stream = safeFs.OpenRead(path);
            byte[] buffer = new byte[Math.Min(HeaderBytes, expected.Length)];
            int read = stream.Read(buffer);
            return read >= expected.Length && buffer.AsSpan(0, expected.Length).SequenceEqual(expected);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
