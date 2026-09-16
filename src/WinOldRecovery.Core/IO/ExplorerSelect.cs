namespace WinOldRecovery.Core.IO;

public static class ExplorerSelect
{
    public const int MaxExplorerPathLength = 259;

    public static string BuildSelectArgument(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string candidate = PathCanonicalizer.WithoutExtendedPrefix(path);
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
}
