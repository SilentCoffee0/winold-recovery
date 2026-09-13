using WinOldRecovery.Native;

namespace WinOldRecovery.Core.Planning;

public interface IFreeSpaceProvider
{
    long GetFreeBytes(string directoryPath);
}

public sealed class Win32FreeSpaceProvider : IFreeSpaceProvider
{
    public long GetFreeBytes(string directoryPath)
    {
        return DiskSpace.GetFreeBytes(directoryPath);
    }
}

public sealed record PlanConflict(
    string DestinationPath,
    long ExistingSize,
    DateTimeOffset ExistingWriteUtc);

public sealed record PreflightResult(
    bool CanProceed,
    long RequiredBytes,
    long FreeBytes,
    IReadOnlyList<PlanConflict> Conflicts,
    IReadOnlyList<string> BlockingIssues,
    IReadOnlyList<string> Warnings);

public sealed class PreflightChecker
{
    public const long AbsoluteMarginBytes = 1L * 1024 * 1024 * 1024;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly IFreeSpaceProvider freeSpace;

    public PreflightChecker(IFreeSpaceProvider? freeSpace = null)
    {
        this.freeSpace = freeSpace ?? new Win32FreeSpaceProvider();
    }

    public PreflightResult Check(
        RestorePlan plan,
        ConflictPolicy conflictPolicy = ConflictPolicy.KeepBoth,
        IReadOnlySet<string>? overwriteDestinations = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        long required = plan.TotalBytes + (plan.TotalBytes / 20) + AbsoluteMarginBytes;
        long free = freeSpace.GetFreeBytes(plan.DestinationRoot);
        List<string> blocking = [];
        List<string> warnings = [];
        List<PlanConflict> conflicts = [];
        IReadOnlySet<string> approved = overwriteDestinations ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (free < required)
        {
            blocking.Add(
                $"Not enough free space. Need {required} bytes including margin; {free} bytes are free.");
        }

        foreach (PlanItem item in plan.Items)
        {
            string name = Path.GetFileName(item.DestinationPath);
            if (IsInvalidDestinationName(name))
            {
                blocking.Add("Invalid destination name: " + name);
            }

            if (item.DestinationPath.Length > 260)
            {
                warnings.Add("Long destination path: " + item.DestinationPath);
            }

            AddConflicts(item, conflicts);
        }

        if (conflictPolicy == ConflictPolicy.OverwriteApproved)
        {
            int missing = conflicts.Count(conflict => !approved.Contains(conflict.DestinationPath));
            if (missing > 0)
            {
                blocking.Add(
                    "Overwrite requires per-file confirmation. Review the conflict list and confirm each file.");
            }
        }

        return new PreflightResult(
            blocking.Count == 0,
            required,
            free,
            conflicts,
            blocking,
            warnings);
    }

    private static void AddConflicts(PlanItem item, List<PlanConflict> conflicts)
    {
        if (item.Operation == PlanOperation.CopyFile)
        {
            AddIfExists(item.DestinationPath, conflicts);
            return;
        }

        if (!Directory.Exists(item.SourcePath))
        {
            return;
        }

        foreach (string sourceFile in EnumerateFiles(item.SourcePath))
        {
            string relative = Path.GetRelativePath(item.SourcePath, sourceFile);
            AddIfExists(Path.Combine(item.DestinationPath, relative), conflicts);
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        Stack<string> directories = new();
        directories.Push(root);
        while (directories.Count > 0)
        {
            string directory = directories.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(entry);
                    continue;
                }

                yield return entry;
            }
        }
    }

    private static void AddIfExists(string destinationPath, List<PlanConflict> conflicts)
    {
        if (!File.Exists(destinationPath))
        {
            return;
        }

        FileInfo info = new(destinationPath);
        conflicts.Add(new PlanConflict(destinationPath, info.Length, info.LastWriteTimeUtc));
    }

    public static bool IsInvalidDestinationName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        string stem = Path.GetFileNameWithoutExtension(name);
        if (ReservedNames.Contains(stem) || ReservedNames.Contains(name))
        {
            return true;
        }

        return name.EndsWith(' ') || name.EndsWith('.') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0;
    }
}
