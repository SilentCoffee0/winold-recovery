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

    [Fact]
    public void GroupHeader_ShowsFolderPathAndCannotRestore()
    {
        TreeNodeRow header = new(
            10,
            1,
            "Desktop",
            @"Users\Alice\Desktop",
            NodeKind.Directory,
            0,
            0,
            0,
            null,
            NodeProblem.None,
            Decision.Restore,
            false,
            true,
            2,
            [],
            IsGroupHeader: true);

        Assert.True(header.IsGroupHeader);
        Assert.False(header.CanRestore);
        Assert.Equal(@"Users\Alice\Desktop", header.DisplayName);
        Assert.Equal("—", header.DecisionLabel);
        Assert.Equal("—", header.SizeLabel);
        Assert.Equal("—", header.FilesLabel);
        Assert.Equal("—", header.ModifiedLabel);
        Assert.False(header.IsInheritedDecision);
    }

    [Fact]
    public void SuggestedRestore_IsHollowWithConfirmTooltip()
    {
        TreeNodeRow row = new(
            1,
            null,
            "Documents",
            "Documents",
            NodeKind.Directory,
            0,
            0,
            0,
            null,
            NodeProblem.None,
            Decision.Restore,
            false,
            true,
            0,
            []);
        Assert.Equal("◌ Restore (suggested)", row.DecisionLabel);
        Assert.Equal(DecisionDisplay.SuggestedTooltip, row.DecisionTooltip);
        Assert.False(row.IsInheritedDecision);
    }

    [Fact]
    public void ConfirmedRestore_IsFilledWithoutSuggestedTooltip()
    {
        TreeNodeRow row = new(
            1,
            null,
            "Documents",
            "Documents",
            NodeKind.Directory,
            0,
            0,
            0,
            null,
            NodeProblem.None,
            Decision.Restore,
            true,
            true,
            0,
            []);
        Assert.Equal("● Restore", row.DecisionLabel);
        Assert.NotEqual(DecisionDisplay.SuggestedTooltip, row.DecisionTooltip);
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
