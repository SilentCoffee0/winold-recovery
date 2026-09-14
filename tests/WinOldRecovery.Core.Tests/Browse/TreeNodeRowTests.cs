using WinOldRecovery.Core.Browse;
using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.Scan;

namespace WinOldRecovery.Core.Tests.Browse;

public sealed class TreeNodeRowTests
{
    [Fact]
    public void ReparseRow_UsesHollowNameDashDecisionAndTargetTooltip()
    {
        TreeNodeRow junction = Row(
            NodeKind.Junction,
            "Application Data",
            ["Reparse: 0xA0000003 -> C:\\Users\\VJ\\AppData"]);

        Assert.Equal("⊘ Application Data", junction.DisplayName);
        Assert.Equal("—", junction.DecisionLabel);
        Assert.Equal("—", junction.SizeLabel);
        Assert.Equal("—", junction.FilesLabel);
        Assert.Equal("—", junction.ModifiedLabel);
        Assert.False(junction.CanRestore);
        Assert.Contains("C:\\Users\\VJ\\AppData", junction.RowTooltip, StringComparison.Ordinal);
        Assert.Contains("cannot be restored", junction.RowTooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void CloudPlaceholder_ExplainsThatNothingIsOnDisk()
    {
        TreeNodeRow row = Row(NodeKind.CloudPlaceholder, "photo.jpg", [], NodeProblem.CloudOnly);
        Assert.Equal("photo.jpg", row.DisplayName);
        Assert.Contains("not on disk", row.ProblemExplanation, StringComparison.Ordinal);
        Assert.Equal(row.ProblemExplanation, row.RowTooltip);
    }

    private static TreeNodeRow Row(
        NodeKind kind,
        string name,
        IReadOnlyList<string> badges,
        NodeProblem problem = NodeProblem.None)
    {
        return new TreeNodeRow(
            1,
            null,
            name,
            name,
            kind,
            0,
            0,
            0,
            null,
            problem,
            Decision.Undecided,
            false,
            false,
            0,
            badges);
    }
}
