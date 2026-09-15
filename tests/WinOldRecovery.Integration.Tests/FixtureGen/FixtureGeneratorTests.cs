using WinOldRecovery.Core.Processes;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Registry;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.FixtureGen;

namespace WinOldRecovery.Integration.Tests.FixtureGen;

public sealed class FixtureGeneratorTests
{
    [Fact]
    public async Task PortableFixture_GeneratesAndPassesItsSelfCheck()
    {
        string testRoot = CreateTestRoot();
        string fixtureRoot = Path.Combine(testRoot, "Windows.old");

        try
        {
            FixtureGenerator generator = new(new ProcessRunner());
            FixtureManifest manifest = await generator.GenerateAsync(
                new FixtureOptions(
                    fixtureRoot,
                    NodeModulesFileCount: 128,
                    PortableMode: true));

            FixtureCheckResult result = new FixtureSelfCheck().Check(fixtureRoot);

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.Equal("Created", manifest.Hazards["legacy-junction"].Status);
            Assert.Equal("Created", manifest.Hazards["junction-loop"].Status);
            Assert.Equal("Created", manifest.Hazards["long-path"].Status);
            Assert.Equal("Created", manifest.Hazards["offline-placeholder"].Status);
            Assert.Equal("Created", manifest.Hazards["invalid-name"].Status);
            Assert.Equal("Unavailable", manifest.Hazards["deny-acl"].Status);
            Assert.Equal("Unavailable", manifest.Hazards["orphan-sid"].Status);
            Assert.Equal("Unavailable", manifest.Hazards["efs"].Status);
        }
        finally
        {
            DeleteTestFixture(testRoot);
        }
    }

    [Fact]
    public async Task Manifest_DoesNotContainCanarySecretValues()
    {
        string testRoot = CreateTestRoot();
        string fixtureRoot = Path.Combine(testRoot, "Windows.old");

        try
        {
            FixtureGenerator generator = new(new ProcessRunner());
            await generator.GenerateAsync(
                new FixtureOptions(
                    fixtureRoot,
                    NodeModulesFileCount: 5,
                    PortableMode: true));

            string manifest = await File.ReadAllTextAsync(
                Path.Combine(fixtureRoot, FixtureOptions.ManifestFileName));

            Assert.DoesNotContain("WINOLD_RECOVERY_CANARY", manifest);
        }
        finally
        {
            DeleteTestFixture(testRoot);
        }
    }

    [Fact]
    public async Task Generator_RefusesToReplaceExistingContents()
    {
        string testRoot = CreateTestRoot();
        string fixtureRoot = Path.Combine(testRoot, "Windows.old");
        Directory.CreateDirectory(fixtureRoot);
        await File.WriteAllTextAsync(Path.Combine(fixtureRoot, "keep.txt"), "keep");

        try
        {
            FixtureGenerator generator = new(new ProcessRunner());

            await Assert.ThrowsAsync<IOException>(
                () => generator.GenerateAsync(
                    new FixtureOptions(
                        fixtureRoot,
                        NodeModulesFileCount: 5,
                        PortableMode: true)));

            Assert.Equal(
                "keep",
                await File.ReadAllTextAsync(Path.Combine(fixtureRoot, "keep.txt")));
        }
        finally
        {
            DeleteTestFixture(testRoot);
        }
    }

    [Fact]
    public async Task OfflineRegistryParser_ReadsCopiedFixtureHive()
    {
        string testRoot = CreateTestRoot();
        string fixtureRoot = Path.Combine(testRoot, "Windows.old");

        try
        {
            FixtureGenerator generator = new(new ProcessRunner());
            FixtureManifest manifest = await generator.GenerateAsync(
                new FixtureOptions(
                    fixtureRoot,
                    NodeModulesFileCount: 5,
                    PortableMode: true));
            Assert.Equal("Created", manifest.Hazards["registry-hive"].Status);

            string hivePath = Path.Combine(fixtureRoot, "Users", "Alice", "NTUSER.DAT");
            DateTime before = File.GetLastWriteTimeUtc(hivePath);
            SourceGuard guard = new();
            guard.RegisterSourceRoot(fixtureRoot);
            SafeFs safeFs = new(guard);

            OfflineRegistryHive hive = await OfflineRegistryHive.OpenCopyAsync(
                hivePath,
                Path.Combine(testRoot, "session-tmp"),
                safeFs);

            Assert.True(hive.ContainsKey("Software"));
            Assert.Equal(before, File.GetLastWriteTimeUtc(hivePath));
        }
        finally
        {
            DeleteTestFixture(testRoot);
        }
    }

    [Fact]
    public async Task BundleSelfTest_RoundTripsRegistryAndSqlite()
    {
        BundleSelfTestResult result = await BundleSelfTest.RunAsync();

        Assert.True(result.RegistryParsed);
        Assert.True(result.SqliteLoaded);
        Assert.True(result.ElapsedMilliseconds >= 0);
    }

    private static string CreateTestRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"WinOldRecovery-FixtureGen-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTestFixture(string testRoot)
    {
        string alice = Path.Combine(testRoot, "Windows.old", "Users", "Alice");
        string[] directoryLinks =
        [
            Path.Combine(alice, "Application Data"),
            Path.Combine(alice, "Loop"),
            Path.Combine(alice, "DirectorySymlink"),
        ];

        foreach (string link in directoryLinks)
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }

        string fileLink = Path.Combine(alice, "file-link.txt");
        if (File.Exists(fileLink))
        {
            File.Delete(fileLink);
        }

        Directory.Delete(testRoot, recursive: true);
    }

    [Fact]
    public void FixtureGeneratorSource_DoesNotInvokeNetExe()
    {
        string source = File.ReadAllText(FindFixtureGeneratorSource());
        Assert.DoesNotContain("net.exe", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"net\"", source, StringComparison.Ordinal);
        Assert.Contains("AssignUnmappedOwner", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ScaleTree_CreatesExpectedEmptyFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinOldRecovery-scale-" + Guid.NewGuid().ToString("N"));
        try
        {
            ScaleTree.Create(root, directoryCount: 2, filesPerDirectory: 3);
            Assert.Equal(2 * 3, Directory.EnumerateFiles(Path.Combine(root, "Users", "Alice", "Scale"), "*.txt", SearchOption.AllDirectories).Count());
            Assert.Equal(4 + 2 + (2 * 3), ScaleTree.ExpectedNodes(2, 3));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void FixtureSelfCheckSource_ArmsWatchdogOnFullHazards()
    {
        string source = File.ReadAllText(FindToolsSource("FixtureSelfCheck.cs"));
        Assert.Contains("SelfCheckWatchdog", source, StringComparison.Ordinal);
        Assert.Contains("FileSystemWatcher", source, StringComparison.Ordinal);
        Assert.Contains("ReportIfSourceChanged", source, StringComparison.Ordinal);
    }

    private static string FindFixtureGeneratorSource() =>
        FindToolsSource("FixtureGenerator.cs");

    private static string FindToolsSource(string fileName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "tools", "FixtureGen", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate tools/FixtureGen/{fileName} from the test host.");
    }
}
