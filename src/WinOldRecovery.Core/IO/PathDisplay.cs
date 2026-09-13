namespace WinOldRecovery.Core.IO;

public static class PathDisplay
{
    public const int DefaultLimit = 72;

    public static string MiddleEllipsis(string path, int maxChars = DefaultLimit)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 5);

        if (path.Length <= maxChars)
        {
            return path;
        }

        int remaining = maxChars - 3;
        int prefix = remaining / 2;
        int suffix = remaining - prefix;
        return string.Concat(path.AsSpan(0, prefix), "...", path.AsSpan(path.Length - suffix, suffix));
    }
}
