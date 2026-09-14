using System.Globalization;
using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Scan;

namespace WinOldRecovery.Core.Browse;

public enum FilesViewMode
{
    Tree,
    Largest,
    Recent,
    Search,
    Unknown,
    Problems,
}

public sealed record TreeNodeRow(
    long Id,
    long? ParentId,
    string Name,
    string RelPath,
    NodeKind Kind,
    long Size,
    long AggSize,
    long AggFiles,
    DateTimeOffset? ModifiedUtc,
    NodeProblem Problem,
    Decision EffectiveDecision,
    bool HasOwnUserDecision,
    bool HasSuggestedDefault,
    int ChildCount,
    IReadOnlyList<string> Badges,
    bool MixedSubtree = false)
{
    public bool IsReparse => Kind is NodeKind.Junction or NodeKind.Symlink or NodeKind.MountPoint;

    public bool CanRestore => !IsReparse;

    public string SizeLabel => QuantityFormat.Bytes(AggSize);

    public string FilesLabel => QuantityFormat.Count(AggFiles);

    public string ModifiedLabel => ModifiedUtc?.ToString("d MMM yyyy", CultureInfo.InvariantCulture) ?? "—";

    public string BadgeText => string.Join(", ", Badges);

    public string DecisionLabel
    {
        get
        {
            if (MixedSubtree)
            {
                return "mixed";
            }

            if (HasOwnUserDecision)
            {
                return EffectiveDecision.ToString();
            }

            if (EffectiveDecision == Decision.Undecided)
            {
                return "Undecided";
            }

            if (HasSuggestedDefault)
            {
                return $"{EffectiveDecision} (suggested)";
            }

            return $"{EffectiveDecision} (inherited)";
        }
    }
}

public sealed record NodePage(
    IReadOnlyList<TreeNodeRow> Rows,
    int TotalCount,
    bool Truncated);
