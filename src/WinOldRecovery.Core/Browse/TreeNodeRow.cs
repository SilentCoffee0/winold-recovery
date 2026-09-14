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
    bool MixedSubtree = false,
    long RestoreBytes = 0,
    long LeaveBehindBytes = 0,
    long UndecidedBytes = 0)
{
    public bool IsReparse => Kind is NodeKind.Junction or NodeKind.Symlink or NodeKind.MountPoint;

    public bool CanRestore => !IsReparse;

    public string SizeLabel => QuantityFormat.Bytes(AggSize);

    public string FilesLabel => QuantityFormat.Count(AggFiles);

    public string ModifiedLabel => ModifiedUtc?.ToString("d MMM yyyy", CultureInfo.InvariantCulture) ?? "—";

    public string BadgeText => string.Join(", ", Badges);

    public string MixedBar
    {
        get
        {
            long total = RestoreBytes + LeaveBehindBytes + UndecidedBytes;
            if (total <= 0)
            {
                return string.Empty;
            }

            const int slots = 8;
            int restore = (int)Math.Round(RestoreBytes * slots / (double)total);
            int leave = (int)Math.Round(LeaveBehindBytes * slots / (double)total);
            restore = Math.Clamp(restore, 0, slots);
            leave = Math.Clamp(leave, 0, slots - restore);
            int undecided = Math.Max(0, slots - restore - leave);
            return new string('█', restore) + new string('░', leave) + new string('▒', undecided);
        }
    }

    public string DecisionLabel
    {
        get
        {
            if (MixedSubtree)
            {
                string bar = MixedBar;
                return bar.Length == 0 ? "mixed" : "mixed " + bar;
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
