using WinOldRecovery.Core.Restore;

namespace WinOldRecovery.Core.Tests.Restore;

public sealed class RestoreSkipFormatTests
{
    [Fact]
    public void Summary_EmptyWhenNothingSkipped()
    {
        Assert.Equal(string.Empty, RestoreSkipFormat.Summary(new RestoreSkipCounts()));
    }

    [Fact]
    public void Summary_CountsEachSkippedKindOnce()
    {
        RestoreSkipCounts counts = new();
        counts.Add(FileAttributes.ReparsePoint | FileAttributes.Directory);
        counts.Add(FileAttributes.Offline);
        counts.Add(FileAttributes.Encrypted);
        Assert.Equal("Warnings (3)", RestoreSkipFormat.Summary(counts));
        string detail = RestoreSkipFormat.Detail(counts);
        Assert.Contains("1 junctions or symlinks skipped (not followed)", detail, StringComparison.Ordinal);
        Assert.Contains("1 cloud placeholders skipped", detail, StringComparison.Ordinal);
        Assert.Contains("1 EFS-encrypted files skipped", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_CountsReparseAheadOfOfflineOnTheSameEntry()
    {
        RestoreSkipCounts counts = new();
        counts.Add(FileAttributes.ReparsePoint | FileAttributes.Offline);
        Assert.Equal(1, counts.Reparse);
        Assert.Equal(0, counts.Offline);
        Assert.Equal(1, counts.Total);
    }
}
