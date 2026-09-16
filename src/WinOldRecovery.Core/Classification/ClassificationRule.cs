using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.Persistence;

namespace WinOldRecovery.Core.Classification;

public enum ClassificationKind
{
    HighValue,
    Regeneratable,
    GameSave,
    Sensitive,
}

public sealed class ClassificationRule
{
    public required string Id { get; init; }

    public required ClassificationKind Kind { get; init; }

    public required string Badge { get; init; }

    public string Why { get; init; } = string.Empty;

    public bool Sensitive { get; init; }

    public Decision SuggestedDefault { get; init; } = Decision.Undecided;

    public IReadOnlyList<string> NameGlobs { get; init; } = [];

    public IReadOnlyList<string> PathGlobs { get; init; } = [];

    public IReadOnlyList<string> DirectoryNames { get; init; } = [];

    public IReadOnlyList<string> PathContains { get; init; } = [];

    public IReadOnlyList<string> SiblingGlobs { get; init; } = [];

    public IReadOnlyList<string> ChildGlobs { get; init; } = [];

    public long? MinSize { get; init; }

    public string? HeaderHex { get; init; }
}

public sealed class ClassificationRuleFile
{
    public IReadOnlyList<ClassificationRule> Rules { get; init; } = [];
}

public sealed record RuleHitCount(string RuleId, string Badge, ClassificationKind Kind, int Count, long Bytes);

public sealed record ClassificationSummary(
    int HighValueCount,
    long HighValueBytes,
    int RegeneratableCount,
    long RegeneratableBytes,
    IReadOnlyList<RuleHitCount> Breakdown)
{
    public static ClassificationSummary FromBadgeTotals(IReadOnlyList<BadgeKindTotal> totals)
    {
        ArgumentNullException.ThrowIfNull(totals);
        int highCount = 0;
        long highBytes = 0;
        int regenCount = 0;
        long regenBytes = 0;
        List<RuleHitCount> breakdown = [];
        foreach (BadgeKindTotal total in totals)
        {
            if (!Enum.TryParse(total.Kind, ignoreCase: false, out ClassificationKind kind) ||
                kind == ClassificationKind.Sensitive)
            {
                continue;
            }

            breakdown.Add(
                new RuleHitCount(total.Kind + ":" + total.Detail, total.Detail, kind, total.Count, total.Bytes));
            if (kind is ClassificationKind.HighValue or ClassificationKind.GameSave)
            {
                highCount += total.Count;
                highBytes += total.Bytes;
            }
            else if (kind == ClassificationKind.Regeneratable)
            {
                regenCount += total.Count;
                regenBytes += total.Bytes;
            }
        }

        return new ClassificationSummary(highCount, highBytes, regenCount, regenBytes, breakdown);
    }
}
