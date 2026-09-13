using System.IO.Enumeration;
using System.Text.RegularExpressions;

namespace WinOldRecovery.Core.Classification;

internal static partial class RuleGlob
{
    public static bool MatchesName(string name, string glob)
    {
        return FileSystemName.MatchesSimpleExpression(glob, name, ignoreCase: true);
    }

    public static bool MatchesPath(string relPath, string glob)
    {
        string path = relPath.Replace('/', '\\');
        string pattern = glob.Replace('/', '\\');
        string escaped = Regex.Escape(pattern)
            .Replace(@"\*\*", "\u0001", StringComparison.Ordinal)
            .Replace(@"\*", @"[^\\]*", StringComparison.Ordinal)
            .Replace(@"\?", @"[^\\]", StringComparison.Ordinal)
            .Replace("\u0001", ".*", StringComparison.Ordinal);
        return Regex.IsMatch(
            path,
            "^" + escaped + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool ContainsSegment(string relPath, string fragment)
    {
        return relPath.Contains(fragment.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
    }
}
