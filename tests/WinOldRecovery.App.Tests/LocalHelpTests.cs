using System.IO;
using WinOldRecovery.App.Help;

namespace WinOldRecovery.App.Tests;

public sealed class LocalHelpTests
{
    [Fact]
    public void Catalog_ListsTheUxHelpPages()
    {
        Assert.Contains(LocalHelp.Catalog, topic => topic.FileName == "limitations.md");
        Assert.Contains(LocalHelp.Catalog, topic => topic.FileName == "browser-passwords.md");
        Assert.Contains(LocalHelp.Catalog, topic => topic.FileName == "syncthing-identity.md");
        Assert.Contains(LocalHelp.Catalog, topic => topic.FileName == "git-repositories.md");
        Assert.Contains(LocalHelp.Catalog, topic => topic.FileName == "wsl.md");
        Assert.Contains(LocalHelp.Catalog, topic => topic.FileName == "gpg.md");
        Assert.Contains(LocalHelp.Catalog, topic => topic.FileName == "outlook.md");
    }

    [Fact]
    public void ReadDisplayText_RendersLocalMarkdownWithoutLinkTargets()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinOldRecovery-Help-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(
                Path.Combine(root, "limitations.md"),
                "# Limits\n\nChrome passwords [cannot](https://example.invalid/secret) be recovered.\n");
            LocalHelp help = new(root);
            string text = help.ReadDisplayText("limitations.md");
            Assert.Contains("Limits", text, StringComparison.Ordinal);
            Assert.Contains("cannot", text, StringComparison.Ordinal);
            Assert.DoesNotContain("https://", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("example.invalid", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_RejectsPathTraversal()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinOldRecovery-Help-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            LocalHelp help = new(root);
            Assert.Throws<InvalidOperationException>(() => help.Resolve("..\\secret.md"));
            Assert.Throws<InvalidOperationException>(() => help.Resolve("../secret.md"));
            Assert.Throws<InvalidOperationException>(() => help.Resolve("folder/page.md"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BundledHelp_IncludesLimitationsNextToTheTestHost()
    {
        LocalHelp help = LocalHelp.FromAppDirectory();
        string text = help.ReadDisplayText("limitations.md");
        Assert.Contains("Chrome and Edge passwords", text, StringComparison.OrdinalIgnoreCase);
    }
}
