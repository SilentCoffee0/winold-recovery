namespace WinOldRecovery.Core.Planning;

public static class OverwriteApprovals
{
    public const string KvKey = "overwrite.destinations";

    public static IReadOnlySet<string> Parse(string? stored)
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(stored))
        {
            return paths;
        }

        foreach (string line in stored.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            paths.Add(line.Trim());
        }

        return paths;
    }

    public static string Format(IEnumerable<string> destinations)
    {
        return string.Join('\n', destinations.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public static bool Allows(IReadOnlySet<string> approved, string destinationPath)
    {
        return approved.Contains(destinationPath);
    }
}
