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
    private readonly CompiledRule[] compiledRules;

    public ClassificationEngine(
        SessionDb sessionDb,
        SafeFs safeFs,
        IReadOnlyList<ClassificationRule>? rules = null)
    {
        this.sessionDb = sessionDb ?? throw new ArgumentNullException(nameof(sessionDb));
        this.safeFs = safeFs ?? throw new ArgumentNullException(nameof(safeFs));
        this.rules = rules ?? ClassificationRuleCatalog.LoadEmbedded();
        compiledRules = Compile(this.rules);
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

        MatchScratch scratch = new();
        Dictionary<long, IReadOnlyList<string>> childNames = new();
        Dictionary<string, long> nodesByRelPath = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> wantedRelPaths = [];
        foreach (DetectedProfile profile in profiles)
        {
            foreach (StandardFolderMatch folder in profile.StandardFolders)
            {
                if (folder.PresentInSource && folder.RelativePathInSource is not null)
                {
                    wantedRelPaths.Add(folder.RelativePathInSource);
                }
            }

            wantedRelPaths.Add(Path.Combine("Users", profile.Name, "AppData"));
        }

        int index = 0;
        sessionDb.EnumerateClassificationNodes(
            sessionId,
            node =>
            {
                if ((index++ & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (wantedRelPaths.Contains(node.RelPath))
                {
                    nodesByRelPath[node.RelPath] = node.Id;
                }

                MatchNode(
                    node,
                    scratch,
                    id =>
                    {
                        if (!childNames.TryGetValue(id, out IReadOnlyList<string>? names))
                        {
                            names = sessionDb.ListChildNames(sessionId, id);
                            childNames[id] = names;
                        }

                        return names;
                    },
                    sourceRoot);
            });

        foreach (DetectedProfile profile in profiles)
        {
            foreach (StandardFolderMatch folder in profile.StandardFolders)
            {
                if (!folder.PresentInSource || folder.RelativePathInSource is null)
                {
                    continue;
                }

                if (!nodesByRelPath.TryGetValue(folder.RelativePathInSource, out long nodeId))
                {
                    continue;
                }

                if (SuggestedDefaultTable.IsRestoreStandardFolder(folder.KnownName))
                {
                    scratch.Suggested[nodeId] = Decision.Restore;
                }
            }

            string appDataRel = Path.Combine("Users", profile.Name, "AppData");
            if (nodesByRelPath.TryGetValue(appDataRel, out long appDataId))
            {
                scratch.Suggested[appDataId] = Decision.LeaveBehind;
            }
        }

        await sessionDb.InsertBadgesAsync(scratch.Badges, cancellationToken).ConfigureAwait(false);
        if (scratch.Sensitive.Count > 0)
        {
            long[] sensitiveIds = new long[scratch.Sensitive.Count];
            scratch.Sensitive.CopyTo(sensitiveIds);
            await sessionDb.MarkNodesSensitiveAsync(sensitiveIds, cancellationToken).ConfigureAwait(false);
        }

        DecisionEngine decisions = new(sessionDb, sessionId);
        await decisions.SetSuggestedDefaultsAsync(scratch.Suggested, cancellationToken)
            .ConfigureAwait(false);

        int highCount = 0;
        long highBytes = 0;
        int regenCount = 0;
        long regenBytes = 0;
        List<RuleHitCount> breakdown = [];
        foreach ((string id, (int count, long bytes, ClassificationRule rule)) in scratch.Hits.OrderBy(static pair => pair.Key))
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

    internal int CountMatchesForTests(IReadOnlyList<ClassificationNodeRow> nodes, string sourceRoot)
    {
        return MatchAll(nodes, sourceRoot, CancellationToken.None).Badges.Count;
    }

    private MatchScratch MatchAll(
        IReadOnlyList<ClassificationNodeRow> nodes,
        string sourceRoot,
        CancellationToken cancellationToken)
    {
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

        MatchScratch scratch = new();
        for (int index = 0; index < nodes.Count; index++)
        {
            if ((index & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            MatchNode(
                nodes[index],
                scratch,
                id => siblings.TryGetValue(id, out List<string>? names) ? names : null,
                sourceRoot);
        }

        return scratch;
    }

    private void MatchNode(
        ClassificationNodeRow node,
        MatchScratch scratch,
        Func<long, IReadOnlyList<string>?> childrenOf,
        string sourceRoot)
    {
        string relPath = RuleGlob.NormalizeRelPath(node.RelPath);
        for (int ruleIndex = 0; ruleIndex < compiledRules.Length; ruleIndex++)
        {
            CompiledRule compiled = compiledRules[ruleIndex];
            if (!Matches(node, relPath, compiled, childrenOf, sourceRoot))
            {
                continue;
            }

            ClassificationRule rule = compiled.Rule;
            scratch.Badges.Add(new NodeBadgeRow(node.Id, rule.Kind.ToString(), rule.Badge));
            if (rule.Sensitive)
            {
                scratch.Sensitive.Add(node.Id);
            }

            if (!scratch.Hits.TryGetValue(rule.Id, out (int Count, long Bytes, ClassificationRule Rule) hit))
            {
                hit = (0, 0, rule);
            }

            scratch.Hits[rule.Id] = (hit.Count + 1, hit.Bytes + node.AggSize, rule);

            if (rule.Kind == ClassificationKind.Regeneratable)
            {
                continue;
            }

            if (rule.SuggestedDefault is Decision.Restore)
            {
                scratch.Suggested[node.Id] = Decision.Restore;
            }
        }
    }

    private bool Matches(
        ClassificationNodeRow node,
        string relPath,
        CompiledRule rule,
        Func<long, IReadOnlyList<string>?> childrenOf,
        string sourceRoot)
    {
        if (rule.MinSize is { } min && node.Size < min)
        {
            return false;
        }

        if (!rule.HasConstraint)
        {
            return false;
        }

        if (rule.DirectoryNames is not null)
        {
            if (node.Kind != NodeKind.Directory || !rule.DirectoryNames.Contains(node.Name))
            {
                return false;
            }
        }

        if (rule.Names.Any && !rule.Names.Matches(node.Name))
        {
            return false;
        }

        if (rule.PathContains.Length > 0 && !AnyPathContains(relPath, rule.PathContains))
        {
            return false;
        }

        if (rule.PathGlobs.Length > 0 && !AnyPathGlob(relPath, node.Name, rule.PathGlobs))
        {
            return false;
        }

        if (rule.SiblingGlobs.Length > 0)
        {
            if (node.ParentId is not long parentId ||
                childrenOf(parentId) is not { Count: > 0 } names ||
                !AnySiblingMatch(names, rule.SiblingGlobs))
            {
                return false;
            }
        }

        if (rule.ChildGlobs.Length > 0)
        {
            if (childrenOf(node.Id) is not { Count: > 0 } children ||
                !AnySiblingMatch(children, rule.ChildGlobs))
            {
                return false;
            }
        }

        if (rule.HeaderHex is { Length: > 0 } header)
        {
            if (rule.Rule.Sensitive || node.Problem != NodeProblem.None || node.Kind != NodeKind.File)
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

    private static bool AnyNameGlob(string name, string[] globs)
    {
        for (int index = 0; index < globs.Length; index++)
        {
            if (RuleGlob.MatchesName(name, globs[index]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AnyPathContains(string relPath, string[] fragments)
    {
        for (int index = 0; index < fragments.Length; index++)
        {
            if (relPath.Contains(fragments[index], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AnyPathGlob(string relPath, string nodeName, CompiledPathGlob[] globs)
    {
        for (int index = 0; index < globs.Length; index++)
        {
            if (globs[index].IsMatch(relPath, nodeName))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AnySiblingMatch(IReadOnlyList<string> names, string[] globs)
    {
        for (int globIndex = 0; globIndex < globs.Length; globIndex++)
        {
            string glob = globs[globIndex];
            for (int nameIndex = 0; nameIndex < names.Count; nameIndex++)
            {
                if (RuleGlob.MatchesName(names[nameIndex], glob))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static CompiledRule[] Compile(IReadOnlyList<ClassificationRule> rules)
    {
        CompiledRule[] compiled = new CompiledRule[rules.Count];
        for (int index = 0; index < rules.Count; index++)
        {
            ClassificationRule rule = rules[index];
            HashSet<string>? directoryNames = null;
            if (rule.DirectoryNames.Count > 0)
            {
                directoryNames = new HashSet<string>(rule.DirectoryNames, StringComparer.OrdinalIgnoreCase);
            }

            string[] pathContains = new string[rule.PathContains.Count];
            for (int fragment = 0; fragment < rule.PathContains.Count; fragment++)
            {
                pathContains[fragment] = RuleGlob.NormalizeRelPath(rule.PathContains[fragment]);
            }

            CompiledPathGlob[] pathGlobs = new CompiledPathGlob[rule.PathGlobs.Count];
            for (int glob = 0; glob < rule.PathGlobs.Count; glob++)
            {
                pathGlobs[glob] = RuleGlob.GetPathGlob(rule.PathGlobs[glob]);
            }

            NameFilter names = NameFilter.FromGlobs(rule.NameGlobs);
            compiled[index] = new CompiledRule(
                rule,
                names,
                pathGlobs,
                directoryNames,
                pathContains,
                rule.SiblingGlobs.ToArray(),
                rule.ChildGlobs.ToArray(),
                rule.MinSize,
                rule.HeaderHex,
                names.Any ||
                    pathGlobs.Length > 0 ||
                    directoryNames is not null ||
                    pathContains.Length > 0 ||
                    rule.SiblingGlobs.Count > 0 ||
                    rule.ChildGlobs.Count > 0 ||
                    !string.IsNullOrEmpty(rule.HeaderHex));
        }

        return compiled;
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

    private sealed class MatchScratch
    {
        public List<NodeBadgeRow> Badges { get; } = [];

        public HashSet<long> Sensitive { get; } = [];

        public Dictionary<string, (int Count, long Bytes, ClassificationRule Rule)> Hits { get; } = new(StringComparer.Ordinal);

        public Dictionary<long, Decision> Suggested { get; } = [];
    }

    private sealed record CompiledRule(
        ClassificationRule Rule,
        NameFilter Names,
        CompiledPathGlob[] PathGlobs,
        HashSet<string>? DirectoryNames,
        string[] PathContains,
        string[] SiblingGlobs,
        string[] ChildGlobs,
        long? MinSize,
        string? HeaderHex,
        bool HasConstraint);

    private sealed class NameFilter
    {
        private readonly HashSet<string>? exactNames;
        private readonly HashSet<string>? extensions;
        private readonly string[] complexGlobs;

        private NameFilter(HashSet<string>? exactNames, HashSet<string>? extensions, string[] complexGlobs, bool any)
        {
            this.exactNames = exactNames;
            this.extensions = extensions;
            this.complexGlobs = complexGlobs;
            Any = any;
        }

        public bool Any { get; }

        public static NameFilter FromGlobs(IReadOnlyList<string> globs)
        {
            if (globs.Count == 0)
            {
                return new NameFilter(null, null, [], false);
            }

            HashSet<string> exact = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> extensions = new(StringComparer.OrdinalIgnoreCase);
            List<string> complex = [];
            for (int index = 0; index < globs.Count; index++)
            {
                string glob = globs[index];
                if (IsSimpleExtensionGlob(glob))
                {
                    extensions.Add(glob[1..]);
                }
                else if (glob.IndexOfAny(['*', '?']) < 0)
                {
                    exact.Add(glob);
                }
                else
                {
                    complex.Add(glob);
                }
            }

            return new NameFilter(
                exact.Count == 0 ? null : exact,
                extensions.Count == 0 ? null : extensions,
                complex.Count == 0 ? [] : complex.ToArray(),
                true);
        }

        public bool Matches(string name)
        {
            if (exactNames is not null && exactNames.Contains(name))
            {
                return true;
            }

            if (extensions is not null)
            {
                int dot = name.LastIndexOf('.');
                if (dot > 0 &&
                    extensions.GetAlternateLookup<ReadOnlySpan<char>>().Contains(name.AsSpan(dot)))
                {
                    return true;
                }
            }

            return AnyNameGlob(name, complexGlobs);
        }

        private static bool IsSimpleExtensionGlob(string glob)
        {
            if (glob.Length < 3 || glob[0] != '*' || glob[1] != '.')
            {
                return false;
            }

            for (int index = 2; index < glob.Length; index++)
            {
                char character = glob[index];
                if (character is '*' or '?' or '.')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
