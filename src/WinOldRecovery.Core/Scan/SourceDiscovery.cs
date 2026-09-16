using System.Globalization;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Processes;

namespace WinOldRecovery.Core.Scan;

public sealed record CleanupTaskStatus(bool Present, DateTimeOffset? NextRunAt);

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
        Task<CleanupTaskStatus> cleanupQuery = QueryCleanupTaskAsync(cancellationToken);
        List<(string Path, SourceCandidateKind Kind)> found = [];
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
                    found.Add((windowsOld, SourceCandidateKind.WindowsOld));
                }
            }

            if (LooksLikeOldSystemVolume(volumeRoot) && seen.Add(volumeRoot))
            {
                found.Add((volumeRoot, SourceCandidateKind.OldSystemVolume));
            }
        }

        CleanupTaskStatus cleanupTask = await cleanupQuery.ConfigureAwait(false);
        List<SourceCandidate> candidates = new(found.Count);
        foreach ((string path, SourceCandidateKind kind) in found)
        {
            candidates.Add(Inspect(path, kind, cleanupTask));
        }

        return candidates
            .OrderBy(static candidate => candidate.Kind)
            .ThenBy(static candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public SourceCandidate InspectBrowsedPath(string path, bool cleanupTaskPresent)
    {
        return InspectBrowsedPath(path, new CleanupTaskStatus(cleanupTaskPresent, null));
    }

    public SourceCandidate InspectBrowsedPath(string path, CleanupTaskStatus cleanupTask)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(cleanupTask);
        string normalized = PathCanonicalizer.NormalizeLexically(path);
        if (!Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException($"The chosen folder does not exist: '{path}'.");
        }

        return Inspect(normalized, SourceCandidateKind.BrowsedFolder, cleanupTask);
    }

    public async Task<bool> IsCleanupTaskPresentAsync(
        CancellationToken cancellationToken = default)
    {
        CleanupTaskStatus status = await QueryCleanupTaskAsync(cancellationToken).ConfigureAwait(false);
        return status.Present;
    }

    public async Task<CleanupTaskStatus> QueryCleanupTaskAsync(
        CancellationToken cancellationToken = default)
    {
        ProcessResult result = await processRunner.RunAsync(
                new ProcessRequest(
                    "schtasks.exe",
                    ["/Query", "/TN", SetupCleanupTaskName, "/FO", "LIST"],
                    Timeout: TimeSpan.FromSeconds(2)),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            return new CleanupTaskStatus(false, null);
        }

        return new CleanupTaskStatus(true, ParseNextRunTime(result.StandardOutput));
    }

    public static DateTimeOffset? ParseNextRunTime(string? standardOutput)
    {
        if (string.IsNullOrWhiteSpace(standardOutput))
        {
            return null;
        }

        foreach (string raw in standardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            int colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            string name = line[..colon].Trim();
            if (!name.Contains("Next Run", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = line[(colon + 1)..].Trim();
            if (value.Length == 0 ||
                value.Equals("N/A", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Disabled", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Never", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (DateTime.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out DateTime parsed) ||
                DateTime.TryParse(
                    value,
                    CultureInfo.CurrentCulture,
                    DateTimeStyles.AssumeLocal,
                    out parsed))
            {
                return new DateTimeOffset(parsed);
            }
        }

        return null;
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
        CleanupTaskStatus cleanupTask)
    {
        string normalized = PathCanonicalizer.NormalizeLexically(path);
        DateTimeOffset createdAt = Directory.GetCreationTime(normalized);
        DateTimeOffset? estimatedDelete = kind == SourceCandidateKind.WindowsOld
            ? createdAt + AutomaticCleanupWindow
            : null;
        bool taskPresent = cleanupTask.Present && kind == SourceCandidateKind.WindowsOld;

        return new SourceCandidate(
            normalized,
            kind,
            createdAt,
            estimatedDelete,
            LooksLikeWindowsInstallation(normalized),
            HasUsersFolder(normalized),
            taskPresent,
            taskPresent ? cleanupTask.NextRunAt : null,
            CountUserProfiles(normalized));
    }

    private static int CountUserProfiles(string sourceRoot)
    {
        string users = Path.Combine(sourceRoot, "Users");
        if (!Directory.Exists(users))
        {
            return 0;
        }

        EnumerationOptions options = new()
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0,
        };

        int count = 0;
        foreach (string directory in Directory.EnumerateDirectories(users, "*", options))
        {
            string name = Path.GetFileName(directory);
            if (name.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Default User", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("All Users", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            count++;
        }

        return count;
    }
}
