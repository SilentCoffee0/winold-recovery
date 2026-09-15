using System.Text;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Recipes;
using WinOldRecovery.Core.Registry;

namespace WinOldRecovery.Recipes;

internal static class AnkiBaseDiscovery
{
    public static IEnumerable<string> CandidateBases(ProfileContext context)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Enumerate(context))
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string full = Path.GetFullPath(path);
            if (seen.Add(full))
            {
                yield return full;
            }
        }
    }

    private static IEnumerable<string> Enumerate(ProfileContext context)
    {
        yield return Path.Combine(context.OldProfileRoot, "AppData", "Roaming", "Anki2");
        yield return Path.Combine(context.OldProfileRoot, "Documents", "Anki");
        foreach (string shortcut in ShortcutBases(context))
        {
            yield return shortcut;
        }

        foreach (string hiveBase in HiveBases(context))
        {
            yield return hiveBase;
        }
    }

    private static IEnumerable<string> ShortcutBases(ProfileContext context)
    {
        string programs = Path.Combine(
            context.OldProfileRoot,
            "AppData",
            "Roaming",
            "Microsoft",
            "Windows",
            "Start Menu",
            "Programs");
        if (!context.SafeFs.DirectoryExists(programs))
        {
            yield break;
        }

        foreach (string lnk in DetectorWalk.EnumerateFiles(context.SafeFs, programs, 4))
        {
            if (!lnk.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = context.SafeFs.ReadAllBytes(lnk);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (string path in ReadDashBPaths(bytes))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> HiveBases(ProfileContext context)
    {
        string hivePath = Path.Combine(context.OldProfileRoot, "NTUSER.DAT");
        if (!context.SafeFs.FileExists(hivePath))
        {
            yield break;
        }

        string? expanded = null;
        try
        {
            OfflineRegistryHive hive = OfflineRegistryHive.OpenCopyAsync(
                    hivePath,
                    context.SessionTemporaryDirectory,
                    context.SafeFs)
                .GetAwaiter()
                .GetResult();
            IReadOnlyDictionary<string, string> values = hive.GetStringValues("Environment");
            if (values.TryGetValue("ANKI_BASE", out string? path) && !string.IsNullOrWhiteSpace(path))
            {
                expanded = Environment.ExpandEnvironmentVariables(path);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
        }

        if (!string.IsNullOrWhiteSpace(expanded))
        {
            yield return expanded;
        }
    }

    internal static IReadOnlyList<string> ReadDashBPaths(ReadOnlySpan<byte> bytes)
    {
        List<string> paths = [];
        AddMatches(paths, Encoding.Unicode.GetString(bytes));
        AddMatches(paths, Encoding.ASCII.GetString(bytes));
        return paths;
    }

    private static void AddMatches(List<string> paths, string text)
    {
        int index = 0;
        while (index < text.Length)
        {
            int found = text.IndexOf("-b ", index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return;
            }

            int start = found + 3;
            while (start < text.Length && char.IsWhiteSpace(text[start]))
            {
                start++;
            }

            if (start >= text.Length)
            {
                return;
            }

            string path;
            if (text[start] == '"')
            {
                int end = text.IndexOf('"', start + 1);
                if (end < 0)
                {
                    return;
                }

                path = text[(start + 1)..end];
                index = end + 1;
            }
            else
            {
                int end = start;
                while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != '\0')
                {
                    end++;
                }

                path = text[start..end];
                index = end;
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                paths.Add(path.Trim());
            }
        }
    }
}
