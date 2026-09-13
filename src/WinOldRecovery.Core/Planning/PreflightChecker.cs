using WinOldRecovery.Core.Planning;
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

    public PreflightResult Check(RestorePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        long required = plan.TotalBytes + (plan.TotalBytes / 20) + AbsoluteMarginBytes;
        long free = freeSpace.GetFreeBytes(plan.DestinationRoot);
        List<string> blocking = [];
        List<string> warnings = [];
        List<PlanConflict> conflicts = [];

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

            if (File.Exists(item.DestinationPath))
            {
                FileInfo info = new(item.DestinationPath);
                conflicts.Add(
                    new PlanConflict(
                        item.DestinationPath,
                        info.Length,
                        info.LastWriteTimeUtc));
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
