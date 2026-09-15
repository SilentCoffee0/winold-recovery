using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinOldRecovery.Core.IO;

namespace WinOldRecovery.Recipes;

internal static class ChromiumLocalState
{
    public static ChromiumUserDataMeta Read(SafeFs safeFs, string userData)
    {
        string version = string.Empty;
        string lastVersion = Path.Combine(userData, "Last Version");
        if (safeFs.FileExists(lastVersion))
        {
            try
            {
                version = safeFs.ReadAllText(lastVersion).Trim();
            }
            catch (IOException)
            {
            }
        }

        Dictionary<string, ChromiumProfileLabel> profiles = new(StringComparer.OrdinalIgnoreCase);
        string localState = Path.Combine(userData, "Local State");
        if (!safeFs.FileExists(localState))
        {
            return new ChromiumUserDataMeta(version, profiles);
        }

        string json;
        try
        {
            json = safeFs.ReadAllText(localState);
        }
        catch (IOException)
        {
            return new ChromiumUserDataMeta(version, profiles);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("profile", out JsonElement profile) &&
                profile.TryGetProperty("info_cache", out JsonElement cache) &&
                cache.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty entry in cache.EnumerateObject())
                {
                    profiles[entry.Name] = ReadLabel(entry.Value);
                }
            }
        }
        catch (JsonException)
        {
        }

        return new ChromiumUserDataMeta(version, profiles);
    }

    public static string NextProfileDirectory(SafeFs safeFs, string userData)
    {
        for (int n = 1; n < 10_000; n++)
        {
            string name = "Profile " + n;
            if (!safeFs.DirectoryExists(Path.Combine(userData, name)) &&
                !safeFs.FileExists(Path.Combine(userData, name)))
            {
                return name;
            }
        }

        return "Profile 10000";
    }

    public static string RegisterRecoveredProfile(string localStateJson, string folder, string displayName)
    {
        JsonNode root = JsonNode.Parse(string.IsNullOrWhiteSpace(localStateJson) ? "{}" : localStateJson)
            ?? new JsonObject();
        JsonObject profileObject = root["profile"] as JsonObject ?? new JsonObject();
        root["profile"] = profileObject;
        JsonObject cache = profileObject["info_cache"] as JsonObject ?? new JsonObject();
        profileObject["info_cache"] = cache;
        string name = displayName.Trim();
        if (name.Length == 0)
        {
            name = folder;
        }

        if (!name.EndsWith(" (recovered)", StringComparison.Ordinal))
        {
            name += " (recovered)";
        }

        cache[folder] = new JsonObject { ["name"] = name };
        return root.ToJsonString(
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private static ChromiumProfileLabel ReadLabel(JsonElement value)
    {
        string display = ReadString(value, "name");
        string userName = ReadString(value, "user_name");
        string gaia = ReadString(value, "gaia_name");
        string account = !string.IsNullOrWhiteSpace(userName) ? userName : gaia;
        return new ChromiumProfileLabel(display, account);
    }

    private static string ReadString(JsonElement value, string name)
    {
        return value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(name, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }
}

internal sealed record ChromiumUserDataMeta(
    string BrowserVersion,
    IReadOnlyDictionary<string, ChromiumProfileLabel> Profiles);

internal sealed record ChromiumProfileLabel(string DisplayName, string Account);
