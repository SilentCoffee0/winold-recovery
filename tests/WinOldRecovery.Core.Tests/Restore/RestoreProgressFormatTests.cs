using WinOldRecovery.Core.Restore;

namespace WinOldRecovery.Core.Tests.Restore;

public sealed class RestoreProgressFormatTests
{
    [Fact]
    public void Percent_UsesBytesWhenTotalBytesIsKnown()
    {
        RestoreProgress progress = new(1, 4, 38, 100, "Documents");
        Assert.Equal(38, RestoreProgressFormat.Percent(progress));
    }

    [Fact]
    public void Percent_FallsBackToItemsWhenTotalBytesIsZero()
    {
        RestoreProgress progress = new(2, 4, 0, 0, "Documents");
        Assert.Equal(50, RestoreProgressFormat.Percent(progress));
    }

    [Fact]
    public void FormatEta_OmitsEstimateUntilBytesHaveCopied()
    {
        Assert.Equal(string.Empty, RestoreProgressFormat.FormatEta(0, 100, TimeSpan.FromMinutes(1)));
        Assert.Equal(string.Empty, RestoreProgressFormat.FormatEta(100, 100, TimeSpan.FromMinutes(1)));
        Assert.Equal(string.Empty, RestoreProgressFormat.FormatEta(50, 100, TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void FormatEta_ReportsMinutesLeftFromCopyRate()
    {
        Assert.Equal("~9 min left", RestoreProgressFormat.FormatEta(38, 100, TimeSpan.FromSeconds(331)));
    }

    [Fact]
    public void Line_MatchesUxHeadline()
    {
        RestoreProgress progress = new(1, 2, 38, 100, "Documents");
        string line = RestoreProgressFormat.Line(progress, TimeSpan.FromSeconds(331));
        Assert.Equal("Restoring…  38 %   38 bytes of 100 bytes   ~9 min left  Documents", line);
    }
}
