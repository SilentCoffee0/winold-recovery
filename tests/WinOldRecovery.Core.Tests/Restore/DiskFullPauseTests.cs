using WinOldRecovery.Core.Planning;
using WinOldRecovery.Core.Restore;

namespace WinOldRecovery.Core.Tests.Restore;

public sealed class DiskFullPauseTests
{
    [Fact]
    public void Format_NamesTheVolumeAndTheBytesToFree()
    {
        string message = DiskFullPause.Format(@"C:\Users\VJ\Recovered", remainingBytes: 5L * 1024 * 1024 * 1024, freeBytes: 0);
        Assert.StartsWith("C:", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("is full. Free", message, StringComparison.Ordinal);
        Assert.Contains("Resume", message, StringComparison.Ordinal);
        Assert.Contains("Cancel", message, StringComparison.Ordinal);
        Assert.Contains("GB", message, StringComparison.Ordinal);
    }
}
