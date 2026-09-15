using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Text.RegularExpressions;

namespace WinOldRecovery.Core.Classification;

internal static partial class RuleGlob
{
    private static readonly ConcurrentDictionary<string, Regex> PathPatterns = new(StringComparer.Ordinal);

    public static bool MatchesName(string name, string glob)
    {
        return FileSystemName.MatchesSimpleExpression(glob, name, ignoreCase: true);
    }

    public static bool MatchesPath(string relPath, string glob)
    {
        string path = relPath.Replace('/', '\\');
        Regex regex = PathPatterns.GetOrAdd(glob.Replace('/', '\\'), CompilePathGlob);
        return regex.IsMatch(path);
    }

    public static bool ContainsSegment(string relPath, string fragment)
    {
        return relPath.Contains(fragment.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
    }

    private static Regex CompilePathGlob(string glob)
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
