namespace WinOldRecovery.Core.IO;

public static class ExplorerSelect
{
    public const int MaxExplorerPathLength = 259;

    public static string BuildSelectArgument(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string candidate = StripExtendedPrefix(path);
        while (candidate.Length > MaxExplorerPathLength)
        {
            string? parent = Path.GetDirectoryName(candidate);
            if (string.IsNullOrEmpty(parent) ||
                string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            candidate = parent;
        }

        return "/select," + candidate;
    }

    private static string StripExtendedPrefix(string path)
    {
        const string extended = @"\\?\";
        const string extendedUnc = @"\\?\UNC\";
        if (path.StartsWith(extendedUnc, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[extendedUnc.Length..];
        }

        return path.StartsWith(extended, StringComparison.Ordinal)
            ? path[extended.Length..]
            : path;
    }
}
