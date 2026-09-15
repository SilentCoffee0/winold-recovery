using System.IO;

namespace WinOldRecovery.App;

public static class PublishedRestoreArgs
{
    public static bool TryParse(
        IReadOnlyList<string> args,
        out string sourceRoot,
        out string destinationRoot,
        out string reportPath)
    {
        sourceRoot = string.Empty;
        destinationRoot = string.Empty;
        reportPath = string.Empty;
        if (args is null || args.Count < 5)
        {
            return false;
        }

        string? source = null;
        string? destination = null;
        string? report = null;
        for (int index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], "--restore", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Count)
            {
                source = args[++index];
                if (index + 1 < args.Count &&
                    !args[index + 1].StartsWith('-'))
                {
                    destination = args[++index];
                }

                continue;
            }

            if (string.Equals(args[index], "--report", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Count)
            {
                report = args[++index];
            }
        }

        if (string.IsNullOrWhiteSpace(source) ||
            string.IsNullOrWhiteSpace(destination) ||
            string.IsNullOrWhiteSpace(report))
        {
            return false;
        }

        sourceRoot = Path.GetFullPath(source);
        destinationRoot = Path.GetFullPath(destination);
        reportPath = Path.GetFullPath(report);
        return true;
    }
}
