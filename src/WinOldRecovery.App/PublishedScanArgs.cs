using System.IO;

namespace WinOldRecovery.App;

public static class PublishedScanArgs
{
    public static bool TryParse(IReadOnlyList<string> args, out string sourceRoot, out string reportPath)
    {
        sourceRoot = string.Empty;
        reportPath = string.Empty;
        if (args is null || args.Count < 4)
        {
            return false;
        }

        string? source = null;
        string? report = null;
        for (int index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], "--scan", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Count)
            {
                source = args[++index];
                continue;
            }

            if (string.Equals(args[index], "--report", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Count)
            {
                report = args[++index];
            }
        }

        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(report))
        {
            return false;
        }

        sourceRoot = Path.GetFullPath(source);
        reportPath = Path.GetFullPath(report);
        return true;
    }
}
