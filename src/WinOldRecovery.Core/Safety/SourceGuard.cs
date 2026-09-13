using System.Collections.Concurrent;
using WinOldRecovery.Core.IO;

namespace WinOldRecovery.Core.Safety;

public sealed class SourceGuard
{
    private readonly ConcurrentDictionary<string, byte> sourceRoots =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> SourceRoots => sourceRoots.Keys.ToArray();

    public string RegisterSourceRoot(string sourceRoot)
    {
        string canonicalRoot = PathCanonicalizer.Canonicalize(sourceRoot);
        sourceRoots.TryAdd(canonicalRoot, 0);
        return canonicalRoot;
    }

    public bool UnregisterSourceRoot(string sourceRoot)
    {
        string canonicalRoot = PathCanonicalizer.Canonicalize(sourceRoot);
        return sourceRoots.TryRemove(canonicalRoot, out _);
    }

    public bool IsSourcePath(string path)
    {
        string canonicalPath = PathCanonicalizer.Canonicalize(path);
        return FindContainingRoot(canonicalPath) is not null;
    }

    public void DemandWriteAllowed(string path, PurgeToken? purgeToken = null)
    {
        string canonicalPath = PathCanonicalizer.Canonicalize(path);
        ValidateWrite(canonicalPath, purgeToken);
    }

    internal string GetValidatedWritePath(string path, PurgeToken? purgeToken)
    {
        string canonicalPath = PathCanonicalizer.Canonicalize(path);
        ValidateWrite(canonicalPath, purgeToken);
        return canonicalPath;
    }

    private void ValidateWrite(string canonicalPath, PurgeToken? purgeToken)
    {
        string? containingRoot = FindContainingRoot(canonicalPath);
        if (containingRoot is null)
        {
            return;
        }

        if (purgeToken is not null &&
            string.Equals(
                purgeToken.CanonicalSourceRoot,
                containingRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new SourceWriteDeniedException(canonicalPath);
    }

    private string? FindContainingRoot(string canonicalPath)
    {
        return sourceRoots.Keys
            .Where(root => Contains(root, canonicalPath))
            .OrderByDescending(static root => root.Length)
            .FirstOrDefault();
    }

    private static bool Contains(string root, string candidate)
    {
        if (string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return candidate.Length > root.Length &&
               candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
               candidate[root.Length] is '\\' or '/';
    }
}
