using WinOldRecovery.Core.Decisions;

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
    IReadOnlyList<RuleHitCount> Breakdown);
