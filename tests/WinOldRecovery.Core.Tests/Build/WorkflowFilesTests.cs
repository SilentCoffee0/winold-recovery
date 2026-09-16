namespace WinOldRecovery.Core.Tests.Build;

public sealed class WorkflowFilesTests
{
    [Fact]
    public void CiWorkflow_BuildsTestsAndPublishesSingleFileArtifact()
    {
        string workflow = ReadWorkflow("ci.yml");

        Assert.Contains("windows-latest", workflow);
        Assert.Contains("dotnet build WinOldRecovery.slnx --configuration Release --no-restore -warnaserror", workflow);
        Assert.Contains("dotnet test WinOldRecovery.slnx --configuration Release --no-build --no-restore", workflow);
        Assert.Contains("-p:PublishProfile=win-x64", workflow);
        Assert.Contains("WinOldRecovery.exe", workflow);
        Assert.Contains("actions/checkout@v5", workflow);
        Assert.Contains("actions/setup-dotnet@v5", workflow);
        Assert.Contains("actions/upload-artifact@v6", workflow);
        Assert.DoesNotContain("actions/checkout@v4", workflow);
        Assert.DoesNotContain("actions/setup-dotnet@v4", workflow);
        Assert.DoesNotContain("actions/upload-artifact@v4", workflow);
        Assert.DoesNotContain("/MIR", workflow);
        Assert.DoesNotContain("/PURGE", workflow);
        Assert.DoesNotContain("/MOV", workflow);
        Assert.DoesNotContain("/MOVE", workflow);
    }

    [Fact]
    public void ReleaseWorkflow_CreatesTaggedAssetsWithChecksums()
    {
        string workflow = ReadWorkflow("release.yml");

        Assert.Contains("windows-latest", workflow);
        Assert.Contains("tags:", workflow);
        Assert.Contains("v*", workflow);
        Assert.Contains("SHA256", workflow);
        Assert.Contains("gh release create", workflow);
        Assert.Contains("actions/checkout@v5", workflow);
        Assert.Contains("actions/setup-dotnet@v5", workflow);
        Assert.DoesNotContain("actions/checkout@v4", workflow);
        Assert.DoesNotContain("actions/setup-dotnet@v4", workflow);
        Assert.DoesNotContain("v0.1.0-m0", workflow);
        Assert.DoesNotContain("/MIR", workflow);
        Assert.DoesNotContain("/PURGE", workflow);
        Assert.DoesNotContain("/MOV", workflow);
        Assert.DoesNotContain("/MOVE", workflow);
    }

    private static string ReadWorkflow(string fileName)
    {
        string root = FindRepositoryRoot();
        string path = Path.Combine(root, ".github", "workflows", fileName);
        Assert.True(File.Exists(path), $"Missing workflow file '{path}'.");
        return File.ReadAllText(path);
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
