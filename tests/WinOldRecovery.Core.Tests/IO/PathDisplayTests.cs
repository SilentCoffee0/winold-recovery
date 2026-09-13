using WinOldRecovery.Core.IO;

namespace WinOldRecovery.Core.Tests.IO;

public sealed class PathDisplayTests
{
    [Fact]
    public void MiddleEllipsis_KeepsShortPaths()
    {
        Assert.Equal(@"Users\Alice\Desktop", PathDisplay.MiddleEllipsis(@"Users\Alice\Desktop"));
    }

    [Fact]
    public void MiddleEllipsis_KeepsStartAndEnd()
    {
        string path = new string('a', 40) + "\\middle\\file.txt";
        string shown = PathDisplay.MiddleEllipsis(path, maxChars: 20);
        Assert.Equal(20, shown.Length);
        Assert.StartsWith("aaaaaaaa", shown, StringComparison.Ordinal);
        Assert.EndsWith("file.txt", shown, StringComparison.Ordinal);
        Assert.Contains("...", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("middle", shown, StringComparison.Ordinal);
    }
}
