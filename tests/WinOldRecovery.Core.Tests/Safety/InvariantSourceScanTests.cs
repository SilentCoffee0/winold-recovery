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
    public void I1_MutatingApisStayInsideSafeFsAndPurge()
    {
        string[] forbidden =
        [
            "File.Delete(",
            "Directory.Delete(",
            "File.Move(",
            "File.Copy(",
            "File.WriteAllText(",
            "File.WriteAllBytes(",
            "Directory.CreateDirectory(",
            "RegLoadKey",
        ];
        foreach (string path in SourceFiles())
        {
            if (IsAllowedWriteSurface(path))
            {
                continue;
            }

            string text = File.ReadAllText(path);
            foreach (string token in forbidden)
            {
                Assert.False(
                    text.Contains(token, StringComparison.Ordinal),
                    path + " uses " + token + " outside SafeFs/Purge.");
            }

            Assert.False(
                ContainsDirectSetAccessControl(text),
                path + " uses SetAccessControl outside SafeFs.");
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

    private static bool ContainsDirectSetAccessControl(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                trimmed.StartsWith("*", StringComparison.Ordinal))
            {
                continue;
            }

            if (!trimmed.Contains("SetAccessControl", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.Contains("SafeFs.SetAccessControl", StringComparison.Ordinal) ||
                trimmed.Contains("safeFs.SetAccessControl", StringComparison.Ordinal))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool IsAllowedWriteSurface(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.EndsWith("/IO/SafeFs.cs", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/Purge/", StringComparison.OrdinalIgnoreCase);
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
