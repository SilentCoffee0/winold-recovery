using WinOldRecovery.Core.Browse;
using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.IO;
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
        Assert.Equal(row.ProblemExplanation, row.ProblemLabel);
    }

    [Fact]
    public void LongFileName_IsMiddleEllipsized_AndTooltipKeepsFullRelPath()
    {
        string name = new string('a', 30) + "-middle-" + new string('z', 30) + ".jpg";
        TreeNodeRow row = Row(NodeKind.File, name, []);
        Assert.Equal(48, row.DisplayName.Length);
        Assert.Contains("...", row.DisplayName, StringComparison.Ordinal);
        Assert.StartsWith("aaaaaaaa", row.DisplayName, StringComparison.Ordinal);
        Assert.EndsWith(".jpg", row.DisplayName, StringComparison.Ordinal);
        Assert.DoesNotContain("middle", row.DisplayName, StringComparison.Ordinal);
        Assert.Equal(name, row.RowTooltip);
    }

    [Fact]
    public void LongGroupHeader_IsMiddleEllipsized()
    {
        string path = @"Users\Alice\" + new string('n', 80) + @"\Desktop";
        TreeNodeRow header = new(
            10,
            1,
            "Desktop",
            path,
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

        Assert.Equal(PathDisplay.DefaultLimit, header.DisplayName.Length);
        Assert.Contains("...", header.DisplayName, StringComparison.Ordinal);
        Assert.StartsWith(@"Users\Alice", header.DisplayName, StringComparison.Ordinal);
        Assert.EndsWith(@"Desktop", header.DisplayName, StringComparison.Ordinal);
        Assert.Equal(path, header.RowTooltip);
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

    [Fact]
    public void DirectoryWithChildren_ShowsFoldGlyphIndentAndPercent()
    {
        TreeNodeRow closed = new(
            1,
            null,
            "Users",
            "Users",
            NodeKind.Directory,
            0,
            500,
            10,
            null,
            NodeProblem.None,
            Decision.Undecided,
            false,
            false,
            4,
            [],
            Depth: 1,
            IsExpanded: false,
            PercentOfParent: 42.5);
        Assert.True(closed.CanExpand);
        Assert.Equal("▸", closed.TreeGlyph);
        Assert.Equal(16, closed.DepthPx);
        Assert.Equal("42.5 %", closed.PercentLabel);
        Assert.Equal("▾", (closed with { IsExpanded = true }).TreeGlyph);
        TreeNodeRow file = closed with { Kind = NodeKind.File, ChildCount = 0, PercentOfParent = 0 };
        Assert.False(file.CanExpand);
        Assert.Equal("  ", file.TreeGlyph);
        Assert.Equal("—", file.PercentLabel);
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
