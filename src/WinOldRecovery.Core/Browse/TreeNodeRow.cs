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

    public string DisplayName => IsReparse ? "⊘ " + Name : Name;

    public string SizeLabel => IsReparse ? "—" : QuantityFormat.Bytes(AggSize);

    public string FilesLabel => IsReparse ? "—" : QuantityFormat.Count(AggFiles);

    public string ModifiedLabel =>
        IsReparse ? "—" : ModifiedUtc?.ToString("d MMM yyyy", CultureInfo.InvariantCulture) ?? "—";

    public string BadgeText => string.Join(", ", Badges);

    public string RowTooltip
    {
        get
        {
            if (IsReparse)
            {
                string target = ReparseTarget;
                return string.IsNullOrEmpty(target)
                    ? "Junction or symlink. It is listed, not followed, and cannot be restored."
                    : "Points to " + target + ". Junctions and symlinks cannot be restored.";
            }

            if (Problem != NodeProblem.None)
            {
                return ProblemExplanation;
            }

            return RelPath;
        }
    }

    public string ReparseTarget
    {
        get
        {
            foreach (string badge in Badges)
            {
                const string prefix = "Reparse: ";
                if (!badge.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                string detail = badge[prefix.Length..];
                int arrow = detail.IndexOf(" -> ", StringComparison.Ordinal);
                return arrow < 0 ? string.Empty : detail[(arrow + 4)..];
            }

            return string.Empty;
        }
    }

    public string ProblemExplanation => Problem switch
    {
        NodeProblem.CloudOnly =>
            "Cloud placeholder. The file is not on disk here and cannot be restored.",
        NodeProblem.EfsEncrypted =>
            "Encrypted with EFS. This app does not decrypt it, so restore cannot copy the plaintext.",
        NodeProblem.AccessDenied =>
            "The scan could not read this folder. Run elevated with backup privilege to include it.",
        NodeProblem.LongPath =>
            "The path is longer than Explorer usually accepts. Restore uses long-path APIs; Open Folder uses the nearest shorter ancestor.",
        NodeProblem.InvalidDestName =>
            "The name is not valid at the destination (trailing space or a reserved device name).",
        NodeProblem.ZeroByteStub =>
            "Zero-byte stub. There may be nothing to restore.",
        _ => string.Empty,
    };

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

    public bool IsInheritedDecision =>
        !IsReparse &&
        !MixedSubtree &&
        !HasOwnUserDecision &&
        !HasSuggestedDefault &&
        EffectiveDecision != Decision.Undecided;

    public string DecisionTooltip =>
        DecisionDisplay.Tooltip(HasOwnUserDecision, HasSuggestedDefault, IsInheritedDecision) ?? RelPath;

    public string DecisionLabel
    {
        get
        {
            if (IsReparse)
            {
                return "—";
            }

            if (MixedSubtree)
            {
                string bar = MixedBar;
                return bar.Length == 0 ? "○ mixed" : "○ mixed " + bar;
            }

            return DecisionDisplay.Label(
                EffectiveDecision,
                HasOwnUserDecision,
                HasSuggestedDefault);
        }
    }
}

public sealed record NodePage(
    IReadOnlyList<TreeNodeRow> Rows,
    int TotalCount,
    bool Truncated);
