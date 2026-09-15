using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Text.RegularExpressions;

namespace WinOldRecovery.Core.Classification;

internal static partial class RuleGlob
{
    private static readonly ConcurrentDictionary<string, CompiledPathGlob> PathPatterns = new(StringComparer.Ordinal);

    public static bool MatchesName(string name, string glob)
    {
        return FileSystemName.MatchesSimpleExpression(glob, name, ignoreCase: true);
    }

    public static bool MatchesPath(string relPath, string glob)
    {
        return GetPathGlob(glob).IsMatch(relPath);
    }

    public static CompiledPathGlob GetPathGlob(string glob)
    {
        return PathPatterns.GetOrAdd(glob, static pattern => new CompiledPathGlob(pattern));
    }

    public static bool ContainsSegment(string relPath, string fragment)
    {
        string needle = fragment.IndexOf('/') >= 0 ? fragment.Replace('/', '\\') : fragment;
        string haystack = relPath.IndexOf('/') >= 0 ? relPath.Replace('/', '\\') : relPath;
        return haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizeRelPath(string relPath)
    {
        return relPath.IndexOf('/') >= 0 ? relPath.Replace('/', '\\') : relPath;
    }
}

internal sealed class CompiledPathGlob
{
    public CompiledPathGlob(string glob)
    {
        string normalized = glob.Replace('/', '\\');
        Regex = Compile(normalized);
        int slash = normalized.LastIndexOf('\\');
        string last = slash < 0 ? normalized : normalized[(slash + 1)..];
        if (last.Length == 0 || last == "*" || last == "**")
        {
            return;
        }

        if (last.Contains('*', StringComparison.Ordinal) || last.Contains('?', StringComparison.Ordinal))
        {
            FileNameGlob = last;
        }
        else
        {
            RequiredSuffix = "\\" + last;
        }
    }

    public Regex Regex { get; }

    public string? FileNameGlob { get; }

    public string? RequiredSuffix { get; }

    public bool IsMatch(string relPath, string? nodeName = null)
    {
        string path = RuleGlob.NormalizeRelPath(relPath);
        if (FileNameGlob is not null)
        {
            string name = nodeName ?? FileNameFrom(path);
            if (!RuleGlob.MatchesName(name, FileNameGlob))
            {
                return false;
            }
        }

        if (RequiredSuffix is not null &&
            !path.EndsWith(RequiredSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Regex.IsMatch(path);
    }

    private static string FileNameFrom(string path)
    {
        int slash = path.LastIndexOf('\\');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private static Regex Compile(string glob)
    {
        string escaped = Regex.Escape(glob)
            .Replace(@"\*\*", "\u0001", StringComparison.Ordinal)
            .Replace(@"\*", @"[^\\]*", StringComparison.Ordinal)
            .Replace(@"\?", @"[^\\]", StringComparison.Ordinal)
            .Replace("\u0001", ".*", StringComparison.Ordinal);
        return new Regex(
            "^" + escaped + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
