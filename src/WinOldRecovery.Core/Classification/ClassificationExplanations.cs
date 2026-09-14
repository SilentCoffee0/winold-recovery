using WinOldRecovery.Core.Browse;

namespace WinOldRecovery.Core.Classification;

public static class ClassificationExplanations
{
    public static IReadOnlyList<string> ForNode(
        TreeNodeRow node,
        IReadOnlyList<ClassificationRule> rules)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(rules);
        List<string> lines = [];
        foreach (ClassificationRule rule in rules)
        {
            if (rule.Why.Length == 0 || !HasBadge(node, rule.Badge) || !LooksLikeThisRule(node, rule))
            {
                continue;
            }

            string line = rule.Badge + ": " + rule.Why;
            if (!lines.Contains(line, StringComparer.Ordinal))
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    private static bool HasBadge(TreeNodeRow node, string badge)
    {
        return node.Badges.Any(item => item.Contains(badge, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeThisRule(TreeNodeRow node, ClassificationRule rule)
    {
        if (rule.DirectoryNames.Count > 0 &&
            rule.DirectoryNames.Contains(node.Name, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (rule.NameGlobs.Count > 0 &&
            rule.NameGlobs.Any(glob => RuleGlob.MatchesName(node.Name, glob)))
        {
            return true;
        }

        if (rule.PathGlobs.Count > 0 &&
            rule.PathGlobs.Any(glob => RuleGlob.MatchesPath(node.RelPath, glob)))
        {
            return true;
        }

        if (rule.PathContains.Count > 0 &&
            rule.PathContains.Any(fragment => RuleGlob.ContainsSegment(node.RelPath, fragment)))
        {
            return true;
        }

        return rule.DirectoryNames.Count == 0 &&
            rule.NameGlobs.Count == 0 &&
            rule.PathGlobs.Count == 0 &&
            rule.PathContains.Count == 0;
    }
}
