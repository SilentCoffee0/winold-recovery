using System.Diagnostics;
using WinOldRecovery.Core.Processes;
using WinOldRecovery.Core.Scan;

namespace WinOldRecovery.Core.Tests.Scan;

public sealed class SourceDiscoveryTests : IDisposable
{
    private readonly string testRoot;

    public SourceDiscoveryTests()
    {
        testRoot = Path.Combine(Path.GetTempPath(), $"WinOldRecovery-Scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
    }

    [Fact]
    public async Task Discover_FindsWindowsOldVariantsAndSkipsReparsePoints()
    {
        string volume = Path.Combine(testRoot, "C");
        Directory.CreateDirectory(volume);
        string windowsOld = Path.Combine(volume, "Windows.old");
        string numbered = Path.Combine(volume, "Windows.old.000");
        string reparse = Path.Combine(volume, "Windows.old.link");
        CreateWindowsOld(windowsOld, createdDaysAgo: 1);
        CreateWindowsOld(numbered, createdDaysAgo: 3);
        CreateJunction(reparse, Path.Combine(testRoot, "live"));

        SourceDiscovery discovery = CreateDiscovery([volume], cleanupTaskPresent: true);
        IReadOnlyList<SourceCandidate> candidates = await discovery.DiscoverAsync();

        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate => Assert.Equal(SourceCandidateKind.WindowsOld, candidate.Kind));
        Assert.Contains(candidates, candidate => candidate.Path.EndsWith(@"\Windows.old", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(candidates, candidate => candidate.Path.EndsWith(@"\Windows.old.000", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(candidates, candidate => candidate.Path.EndsWith(@"\Windows.old.link", StringComparison.OrdinalIgnoreCase));
        Assert.All(candidates, candidate => Assert.True(candidate.CleanupTaskPresent));
        Assert.All(candidates, candidate => Assert.NotNull(candidate.EstimatedAutoDeleteAt));
        Assert.Contains("estimated deletion", candidates[0].DisplayLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Discover_IncludesOtherFixedVolumeWithUsersAndWindows()
    {
        string volume = Path.Combine(testRoot, "D");
        Directory.CreateDirectory(Path.Combine(volume, "Users"));
        Directory.CreateDirectory(Path.Combine(volume, "Windows"));

        SourceDiscovery discovery = CreateDiscovery([volume], cleanupTaskPresent: false);
        IReadOnlyList<SourceCandidate> candidates = await discovery.DiscoverAsync();

        SourceCandidate candidate = Assert.Single(candidates);
        Assert.Equal(SourceCandidateKind.OldSystemVolume, candidate.Kind);
        Assert.True(candidate.LooksLikeWindowsInstallation);
        Assert.False(candidate.CleanupTaskPresent);
        Assert.Null(candidate.EstimatedAutoDeleteAt);
    }

    [Fact]
    public void InspectBrowsedPath_WarnsWhenUsersIsMissingWithoutBlocking()
    {
        string folder = Path.Combine(testRoot, "custom");
        Directory.CreateDirectory(folder);

        SourceDiscovery discovery = CreateDiscovery([], cleanupTaskPresent: false);
        SourceCandidate candidate = discovery.InspectBrowsedPath(folder, cleanupTaskPresent: false);

        Assert.Equal(SourceCandidateKind.BrowsedFolder, candidate.Kind);
        Assert.False(candidate.HasUsersFolder);
        Assert.False(candidate.LooksLikeWindowsInstallation);
    }

    [Fact]
    public void InspectBrowsedPath_RejectsMissingFolder()
    {
        SourceDiscovery discovery = CreateDiscovery([], cleanupTaskPresent: false);

        Assert.Throws<DirectoryNotFoundException>(
            () => discovery.InspectBrowsedPath(
                Path.Combine(testRoot, "missing"),
                cleanupTaskPresent: false));
    }

    [Fact]
    public async Task I1_DiscoveryDoesNotWriteUnderCandidateSource()
    {
        string volume = Path.Combine(testRoot, "E");
        string windowsOld = Path.Combine(volume, "Windows.old");
        CreateWindowsOld(windowsOld, createdDaysAgo: 0);
        DateTime before = Directory.GetLastWriteTimeUtc(windowsOld);

        SourceDiscovery discovery = CreateDiscovery([volume], cleanupTaskPresent: false);
        await discovery.DiscoverAsync();

        Assert.Equal(before, Directory.GetLastWriteTimeUtc(windowsOld));
        Assert.Equal(2, Directory.GetFileSystemEntries(windowsOld).Length);
    }

    [Fact]
    public async Task CleanupTaskQuery_UsesReadOnlySchtasksArguments()
    {
        RecordingProcessRunner runner = new(exitCode: 0);
        SourceDiscovery discovery = new(
            new StubVolumeRootProvider([]),
            runner);

        Assert.True(await discovery.IsCleanupTaskPresentAsync());
        ProcessRequest request = Assert.Single(runner.Requests);
        Assert.Equal("schtasks.exe", request.FileName);
        Assert.Equal("/Query", request.Arguments[0]);
        Assert.Equal(SourceDiscovery.SetupCleanupTaskName, request.Arguments[2]);
        Assert.DoesNotContain("/Change", request.Arguments, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("/Create", request.Arguments, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("/Delete", request.Arguments, StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        string reparse = Path.Combine(testRoot, "C", "Windows.old.link");
        if (Directory.Exists(reparse) &&
            (File.GetAttributes(reparse) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(reparse);
        }

        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static SourceDiscovery CreateDiscovery(
        IReadOnlyList<string> volumes,
        bool cleanupTaskPresent)
    {
        return new SourceDiscovery(
            new StubVolumeRootProvider(volumes),
            new RecordingProcessRunner(cleanupTaskPresent ? 0 : 1));
    }

    private static void CreateWindowsOld(string path, int createdDaysAgo)
    {
        Directory.CreateDirectory(Path.Combine(path, "Users"));
        Directory.CreateDirectory(Path.Combine(path, "Windows"));
        DateTime created = DateTime.Now.AddDays(-createdDaysAgo);
        Directory.SetCreationTime(path, created);
    }

    private static void CreateJunction(string junction, string target)
    {
        Directory.CreateDirectory(target);
        ProcessStartInfo startInfo = new(
            Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junction);
        startInfo.ArgumentList.Add(target);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start mklink.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class StubVolumeRootProvider(IReadOnlyList<string> roots) : IVolumeRootProvider
    {
        public IReadOnlyList<string> GetFixedVolumeRoots() => roots;
    }

    private sealed class RecordingProcessRunner(int exitCode) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public Task<ProcessResult> RunAsync(
            ProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ProcessResult(exitCode, string.Empty, string.Empty));
        }
    }
}
