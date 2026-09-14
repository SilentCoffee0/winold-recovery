using WinOldRecovery.Core.IO;

namespace WinOldRecovery.Core.Tests.IO;

public sealed class ExplorerSelectTests
{
    [Fact]
    public void BuildSelectArgument_KeepsShortPaths()
    {
        Assert.Equal(
            @"/select,C:\Windows.old\Users\Alice\Desktop",
            ExplorerSelect.BuildSelectArgument(@"C:\Windows.old\Users\Alice\Desktop"));
    }

    [Fact]
    public void BuildSelectArgument_StripsExtendedPrefix()
    {
        Assert.Equal(
            @"/select,C:\Windows.old\Users\Alice\file.txt",
            ExplorerSelect.BuildSelectArgument(@"\\?\C:\Windows.old\Users\Alice\file.txt"));
    }

    [Fact]
    public void BuildSelectArgument_WalksToAnAncestorUnderTheExplorerLimit()
    {
        string leaf = @"C:\Windows.old\" + new string('a', 80) + "\\" + new string('b', 80) + "\\" + new string('c', 80) + "\\file.txt";
        Assert.True(leaf.Length > ExplorerSelect.MaxExplorerPathLength);
        string argument = ExplorerSelect.BuildSelectArgument(leaf);
        Assert.StartsWith("/select,", argument, StringComparison.Ordinal);
        string selected = argument["/select,".Length..];
        Assert.True(selected.Length <= ExplorerSelect.MaxExplorerPathLength);
        Assert.StartsWith(@"C:\Windows.old\", selected, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file.txt", selected, StringComparison.Ordinal);
    }
}
