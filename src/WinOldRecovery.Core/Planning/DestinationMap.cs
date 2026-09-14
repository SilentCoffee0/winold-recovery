using System.Text.Json;
using WinOldRecovery.Core.IO;

namespace WinOldRecovery.Core.Planning;

public static class DestinationMap
{
    public const string KvKey = "restore.dest.byRelPath";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static Dictionary<string, string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        Dictionary<string, string>? parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
        return parsed is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
    }

    public static string Format(IReadOnlyDictionary<string, string> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return JsonSerializer.Serialize(map, JsonOptions);
    }

    public static string Resolve(
        string destinationRoot,
        IReadOnlyDictionary<string, string>? byRelPath,
        string relPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentNullException.ThrowIfNull(relPath);

        string? prefix = null;
        string? mapped = null;
        if (byRelPath is not null)
        {
            foreach (KeyValuePair<string, string> entry in byRelPath)
            {
                if (string.IsNullOrWhiteSpace(entry.Key) || string.IsNullOrWhiteSpace(entry.Value))
                {
                    continue;
                }

                if (!IsSelfOrDescendant(relPath, entry.Key))
                {
                    continue;
                }

                if (prefix is null || entry.Key.Length > prefix.Length)
                {
                    prefix = entry.Key;
                    mapped = entry.Value;
                }
            }
        }

        if (mapped is null || prefix is null)
        {
            return string.IsNullOrEmpty(relPath)
                ? destinationRoot
                : Path.Combine(destinationRoot, relPath);
        }

        string rest = relPath.Length == prefix.Length
            ? string.Empty
            : relPath[(prefix.Length + 1)..];
        string canonical = PathCanonicalizer.Canonicalize(mapped);
        return rest.Length == 0 ? canonical : Path.Combine(canonical, rest);
    }

    private static bool IsSelfOrDescendant(string relPath, string prefix)
    {
        if (relPath.Equals(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (relPath.Length <= prefix.Length)
        {
            return false;
        }

        return relPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            relPath[prefix.Length] is '\\' or '/';
    }
}
