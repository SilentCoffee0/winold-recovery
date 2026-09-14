using System.Text.Json;

namespace WinOldRecovery.Core.Recipes;

public static class RecipeFolderMap
{
    public const string FactKey = "folderMap";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string KvKey(string instanceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceKey);
        return "recipe.folderMap." + instanceKey;
    }

    public static Dictionary<string, string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        Dictionary<string, string>? parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
        return parsed is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(parsed, StringComparer.Ordinal);
    }

    public static string Format(IReadOnlyDictionary<string, string> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return JsonSerializer.Serialize(map, JsonOptions);
    }
}
