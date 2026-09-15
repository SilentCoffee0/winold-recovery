using System.Net;
using System.Text;
using System.Text.Json;
using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.Recipes;

namespace WinOldRecovery.Recipes;

public sealed class ChromiumRecipe : IRecipe
{
    private readonly string product;
    private readonly string relativeUserData;

    public ChromiumRecipe(string id, string product, string relativeUserData)
    {
        Id = id;
        this.product = product;
        this.relativeUserData = relativeUserData;
    }

    public string Id { get; }

    public static bool NewProfileTransplantEnabled { get; set; } =
        string.Equals(
            Environment.GetEnvironmentVariable("WINOLD_RECOVERY_CHROMIUM_TRANSPLANT"),
            "1",
            StringComparison.Ordinal);

    public DetectResult Detect(ProfileContext context)
    {
        string userData = Path.Combine(context.OldProfileRoot, relativeUserData);
        if (!context.SafeFs.DirectoryExists(userData))
        {
            return new DetectResult([], []);
        }

        List<RecipeCard> cards = [];
        List<(string RelativePath, string Kind, string Detail)> badges = [];
        foreach (string entry in context.SafeFs.EnumerateFileSystemEntries(userData))
        {
            string name = Path.GetFileName(entry);
            if (!name.Equals("Default", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string bookmarks = Path.Combine(entry, "Bookmarks");
            if (!context.SafeFs.FileExists(bookmarks))
            {
                continue;
            }

            string bookmarksJson = context.SafeFs.ReadAllText(bookmarks);
            int count = CountBookmarks(bookmarksJson);
            string extensionsHtml = BuildExtensionsHtml(context, Path.Combine(entry, "Extensions"));
            int extensionCount = CountTag(extensionsHtml, "<li>");
            (int historyCount, string historyHtml, string historyCsv) = ReadHistory(context, entry, name);
            (int tabCount, string tabsHtml) = ReadTabs(context, Path.Combine(entry, "Sessions"));
            (int autofillCount, string autofillCsv) = ReadAutofill(context, entry, name);
            List<RecipeComponent> components =
            [
                new RecipeComponent(
                    "bookmarks-export",
                    "Bookmarks HTML export",
                    count + " bookmarks",
                    Decision.Restore,
                    false,
                    null,
                    false),
                new RecipeComponent(
                    "history-export",
                    "History export",
                    historyCount + " URLs",
                    historyCount > 0 ? Decision.Restore : Decision.LeaveBehind,
                    false,
                    null,
                    true),
                new RecipeComponent(
                    "tabs-export",
                    "Open tabs list",
                    tabCount + " tabs",
                    tabCount > 0 ? Decision.Restore : Decision.LeaveBehind,
                    false,
                    null,
                    false),
                new RecipeComponent(
                    "extensions-export",
                    "Extensions list",
                    extensionCount + " extensions",
                    Decision.Restore,
                    false,
                    null,
                    false),
                new RecipeComponent(
                    "autofill-export",
                    "Autofill CSV export",
                    autofillCount + " fields",
                    Decision.Undecided,
                    false,
                    null,
                    true),
                new RecipeComponent(
                    "passwords",
                    "Passwords, cookies and cards",
                    "Cannot be recovered",
                    Decision.Undecided,
                    true,
                    "Chrome and Edge encrypt them with keys that only existed on the old Windows installation.",
                    true),
            ];
            if (NewProfileTransplantEnabled)
            {
                components.Add(
                    new RecipeComponent(
                        "bookmarks-transplant",
                        "Bookmarks file into a new profile folder",
                        "Feature-flagged copy of Bookmarks.json into Recovered-from-Windows.old",
                        Decision.Undecided,
                        false,
                        null,
                        false));
            }

            cards.Add(
                new RecipeCard(
                    Id,
                    product + " — " + name,
                    "Bookmarks, browsing history, the tabs that were open, and the list of installed extensions.",
                    "These are the everyday traces of how you used " + product + ".",
                    "Bookmarks exported as HTML. Passwords, cookies and payment cards cannot be recovered after a clean reinstall.",
                    "Sign in to your Google or Microsoft account if sync was on.",
                    "Bookmarks do not regenerate. Caches do.",
                    "You keep a clean browser but lose local bookmarks that were not synced.",
                    components,
                    context.ProfileName + ":" + name,
                    new Dictionary<string, string>
                    {
                        ["profileDir"] = entry,
                        ["bookmarksJson"] = bookmarksJson,
                        ["count"] = count.ToString(),
                        ["extensions"] = extensionCount.ToString(),
                        ["extensionsHtml"] = extensionsHtml,
                        ["historyHtml"] = historyHtml,
                        ["historyCsv"] = historyCsv,
                        ["tabsHtml"] = tabsHtml,
                        ["autofillCsv"] = autofillCsv,
                    }));
            DetectorWalk.AddTreeBadge(
                badges,
                context.OldProfileRoot,
                entry,
                Id.Equals("edge", StringComparison.Ordinal) ? "Edge" : "Chrome",
                name);
        }

        return new DetectResult(cards, badges);
    }

    public PlanResult Plan(CardDecisions decisions, DestinationContext destination)
    {
        string exportRoot = Path.Combine(
            destination.SessionExportsDirectory,
            Id,
            decisions.Card.InstanceKey.Replace(':', '_'));
        List<RecipeWrite> writes = [];
        if (RecipeDecisions.ShouldRestore(decisions, "bookmarks-export"))
        {
            string html = NetscapeBookmarks(decisions.Card.Facts["bookmarksJson"]);
            string dest = Path.Combine(exportRoot, "bookmarks.html");
            writes.Add(new RecipeWrite(RecipeWriteKind.WriteContent, null, dest, html, html.Length, "bookmarks-export"));
        }

        if (RecipeDecisions.ShouldRestore(decisions, "history-export"))
        {
            writes.Add(
                new RecipeWrite(
                    RecipeWriteKind.WriteContent,
                    null,
                    Path.Combine(exportRoot, "history.html"),
                    decisions.Card.Facts.GetValueOrDefault("historyHtml"),
                    1,
                    "history-export"));
            writes.Add(
                new RecipeWrite(
                    RecipeWriteKind.WriteContent,
                    null,
                    Path.Combine(exportRoot, "history.csv"),
                    decisions.Card.Facts.GetValueOrDefault("historyCsv"),
                    1,
                    "history-export"));
        }

        if (RecipeDecisions.ShouldRestore(decisions, "tabs-export"))
        {
            string html = decisions.Card.Facts.GetValueOrDefault("tabsHtml") ?? "<!DOCTYPE html><title>Open tabs</title>";
            writes.Add(
                new RecipeWrite(
                    RecipeWriteKind.WriteContent,
                    null,
                    Path.Combine(exportRoot, "tabs.html"),
                    html,
                    html.Length,
                    "tabs-export"));
        }

        if (RecipeDecisions.ShouldRestore(decisions, "extensions-export"))
        {
            string html = decisions.Card.Facts.GetValueOrDefault("extensionsHtml") ?? "<!DOCTYPE html><title>Extensions</title>";
            writes.Add(
                new RecipeWrite(
                    RecipeWriteKind.WriteContent,
                    null,
                    Path.Combine(exportRoot, "extensions.html"),
                    html,
                    html.Length,
                    "extensions-export"));
        }

        if (RecipeDecisions.ShouldRestore(decisions, "autofill-export"))
        {
            string csv = decisions.Card.Facts.GetValueOrDefault("autofillCsv") ?? "kind,name,value\n";
            writes.Add(
                new RecipeWrite(
                    RecipeWriteKind.WriteContent,
                    null,
                    Path.Combine(exportRoot, "autofill.csv"),
                    csv,
                    csv.Length,
                    "autofill-export"));
        }

        if (NewProfileTransplantEnabled &&
            RecipeDecisions.ShouldRestore(decisions, "bookmarks-transplant"))
        {
            string source = Path.Combine(decisions.Card.Facts["profileDir"], "Bookmarks");
            string dest = Path.Combine(
                destination.DestinationProfileRoot,
                relativeUserData,
                "Recovered-from-Windows.old",
                "Bookmarks");
            if (destination.SafeFs.FileExists(dest))
            {
                dest = RecipeDecisions.ConflictName(dest);
            }

            writes.Add(new RecipeWrite(RecipeWriteKind.CopyFile, source, dest, null, 1, "bookmarks-transplant"));
        }

        return new PlanResult(decisions.Card, writes);
    }

    public Task ExecuteAsync(PlanResult plan, IRecipeJournal journal, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public RecipeVerifyResult Verify(PlanResult plan)
    {
        bool ok = plan.Writes.All(static write => File.Exists(write.DestinationPath) && new FileInfo(write.DestinationPath).Length > 0);
        return new RecipeVerifyResult(ok, ok ? "Chromium exports present" : "Chromium export missing");
    }

    public IReadOnlyList<Prerequisite> Prerequisites(PlanResult plan) =>
        [new Prerequisite(Id == "edge" ? "msedge" : "chrome", product + " must be closed before a profile transplant.")];

    internal static int CountBookmarks(string json)
    {
        using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        if (!document.RootElement.TryGetProperty("roots", out JsonElement roots))
        {
            return 0;
        }

        int count = 0;
        foreach (JsonProperty property in roots.EnumerateObject())
        {
            count += CountUrls(property.Value);
        }

        return count;
    }

    private (int Count, string Html, string Csv) ReadHistory(ProfileContext context, string profileDir, string profileName)
    {
        string history = Path.Combine(profileDir, "History");
        if (!context.SafeFs.FileExists(history))
        {
            return (0, string.Empty, "url,title,visit_count\n");
        }

        try
        {
            string copy = ReadOnlySqlite.CopyToTemp(
                context.SafeFs,
                history,
                Path.Combine(context.SessionTemporaryDirectory, Id, profileName),
                "History");
            return SqliteExports.ChromiumHistory(copy);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            return (0, string.Empty, "url,title,visit_count\n");
        }
    }

    private (int Count, string Csv) ReadAutofill(ProfileContext context, string profileDir, string profileName)
    {
        string webData = Path.Combine(profileDir, "Web Data");
        if (!context.SafeFs.FileExists(webData))
        {
            return (0, "kind,name,value\n");
        }

        try
        {
            string copy = ReadOnlySqlite.CopyToTemp(
                context.SafeFs,
                webData,
                Path.Combine(context.SessionTemporaryDirectory, Id, profileName),
                "Web Data");
            return SqliteExports.ChromiumAutofill(copy);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            return (0, "kind,name,value\n");
        }
    }

    private static (int Count, string Html) ReadTabs(ProfileContext context, string sessionsDir)
    {
        if (!context.SafeFs.DirectoryExists(sessionsDir))
        {
            return (0, "<!DOCTYPE html><title>Open tabs</title>");
        }

        string? newest = context.SafeFs.EnumerateFileSystemEntries(sessionsDir)
            .Where(static path =>
            {
                string name = Path.GetFileName(path);
                return name.StartsWith("Session_", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Tabs_", StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (newest is null)
        {
            return (0, "<!DOCTYPE html><title>Open tabs</title>");
        }

        IReadOnlyList<string> urls = SnssReader.ReadTabUrls(context.SafeFs.ReadAllBytes(newest));
        StringBuilder html = new();
        html.AppendLine("<!DOCTYPE html><title>Open tabs</title><h1>Reopen these tabs</h1><ul>");
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

    private string BuildExtensionsHtml(ProfileContext context, string extensionsRoot)
    {
        StringBuilder html = new();
        html.AppendLine("<!DOCTYPE html><title>Extensions</title><h1>Extensions</h1><ul>");
        if (!context.SafeFs.DirectoryExists(extensionsRoot))
        {
            html.AppendLine("</ul>");
            return html.ToString();
        }

        string store = Id == "edge"
            ? "https://microsoftedge.microsoft.com/addons/detail/"
            : "https://chromewebstore.google.com/detail/";
        foreach (string idDir in context.SafeFs.EnumerateFileSystemEntries(extensionsRoot))
        {
            if (!context.SafeFs.DirectoryExists(idDir))
            {
                continue;
            }

            string id = Path.GetFileName(idDir);
            string name = id;
            foreach (string versionDir in context.SafeFs.EnumerateFileSystemEntries(idDir))
            {
                string manifest = Path.Combine(versionDir, "manifest.json");
                if (!context.SafeFs.FileExists(manifest))
                {
                    continue;
                }

                try
                {
                    using JsonDocument document = JsonDocument.Parse(context.SafeFs.ReadAllText(manifest));
                    if (document.RootElement.TryGetProperty("name", out JsonElement nameElement) &&
                        nameElement.GetString() is string manifestName &&
                        !manifestName.StartsWith("__MSG_", StringComparison.Ordinal))
                    {
                        name = manifestName;
                    }
                }
                catch (JsonException)
                {
                }

                break;
            }

            html.Append("<li><a href=\"")
                .Append(store)
                .Append(WebUtility.HtmlEncode(id))
                .Append("\">")
                .Append(WebUtility.HtmlEncode(name))
                .Append(" (")
                .Append(WebUtility.HtmlEncode(id))
                .AppendLine(")</a></li>");
        }

        html.AppendLine("</ul>");
        return html.ToString();
    }

    private static int CountTag(string html, string tag)
    {
        int count = 0;
        int index = 0;
        while ((index = html.IndexOf(tag, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += tag.Length;
        }

        return count;
    }

    private static int CountUrls(JsonElement element)
    {
        int count = 0;
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("type", out JsonElement type) &&
            type.GetString() == "url")
        {
            count++;
        }

        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("children", out JsonElement children) &&
            children.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                count += CountUrls(child);
            }
        }

        return count;
    }

    internal static string NetscapeBookmarks(string json)
    {
        StringBuilder builder = new();
        builder.AppendLine("<!DOCTYPE NETSCAPE-Bookmark-file-1>");
        builder.AppendLine("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">");
        builder.AppendLine("<TITLE>Bookmarks</TITLE>");
        builder.AppendLine("<H1>Bookmarks</H1>");
        builder.AppendLine("<DL><p>");
        using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        if (document.RootElement.TryGetProperty("roots", out JsonElement roots))
        {
            foreach (JsonProperty property in roots.EnumerateObject())
            {
                AppendBookmark(property.Value, builder);
            }
        }

        builder.AppendLine("</DL><p>");
        return builder.ToString();
    }

    private static void AppendBookmark(JsonElement element, StringBuilder builder)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (element.TryGetProperty("type", out JsonElement type) && type.GetString() == "url")
        {
            string href = WebUtility.HtmlEncode(element.TryGetProperty("url", out JsonElement url) ? url.GetString() ?? string.Empty : string.Empty);
            string title = WebUtility.HtmlEncode(element.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? href : href);
            builder.Append("<DT><A HREF=\"").Append(href).Append("\">").Append(title).AppendLine("</A>");
        }

        if (element.TryGetProperty("children", out JsonElement children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                AppendBookmark(child, builder);
            }
        }
    }
}
