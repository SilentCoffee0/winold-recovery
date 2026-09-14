using WinOldRecovery.Core.Purge;

namespace WinOldRecovery.Core.Tests.Purge;

public sealed class PurgeProgressFormatTests
{
    [Fact]
    public void Line_ShowsDeletedCountAndPath()
    {
        string line = PurgeProgressFormat.Line(new PurgeProgress(12, @"Users\Alice\notes.txt"));
        Assert.Equal("Deleting…  12 items  Users\\Alice\\notes.txt", line);
    }

    [Fact]
    public void CancelledDetail_StatesPartialTreeRemains()
    {
        Assert.Contains("partly deleted", PurgeProgressFormat.CancelledDetail, StringComparison.Ordinal);
        Assert.Contains("stay on disk", PurgeProgressFormat.CancelledDetail, StringComparison.Ordinal);
    }
}
