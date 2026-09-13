using System.Text;
using System.Text.RegularExpressions;

namespace WinOldRecovery.App.Help;

public static partial class LocalMarkdown
{
    public static string ToDisplayText(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        string withoutLinks = MarkdownLink().Replace(markdown, "$1");
        string[] lines = withoutLinks.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');
        StringBuilder builder = new();
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                builder.AppendLine(line[4..].Trim());
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                builder.AppendLine(line[3..].Trim());
            }
            else if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                builder.AppendLine(line[2..].Trim());
            }
            else
            {
                builder.AppendLine(line.Replace("**", "", StringComparison.Ordinal));
            }
        }

        return builder.ToString().TrimEnd();
    }

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLink();
}
