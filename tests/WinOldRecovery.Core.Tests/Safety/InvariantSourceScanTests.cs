namespace WinOldRecovery.Core.Tests.Safety;

public sealed class InvariantSourceScanTests
{
    [Fact]
    public void I3_SourceContainsNoMirrorOrMoveRobocopySwitches()
    {
        foreach (string path in SourceFiles())
        {
            string text = File.ReadAllText(path);
            Assert.DoesNotContain("/MIR", text, StringComparison.Ordinal);
            Assert.DoesNotContain("/PURGE", text, StringComparison.Ordinal);
            Assert.DoesNotContain("/MOVE", text, StringComparison.Ordinal);
            Assert.DoesNotContain("/MOV", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void I5_SourceDoesNotLoadOrMergeRegistryHives()
    {
        foreach (string path in SourceFiles())
        {
            string text = File.ReadAllText(path);
            Assert.DoesNotContain("RegLoadKey", text, StringComparison.Ordinal);
            Assert.DoesNotContain("RegRestoreKey", text, StringComparison.Ordinal);
            Assert.DoesNotContain("RegLoadAppKey", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void I7_SourceHasNoHttpClientOrSockets()
    {
        foreach (string path in SourceFiles())
        {
            string text = File.ReadAllText(path);
            Assert.DoesNotContain("HttpClient", text, StringComparison.Ordinal);
            Assert.DoesNotContain("TcpClient", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Socket(", text, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> SourceFiles()
    {
        string root = FindRepositoryRoot();
        foreach (string path in Directory.EnumerateFiles(
            Path.Combine(root, "src"),
            "*.cs",
            SearchOption.AllDirectories))
        {
            yield return path;
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WinOldRecovery.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}
