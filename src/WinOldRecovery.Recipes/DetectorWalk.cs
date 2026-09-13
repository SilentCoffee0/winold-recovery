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
                if (isDirectory)
                {
                    if (depth < maxDepth && !skip.Contains(Path.GetFileName(entry)))
                    {
                        stack.Push((entry, depth + 1));
                    }

                    continue;
                }

                yield return entry;
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
        bool ok = plan.Writes.All(static write => File.Exists(write.DestinationPath));
        return new RecipeVerifyResult(ok, ok ? okDetail : missingDetail);
    }
}
