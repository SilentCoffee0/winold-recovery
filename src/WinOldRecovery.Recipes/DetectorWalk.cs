using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Recipes;

namespace WinOldRecovery.Recipes;

internal static class DetectorWalk
{
    private static readonly string[] DefaultSkipNames =
    [
        "AppData",
        "node_modules",
        ".git",
        "AppData.old",
    ];

    private static readonly HashSet<string> VendoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules",
        ".cache",
        ".venv",
        "venv",
        "__pycache__",
        ".pytest_cache",
        ".mypy_cache",
        ".tox",
        ".gradle",
        ".parcel-cache",
        ".turbo",
        ".next",
        ".nuxt",
    };

    public static bool IsReparse(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static IEnumerable<string> EnumerateFiles(
        SafeFs safeFs,
        string root,
        int maxDepth,
        params string[] extraSkipDirectoryNames)
    {
        if (!safeFs.DirectoryExists(root) || IsReparse(root))
        {
            yield break;
        }

        HashSet<string> skip = new(DefaultSkipNames, StringComparer.OrdinalIgnoreCase);
        foreach (string name in extraSkipDirectoryNames)
        {
            skip.Add(name);
        }

        Stack<(string Path, int Depth)> stack = new();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            (string directory, int depth) = stack.Pop();
            IReadOnlyList<string> entries;
            try
            {
                entries = safeFs.EnumerateFileSystemEntries(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string entry in entries)
            {
                if (IsReparse(entry))
                {
                    continue;
                }

                bool isDirectory = safeFs.DirectoryExists(entry);
                string name = Path.GetFileName(entry);
                if (isDirectory)
                {
                    if (depth < maxDepth && !skip.Contains(name))
                    {
                        stack.Push((entry, depth + 1));
                    }

                    continue;
                }

                yield return entry;
            }
        }
    }

    public static IEnumerable<string> EnumerateGitWorkingTrees(
        SafeFs safeFs,
        string root,
        int maxDepth)
    {
        if (!safeFs.DirectoryExists(root) || IsReparse(root))
        {
            yield break;
        }

        HashSet<string> skip = new(DefaultSkipNames, StringComparer.OrdinalIgnoreCase);
        skip.Add(".cache");
        Stack<(string Path, int Depth)> stack = new();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            (string directory, int depth) = stack.Pop();
            IReadOnlyList<string> entries;
            try
            {
                entries = safeFs.EnumerateFileSystemEntries(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string entry in entries)
            {
                if (IsReparse(entry))
                {
                    continue;
                }

                string name = Path.GetFileName(entry);
                if (name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                {
                    yield return directory;
                    continue;
                }

                if (!safeFs.DirectoryExists(entry) || depth >= maxDepth)
                {
                    continue;
                }

                if (IsVendoredDirectoryName(name))
                {
                    foreach (string vendored in ProbeVendoredGitRepos(safeFs, entry))
                    {
                        yield return vendored;
                    }

                    continue;
                }

                if (!skip.Contains(name))
                {
                    stack.Push((entry, depth + 1));
                }
            }
        }
    }

    public static IReadOnlyList<string> OutermostDirectories(IReadOnlyList<string> paths)
    {
        List<string> ordered = paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path.Length)
            .ToList();
        List<string> outermost = [];
        foreach (string path in ordered)
        {
            if (outermost.Any(parent =>
                    path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith(parent + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            outermost.Add(path);
        }

        return outermost;
    }

    public static bool IsVendoredGitPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        foreach (string segment in path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsVendoredDirectoryName(segment))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsVendoredDirectoryName(string name) =>
        VendoredDirectoryNames.Contains(name);

    public static bool HasGit(SafeFs safeFs, string directory)
    {
        ArgumentNullException.ThrowIfNull(safeFs);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string git = Path.Combine(directory, ".git");
        return safeFs.DirectoryExists(git) || safeFs.FileExists(git);
    }

    public static IEnumerable<string> ProbeVendoredGitRepos(SafeFs safeFs, string root)
    {
        ArgumentNullException.ThrowIfNull(safeFs);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (IsReparse(root))
        {
            yield break;
        }

        if (HasGit(safeFs, root))
        {
            yield return root;
        }

        foreach (string child in safeFs.EnumerateDirectories(root))
        {
            if (IsReparse(child))
            {
                continue;
            }

            if (HasGit(safeFs, child))
            {
                yield return child;
            }

            string childName = Path.GetFileName(child);
            if (!childName.StartsWith('@'))
            {
                continue;
            }

            foreach (string package in safeFs.EnumerateDirectories(child))
            {
                if (!IsReparse(package) && HasGit(safeFs, package))
                {
                    yield return package;
                }
            }
        }
    }

    public static string StripExtended(string path)
    {
        const string prefix = @"\\?\";
        return path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
    }

    public static string RelativeUnder(string root, string path)
    {
        return Path.GetRelativePath(StripExtended(root), StripExtended(path));
    }

    public static void AddTreeBadge(
        List<(string RelativePath, string Kind, string Detail)> badges,
        string oldProfileRoot,
        string path,
        string kind,
        string detail)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(kind))
        {
            return;
        }

        string relative = RelativeUnder(oldProfileRoot, path);
        if (string.IsNullOrWhiteSpace(relative) ||
            relative is "." or ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            return;
        }

        badges.Add((relative, kind, detail));
    }

    public static void CopyFileKeepBoth(
        SafeFs safeFs,
        string source,
        string destination,
        string component,
        List<RecipeWrite> writes)
    {
        string dest = destination;
        if (safeFs.FileExists(dest) || safeFs.DirectoryExists(dest))
        {
            dest = RecipeDecisions.ConflictName(dest);
        }

        writes.Add(new RecipeWrite(RecipeWriteKind.CopyFile, source, dest, null, 1, component));
    }

    public static RecipeVerifyResult FilesPresent(PlanResult plan, string okDetail, string missingDetail)
    {
        return FilesPresentMatchingSourceLength(plan, okDetail, missingDetail, sizeMismatchDetail: null);
    }

    public static RecipeVerifyResult FilesPresentMatchingSourceLength(
        PlanResult plan,
        string okDetail,
        string missingDetail,
        string? sizeMismatchDetail)
    {
        foreach (RecipeWrite write in plan.Writes)
        {
            if (!File.Exists(write.DestinationPath))
            {
                return new RecipeVerifyResult(false, missingDetail);
            }

            if (sizeMismatchDetail is null ||
                write.Kind != RecipeWriteKind.CopyFile ||
                string.IsNullOrEmpty(write.SourcePath) ||
                !File.Exists(write.SourcePath))
            {
                continue;
            }

            long destinationLength = new FileInfo(write.DestinationPath).Length;
            long sourceLength = new FileInfo(write.SourcePath).Length;
            if (destinationLength != sourceLength)
            {
                return new RecipeVerifyResult(false, sizeMismatchDetail);
            }
        }

        return new RecipeVerifyResult(true, okDetail);
    }
}
