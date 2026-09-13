using WinOldRecovery.Native;

namespace WinOldRecovery.Core.IO;

public static class PathCanonicalizer
{
    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    public static string Canonicalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string candidate = NormalizeLexically(path);
        Stack<string> missingSegments = new();

        while (true)
        {
            if (NativePath.TryGetFinalPath(candidate, out string resolved))
            {
                while (missingSegments.TryPop(out string? missingSegment))
                {
                    resolved = Path.Combine(resolved, missingSegment);
                }

                return TrimEndingSeparatorUnlessRoot(Path.GetFullPath(resolved));
            }

            string? parent = Path.GetDirectoryName(candidate);
            if (string.IsNullOrEmpty(parent) ||
                string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"No existing ancestor could be resolved for '{path}'.");
            }

            string segment = Path.GetFileName(candidate);
            if (string.IsNullOrEmpty(segment))
            {
                throw new IOException($"Could not resolve '{path}'.");
            }

            missingSegments.Push(segment);
            candidate = parent;
        }
    }

    public static string NormalizeLexically(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (path.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Network paths are not supported in v0.1.");
        }

        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            if (!Path.IsPathFullyQualified(path))
            {
                throw new ArgumentException("The path must be fully qualified.", nameof(path));
            }

            return TrimEndingSeparatorUnlessRoot(Path.GetFullPath(path));
        }

        string regularPath = RemoveExtendedPrefix(path);
        if (!Path.IsPathFullyQualified(regularPath))
        {
            throw new ArgumentException("The path must be fully qualified.", nameof(path));
        }

        if (regularPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new NotSupportedException("Network paths are not supported in v0.1.");
        }

        return TrimEndingSeparatorUnlessRoot(ToExtendedPath(Path.GetFullPath(regularPath)));
    }

    public static string ToExtendedPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            return path;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return ExtendedUncPrefix + path[2..];
        }

        return ExtendedPrefix + path;
    }

    private static string RemoveExtendedPrefix(string path)
    {
        if (path.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[ExtendedUncPrefix.Length..];
        }

        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            return path[ExtendedPrefix.Length..];
        }

        return path;
    }

    private static string TrimEndingSeparatorUnlessRoot(string path)
    {
        string? root = Path.GetPathRoot(path);
        return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            ? path
            : Path.TrimEndingDirectorySeparator(path);
    }
}
