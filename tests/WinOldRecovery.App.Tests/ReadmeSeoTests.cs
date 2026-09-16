using System.IO;
using WinOldRecovery.App.Help;

namespace WinOldRecovery.App.Tests;

public sealed class ReadmeSeoTests
{
    [Fact]
    public void Readme_ContainsProductSpecSearchPhrases()
    {
        string readme = File.ReadAllText(FindReadme());
        string[] phrases =
        [
            "Windows.old recovery",
            "Windows.old restore",
            "recover files after reinstalling Windows",
            "recover files from Windows.old",
            "Windows recovery tool",
            "selectively restore from Windows.old",
            "delete Windows.old safely",
        ];
        foreach (string phrase in phrases)
        {
            Assert.Contains(phrase, readme, StringComparison.Ordinal);
        }

        Assert.Contains(LocalHelp.Catalog, topic => topic.Title == "What can and cannot be recovered");
    }

    private static string FindReadme()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "README.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("README.md was not found from the test output directory.");
    }
}
