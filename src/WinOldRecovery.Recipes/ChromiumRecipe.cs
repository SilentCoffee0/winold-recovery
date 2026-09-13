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

    public DetectResult Detect(ProfileContext context)
    {
        string userData = Path.Combine(context.OldProfileRoot, relativeUserData);
        if (!context.SafeFs.DirectoryExists(userData))
        {
            return new DetectResult([], []);
        }

        List<RecipeCard> cards = [];
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
            int extensionCount = CountExtensions(context, Path.Combine(entry, "Extensions"));
            bool hasHistory = context.SafeFs.FileExists(Path.Combine(entry, "History"));
            bool hasSessions = context.SafeFs.DirectoryExists(Path.Combine(entry, "Sessions"));
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
                            hasHistory ? "History database present" : "No History file",
                            hasHistory ? Decision.Restore : Decision.LeaveBehind,
                            false,
                            null,
                            true),
                        new RecipeComponent(
                            "tabs-export",
                            "Open tabs list",
                            hasSessions ? "Session files present" : "No session files",
                            hasSessions ? Decision.Restore : Decision.LeaveBehind,
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
                            "passwords",
                            "Passwords, cookies and cards",
                            "Cannot be recovered",
                            Decision.Undecided,
                            true,
                            "Chrome and Edge encrypt them with keys that only existed on the old Windows installation.",
                            true),
                    ],
                    context.ProfileName + ":" + name,
                    new Dictionary<string, string>
                    {
                        ["profileDir"] = entry,
                        ["bookmarksJson"] = bookmarksJson,
                        ["count"] = count.ToString(),
                        ["extensions"] = extensionCount.ToString(),
                    }));
        }

        return new DetectResult(cards, []);
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
            string source = Path.Combine(decisions.Card.Facts["profileDir"], "History");
            writes.Add(
                new RecipeWrite(
                    RecipeWriteKind.CopyFile,
                    source,
                    Path.Combine(exportRoot, "history.sqlite"),
                    null,
                    1,
                    "history-export"));
        }

        if (RecipeDecisions.ShouldRestore(decisions, "tabs-export"))
        {
            string html = "<!DOCTYPE html><title>Open tabs</title><p>Session files were found. Import bookmarks.html for URLs that were saved as bookmarks.</p>";
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
            string html = "<!DOCTYPE html><title>Extensions</title><p>Count: " +
                WebUtility.HtmlEncode(decisions.Card.Facts.GetValueOrDefault("extensions", "0")) +
                "</p>";
            writes.Add(
                new RecipeWrite(
                    RecipeWriteKind.WriteContent,
                    null,
                    Path.Combine(exportRoot, "extensions.html"),
                    html,
                    html.Length,
                    "extensions-export"));
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

    private static int CountExtensions(ProfileContext context, string extensionsRoot)
    {
        if (!context.SafeFs.DirectoryExists(extensionsRoot))
        {
            return 0;
        }

        int count = 0;
        foreach (string idDir in context.SafeFs.EnumerateFileSystemEntries(extensionsRoot))
        {
            if (context.SafeFs.DirectoryExists(idDir))
            {
                count++;
            }
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
