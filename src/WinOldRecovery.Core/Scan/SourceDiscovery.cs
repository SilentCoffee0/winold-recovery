using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Processes;

namespace WinOldRecovery.Core.Scan;

public sealed class SourceDiscovery
{
    public const string SetupCleanupTaskName = @"\Microsoft\Windows\Setup\SetupCleanupTask";
    public static readonly TimeSpan AutomaticCleanupWindow = TimeSpan.FromDays(10);

    private readonly IVolumeRootProvider volumeRootProvider;
    private readonly IProcessRunner processRunner;
    private readonly string liveSystemRoot;

    public SourceDiscovery(
        IVolumeRootProvider volumeRootProvider,
        IProcessRunner processRunner,
        string? liveSystemRoot = null)
    {
        this.volumeRootProvider =
            volumeRootProvider ?? throw new ArgumentNullException(nameof(volumeRootProvider));
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        this.liveSystemRoot = Path.GetPathRoot(
            liveSystemRoot ?? Environment.SystemDirectory)
            ?? throw new InvalidOperationException("The live system volume could not be determined.");
    }

    public async Task<IReadOnlyList<SourceCandidate>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        bool cleanupTaskPresent = await IsCleanupTaskPresentAsync(cancellationToken)
            .ConfigureAwait(false);

        List<SourceCandidate> candidates = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string volumeRoot in volumeRootProvider.GetFixedVolumeRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(volumeRoot))
            {
                continue;
            }

            foreach (string windowsOld in EnumerateWindowsOldDirectories(volumeRoot))
            {
                if (seen.Add(windowsOld))
                {
                    candidates.Add(Inspect(windowsOld, SourceCandidateKind.WindowsOld, cleanupTaskPresent));
                }
            }

            if (LooksLikeOldSystemVolume(volumeRoot) && seen.Add(volumeRoot))
            {
                candidates.Add(
                    Inspect(volumeRoot, SourceCandidateKind.OldSystemVolume, cleanupTaskPresent));
            }
        }

        return candidates
            .OrderBy(static candidate => candidate.Kind)
            .ThenBy(static candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public SourceCandidate InspectBrowsedPath(string path, bool cleanupTaskPresent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalized = PathCanonicalizer.NormalizeLexically(path);
        if (!Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException($"The chosen folder does not exist: '{path}'.");
        }

        return Inspect(normalized, SourceCandidateKind.BrowsedFolder, cleanupTaskPresent);
    }

    public async Task<bool> IsCleanupTaskPresentAsync(
        CancellationToken cancellationToken = default)
    {
        ProcessResult result = await processRunner.RunAsync(
                new ProcessRequest(
                    "schtasks.exe",
                    ["/Query", "/TN", SetupCleanupTaskName, "/FO", "LIST"],
                    Timeout: TimeSpan.FromSeconds(15)),
                cancellationToken)
            .ConfigureAwait(false);

        return result.ExitCode == 0;
    }

    public static bool HasUsersFolder(string path)
    {
        return Directory.Exists(Path.Combine(path, "Users"));
    }

    public static bool LooksLikeWindowsInstallation(string path)
    {
        return HasUsersFolder(path) && Directory.Exists(Path.Combine(path, "Windows"));
    }

    private static IEnumerable<string> EnumerateWindowsOldDirectories(string volumeRoot)
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0,
        };

        foreach (string directory in Directory.EnumerateDirectories(
                     volumeRoot,
                     "Windows.old*",
                     options))
        {
            FileAttributes attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            yield return directory;
        }
    }

    private bool LooksLikeOldSystemVolume(string volumeRoot)
    {
        string normalized = PathCanonicalizer.NormalizeLexically(volumeRoot);
        return LooksLikeWindowsInstallation(normalized) &&
               !string.Equals(
                   normalized,
                   PathCanonicalizer.NormalizeLexically(liveSystemRoot),
                   StringComparison.OrdinalIgnoreCase);
    }

    private SourceCandidate Inspect(
        string path,
        SourceCandidateKind kind,
        bool cleanupTaskPresent)
    {
        string normalized = PathCanonicalizer.NormalizeLexically(path);
        DateTimeOffset createdAt = Directory.GetCreationTime(normalized);
        DateTimeOffset? estimatedDelete = kind == SourceCandidateKind.WindowsOld
            ? createdAt + AutomaticCleanupWindow
            : null;

        return new SourceCandidate(
            normalized,
            kind,
            createdAt,
            estimatedDelete,
            LooksLikeWindowsInstallation(normalized),
            HasUsersFolder(normalized),
            cleanupTaskPresent && kind == SourceCandidateKind.WindowsOld);
    }
}
