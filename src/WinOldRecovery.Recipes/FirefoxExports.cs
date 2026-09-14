using System.Net;
using System.Text;
using System.Text.Json;
using WinOldRecovery.Core.IO;

namespace WinOldRecovery.Recipes;

public static class FirefoxExports
{
    public static (int Count, string Html) Tabs(SafeFs safeFs, string profileRoot)
    {
        ArgumentNullException.ThrowIfNull(safeFs);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileRoot);
        string? newest = NewestSession(safeFs, profileRoot);
        if (newest is null)
        {
            return (0, "<!DOCTYPE html><title>Open tabs</title><h1>Open tabs</h1><ul></ul>");
        }

        try
        {
            byte[] raw = safeFs.ReadAllBytes(newest);
            if (!MozLz4.TryDecode(raw, out byte[] utf8))
            {
                return (0, "<!DOCTYPE html><title>Open tabs</title><h1>Open tabs</h1><ul></ul>");
            }

            return TabsFromJson(Encoding.UTF8.GetString(utf8));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return (0, "<!DOCTYPE html><title>Open tabs</title><h1>Open tabs</h1><ul></ul>");
        }
    }

    public static (int Count, string Html) TabsFromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        List<string> urls = [];
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("windows", out JsonElement windows) ||
            windows.ValueKind != JsonValueKind.Array)
        {
            return Html(urls);
        }

        foreach (JsonElement window in windows.EnumerateArray())
        {
            if (!window.TryGetProperty("tabs", out JsonElement tabs) ||
                tabs.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement tab in tabs.EnumerateArray())
            {
                if (urls.Count >= 500)
                {
                    return Html(urls);
                }

                string? url = SelectedUrl(tab);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    urls.Add(url);
                }
            }
        }

        return Html(urls);
    }

    public static (int Count, string Html) Extensions(SafeFs safeFs, string extensionsJsonPath)
    {
        ArgumentNullException.ThrowIfNull(safeFs);
        if (string.IsNullOrWhiteSpace(extensionsJsonPath) || !safeFs.FileExists(extensionsJsonPath))
        {
            return (0, "<!DOCTYPE html><title>Extensions</title><h1>Extensions</h1><ul></ul>");
        }

        try
        {
            return ExtensionsFromJson(safeFs.ReadAllText(extensionsJsonPath));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return (0, "<!DOCTYPE html><title>Extensions</title><h1>Extensions</h1><ul></ul>");
        }
    }

    public static (int Count, string Html) ExtensionsFromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        StringBuilder html = new();
        html.AppendLine("<!DOCTYPE html><title>Extensions</title><h1>Extensions</h1><ul>");
        int count = 0;
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("addons", out JsonElement addons) ||
            addons.ValueKind != JsonValueKind.Array)
        {
            html.AppendLine("</ul>");
            return (0, html.ToString());
        }

        foreach (JsonElement addon in addons.EnumerateArray())
        {
            string type = addon.TryGetProperty("type", out JsonElement typeElement)
                ? typeElement.GetString() ?? string.Empty
                : string.Empty;
            if (!type.Equals("extension", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string location = addon.TryGetProperty("location", out JsonElement locationElement)
                ? locationElement.GetString() ?? string.Empty
                : string.Empty;
            if (location.Equals("app-builtin", StringComparison.OrdinalIgnoreCase) ||
                location.Equals("app-system-defaults", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string id = addon.TryGetProperty("id", out JsonElement idElement)
                ? idElement.GetString() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            string name = id;
            if (addon.TryGetProperty("defaultLocale", out JsonElement locale) &&
                locale.TryGetProperty("name", out JsonElement nameElement) &&
                nameElement.GetString() is string localeName &&
                !string.IsNullOrWhiteSpace(localeName))
            {
                name = localeName;
            }

            html.Append("<li><a href=\"https://addons.mozilla.org/firefox/search/?guid=")
                .Append(Uri.EscapeDataString(id))
                .Append("\">")
                .Append(WebUtility.HtmlEncode(name))
                .Append(" (")
                .Append(WebUtility.HtmlEncode(id))
                .AppendLine(")</a></li>");
            count++;
        }

        html.AppendLine("</ul>");
        return (count, html.ToString());
    }

    private static string? NewestSession(SafeFs safeFs, string profileRoot)
    {
        string[] candidates =
        [
            Path.Combine(profileRoot, "sessionstore.jsonlz4"),
            Path.Combine(profileRoot, "sessionstore-backups", "recovery.jsonlz4"),
            Path.Combine(profileRoot, "sessionstore-backups", "previous.jsonlz4"),
        ];
        string? newest = null;
        DateTime newestTime = DateTime.MinValue;
        foreach (string candidate in candidates)
        {
            if (!safeFs.FileExists(candidate))
            {
                continue;
            }

            DateTime time;
            try
            {
                time = File.GetLastWriteTimeUtc(candidate);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                time = DateTime.MinValue;
            }

            if (newest is null || time >= newestTime)
            {
                newest = candidate;
                newestTime = time;
            }
        }

        return newest;
    }

    private static string? SelectedUrl(JsonElement tab)
    {
        if (!tab.TryGetProperty("entries", out JsonElement entries) ||
            entries.ValueKind != JsonValueKind.Array ||
            entries.GetArrayLength() == 0)
        {
            return null;
        }

        int index = tab.TryGetProperty("index", out JsonElement indexElement) &&
            indexElement.TryGetInt32(out int parsed)
            ? parsed
            : entries.GetArrayLength();
        int offset = Math.Clamp(index - 1, 0, entries.GetArrayLength() - 1);
        JsonElement entry = entries[offset];
        return entry.TryGetProperty("url", out JsonElement url) ? url.GetString() : null;
    }

    private static (int Count, string Html) Html(List<string> urls)
    {
        StringBuilder html = new();
        html.AppendLine("<!DOCTYPE html><title>Open tabs</title><h1>Open tabs</h1><ul>");
        foreach (string url in urls)
        {
            html.Append("<li><a href=\"")
                .Append(WebUtility.HtmlEncode(url))
                .Append("\">")
                .Append(WebUtility.HtmlEncode(url))
                .AppendLine("</a></li>");
        }

        html.AppendLine("</ul>");
        return (urls.Count, html.ToString());
    }
}
