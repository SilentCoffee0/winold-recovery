using System.Net;
using System.Text;
using System.Text.Json;
using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.IO;
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
        ChromiumUserDataMeta localState = ChromiumLocalState.Read(context.SafeFs, userData);
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

            int extensionCount = CountExtensionFolders(
                context,
                Path.Combine(entry, "Extensions"),
                Path.Combine(relativeUserData, name, "Extensions"));
            bool hasHistory = context.SafeFs.FileExists(Path.Combine(entry, "History"));
            bool hasSessions = HasSessionFiles(context.SafeFs, Path.Combine(entry, "Sessions"));
            bool hasAutofill = context.SafeFs.FileExists(Path.Combine(entry, "Web Data"));
            List<RecipeComponent> components =
            [
                new RecipeComponent(
                    "bookmarks-export",
                    "Bookmarks HTML export",
                    "Bookmarks file found",
                    Decision.Restore,
                    false,
                    null,
                    false),
                new RecipeComponent(
                    "history-export",
                    "History export",
                    hasHistory ? "History database found" : "No history file",
                    hasHistory ? Decision.Restore : Decision.LeaveBehind,
                    false,
                    null,
                    true),
                new RecipeComponent(
                    "tabs-export",
                    "Open tabs list",
                    hasSessions ? "Session files found" : "No session file",
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
                    "autofill-export",
                    "Autofill CSV export",
                    hasAutofill ? "Web Data found" : "No autofill file",
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
                (bool allowed, string? reason) = TransplantAvailability(
                    context,
                    localState.BrowserVersion);
                components.Add(
                    new RecipeComponent(
                        "bookmarks-transplant",
                        "Bookmarks file into a new profile folder",
                        allowed
                            ? "New Profile N with Bookmarks; Local State backup then info_cache register"
                            : reason ?? "Disabled",
                        allowed ? Decision.Undecided : Decision.LeaveBehind,
                        !allowed,
                        allowed ? null : reason,
                        false));
            }

            ChromiumProfileLabel label = localState.Profiles.TryGetValue(name, out ChromiumProfileLabel? found)
                ? found
                : new ChromiumProfileLabel(string.Empty, string.Empty);
            string display = string.IsNullOrWhiteSpace(label.DisplayName) ? name : label.DisplayName;
            string title = product + " — " + display;
            if (!string.IsNullOrWhiteSpace(label.Account) &&
                !label.Account.Equals(display, StringComparison.OrdinalIgnoreCase))
            {
                title += " (" + label.Account + ")";
            }

            cards.Add(
                new RecipeCard(
                    Id,
                    title,
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
                        ["extensions"] = extensionCount.ToString(),
                        ["folder"] = name,
                        ["displayName"] = display,
                        ["browserVersion"] = localState.BrowserVersion,
                        ["lastUsed"] = ReadHistoryLastUsed(context.SafeFs, entry),
                        ["historyPresent"] = hasHistory ? "1" : "0",
                        ["autofillPresent"] = hasAutofill ? "1" : "0",
                        ["sessionsPresent"] = hasSessions ? "1" : "0",
                    }));
            DetectorWalk.AddTreeBadge(
                badges,
                context.OldProfileRoot,
                entry,
                Id.Equals("edge", StringComparison.Ordinal) ? "Edge" : "Chrome",
                display);
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
            string html = NetscapeBookmarks(ReadBookmarksJson(destination.SafeFs, decisions.Card.Facts["profileDir"]));
            string dest = Path.Combine(exportRoot, "bookmarks.html");
            writes.Add(new RecipeWrite(RecipeWriteKind.WriteContent, null, dest, html, html.Length, "bookmarks-export"));
        }

        if (RecipeDecisions.ShouldRestore(decisions, "history-export"))
        {
            (int _, string historyHtml, string historyCsv) = ReadHistory(
                destination.SafeFs,
                decisions.Card.Facts["profileDir"],
                WorkDirectory(destination, decisions.Card.Facts["folder"]));
            writes.Add(
                new RecipeWrite(
                    RecipeWriteKind.WriteContent,
                    null,
                    Path.Combine(exportRoot, "history.html"),
                    historyHtml,
                    historyHtml.Length,
                    "history-export"));
            writes.Add(
                new RecipeWrite(
                    RecipeWriteKind.WriteContent,
                    null,
                    Path.Combine(exportRoot, "history.csv"),
                    historyCsv,
                    historyCsv.Length,
                    "history-export"));
        }

        if (RecipeDecisions.ShouldRestore(decisions, "tabs-export"))
        {
            (_, string html) = ReadTabs(
                destination.SafeFs,
                Path.Combine(decisions.Card.Facts["profileDir"], "Sessions"));
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
            string html = BuildExtensionsHtml(
                destination.SafeFs,
                Path.Combine(decisions.Card.Facts["profileDir"], "Extensions"));
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
            (int _, string autofillCsv) = ReadAutofill(
                destination.SafeFs,
                decisions.Card.Facts["profileDir"],
                WorkDirectory(destination, decisions.Card.Facts["folder"]));
            writes.Add(
                new RecipeWrite(
                    RecipeWriteKind.WriteContent,
                    null,
                    Path.Combine(exportRoot, "autofill.csv"),
                    autofillCsv,
                    autofillCsv.Length,
                    "autofill-export"));
        }

        if (NewProfileTransplantEnabled &&
            RecipeDecisions.ShouldRestore(decisions, "bookmarks-transplant") &&
            DestTransplantAllowed(
                destination,
                decisions.Card.Facts.GetValueOrDefault("browserVersion")))
        {
            string userData = Path.Combine(destination.DestinationProfileRoot, relativeUserData);
            string localState = Path.Combine(userData, "Local State");
            string originalJson;
            try
            {
                originalJson = destination.SafeFs.ReadAllText(localState);
            }
            catch (IOException)
            {
                return new PlanResult(decisions.Card, writes);
            }

            string folder = ChromiumLocalState.NextProfileDirectory(destination.SafeFs, userData);
            string backup = Path.Combine(userData, "Local State.winold-bak");
            if (destination.SafeFs.FileExists(backup))
            {
                backup = RecipeDecisions.ConflictName(backup);
            }

            writes.Add(new RecipeWrite(RecipeWriteKind.CopyFile, localState, backup, null, 1, "bookmarks-transplant"));
            writes.Add(
                new RecipeWrite(
                    RecipeWriteKind.CopyFile,
                    Path.Combine(decisions.Card.Facts["profileDir"], "Bookmarks"),
                    Path.Combine(userData, folder, "Bookmarks"),
                    null,
                    1,
                    "bookmarks-transplant"));
            string registered = ChromiumLocalState.RegisterRecoveredProfile(
                originalJson,
                folder,
                decisions.Card.Facts.GetValueOrDefault("displayName") ?? folder);
            writes.Add(
                new RecipeWrite(
                    RecipeWriteKind.WriteContent,
                    null,
                    localState,
                    registered,
                    registered.Length,
                    "bookmarks-transplant"));
        }

        return new PlanResult(decisions.Card, writes);
    }

    public Task ExecuteAsync(PlanResult plan, IRecipeJournal journal, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public RecipeVerifyResult Verify(PlanResult plan)
    {
        if (!plan.Writes.All(static write => File.Exists(write.DestinationPath) && new FileInfo(write.DestinationPath).Length > 0))
        {
            return new RecipeVerifyResult(false, "Chromium export missing");
        }

        foreach (RecipeWrite write in plan.Writes)
        {
            if (write.Kind != RecipeWriteKind.CopyFile ||
                string.IsNullOrEmpty(write.SourcePath) ||
                !Path.GetFileName(write.DestinationPath).Equals("Bookmarks", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(write.SourcePath))
            {
                continue;
            }

            if (new FileInfo(write.DestinationPath).Length != new FileInfo(write.SourcePath).Length)
            {
                return new RecipeVerifyResult(false, "Chromium destination size does not match the source");
            }
        }

        RecipeWrite? localState = plan.Writes.FirstOrDefault(static write =>
            write.ComponentKey == "bookmarks-transplant" &&
            write.Kind == RecipeWriteKind.WriteContent &&
            write.DestinationPath.EndsWith("Local State", StringComparison.OrdinalIgnoreCase));
        if (localState is not null)
        {
            string json = File.ReadAllText(localState.DestinationPath);
            if (!json.Contains("(recovered)", StringComparison.Ordinal))
            {
                return new RecipeVerifyResult(false, "Local State missing recovered profile");
            }

            RecipeWrite? bookmarks = plan.Writes.FirstOrDefault(static write =>
                write.ComponentKey == "bookmarks-transplant" &&
                write.Kind == RecipeWriteKind.CopyFile &&
                write.DestinationPath.EndsWith("Bookmarks", StringComparison.OrdinalIgnoreCase));
            if (bookmarks?.SourcePath is string source &&
                File.Exists(source) &&
                !Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source)))
                    .Equals(
                        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(bookmarks.DestinationPath))),
                        StringComparison.OrdinalIgnoreCase))
            {
                return new RecipeVerifyResult(false, "Transplanted Bookmarks hash does not match the source");
            }
        }

        RecipeWrite? html = plan.Writes.FirstOrDefault(static write =>
            write.ComponentKey == "bookmarks-export" &&
            write.DestinationPath.EndsWith("bookmarks.html", StringComparison.OrdinalIgnoreCase));
        if (html is not null)
        {
            int parsed = CountNetscapeHrefs(File.ReadAllText(html.DestinationPath));
            int expected = CountSourceBookmarks(plan);
            if (expected < 0 || parsed != expected)
            {
                return new RecipeVerifyResult(false, "Bookmark HTML count does not match the source");
            }
        }

        return new RecipeVerifyResult(true, "Chromium exports present");
    }

    public IReadOnlyList<Prerequisite> Prerequisites(PlanResult plan) =>
        [new Prerequisite(Id == "edge" ? "msedge" : "chrome", product + " must be closed before a profile transplant.")];

    private (bool Allowed, string? Reason) TransplantAvailability(ProfileContext context, string sourceVersion)
    {
        return EvaluateTransplant(
            context.SafeFs,
            Path.Combine(context.DestinationProfileRoot, relativeUserData),
            sourceVersion);
    }

    private bool DestTransplantAllowed(DestinationContext destination, string? sourceVersion)
    {
        return EvaluateTransplant(
            destination.SafeFs,
            Path.Combine(destination.DestinationProfileRoot, relativeUserData),
            sourceVersion).Allowed;
    }

    private (bool Allowed, string? Reason) EvaluateTransplant(SafeFs safeFs, string destUserData, string? sourceVersion)
    {
        (bool allowed, string? reason) = EvaluateDestVersion(ReadLastVersion(safeFs, destUserData), sourceVersion);
        if (!allowed)
        {
            return (allowed, reason);
        }

        if (!safeFs.FileExists(Path.Combine(destUserData, "Local State")))
        {
            return (false, product + " has no Local State on this PC.");
        }

        return (true, null);
    }

    private (bool Allowed, string? Reason) EvaluateDestVersion(string destVersion, string? sourceVersion)
    {
        if (string.IsNullOrWhiteSpace(destVersion))
        {
            return (false, product + " is not installed on this PC (no Last Version).");
        }

        if (!string.IsNullOrWhiteSpace(sourceVersion) &&
            CompareChromeVersions(destVersion, sourceVersion) < 0)
        {
            return (
                false,
                "Installed " + product + " " + destVersion + " is older than the source " + sourceVersion + ".");
        }

        return (true, null);
    }

    internal static int CompareChromeVersions(string left, string right)
    {
        return ParseChromeVersion(left).CompareTo(ParseChromeVersion(right));
    }

    private static Version ParseChromeVersion(string text)
    {
        string[] parts = text.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int major = parts.Length > 0 && int.TryParse(parts[0], out int m) ? m : 0;
        int minor = parts.Length > 1 && int.TryParse(parts[1], out int n) ? n : 0;
        int build = parts.Length > 2 && int.TryParse(parts[2], out int b) ? b : 0;
        int revision = parts.Length > 3 && int.TryParse(parts[3], out int r) ? r : 0;
        return new Version(major, minor, build, revision);
    }

    private static string ReadLastVersion(SafeFs safeFs, string userData)
    {
        string path = Path.Combine(userData, "Last Version");
        if (!safeFs.FileExists(path))
        {
            return string.Empty;
        }

        try
        {
            return safeFs.ReadAllText(path).Trim();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static string ReadBookmarksJson(SafeFs safeFs, string profileDir)
    {
        string bookmarks = Path.Combine(profileDir, "Bookmarks");
        if (!safeFs.FileExists(bookmarks))
        {
            return "{}";
        }

        try
        {
            return safeFs.ReadAllText(bookmarks);
        }
        catch (IOException)
        {
            return "{}";
        }
    }

    private static int CountSourceBookmarks(PlanResult plan)
    {
        if (plan.Card.Facts.TryGetValue("profileDir", out string? profileDir) &&
            !string.IsNullOrEmpty(profileDir))
        {
            string bookmarks = Path.Combine(profileDir, "Bookmarks");
            if (File.Exists(bookmarks))
            {
                return CountBookmarks(File.ReadAllText(bookmarks));
            }
        }

        if (int.TryParse(plan.Card.Facts.GetValueOrDefault("count"), out int expected))
        {
            return expected;
        }

        return -1;
    }

    private static int CountExtensionFolders(
        ProfileContext context,
        string extensionsRoot,
        string relativeUnderProfile)
    {
        if (context.Index is { } index)
        {
            return index.CountChildDirectories(relativeUnderProfile);
        }

        if (!context.SafeFs.DirectoryExists(extensionsRoot))
        {
            return 0;
        }

        int count = 0;
        try
        {
            foreach (string entry in context.SafeFs.EnumerateFileSystemEntries(extensionsRoot))
            {
                if (context.SafeFs.DirectoryExists(entry))
                {
                    count++;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        return count;
    }

    private static bool HasSessionFiles(SafeFs safeFs, string sessionsDir)
    {
        if (!safeFs.DirectoryExists(sessionsDir))
        {
            return false;
        }

        try
        {
            foreach (string path in safeFs.EnumerateFileSystemEntries(sessionsDir))
            {
                string name = Path.GetFileName(path);
                if (name.StartsWith("Session_", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Tabs_", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    private static string ReadHistoryLastUsed(SafeFs safeFs, string profileDir)
    {
        string history = Path.Combine(profileDir, "History");
        if (!safeFs.FileExists(history))
        {
            return string.Empty;
        }

        try
        {
            return File.GetLastWriteTimeUtc(history).ToString("O");
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    internal static int CountNetscapeHrefs(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        int count = 0;
        int index = 0;
        while (true)
        {
            int found = html.IndexOf("<A HREF=", index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return count;
            }

            count++;
            index = found + 8;
        }
    }

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

    private static string WorkDirectory(DestinationContext destination, string profileName)
    {
        string root = string.IsNullOrWhiteSpace(destination.SessionTemporaryDirectory)
            ? Path.Combine(destination.SessionExportsDirectory, ".work")
            : destination.SessionTemporaryDirectory;

        return Path.Combine(root, "chromium", profileName);
    }

    private (int Count, string Html, string Csv) ReadHistory(
        SafeFs safeFs,
        string profileDir,
        string tempDirectory)
    {
        string history = Path.Combine(profileDir, "History");
        if (!safeFs.FileExists(history))
        {
            return (0, string.Empty, "url,title,visit_count\n");
        }

        try
        {
            string copy = ReadOnlySqlite.CopyToTemp(safeFs, history, tempDirectory, "History");
            return SqliteExports.ChromiumHistory(copy);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            return (0, string.Empty, "url,title,visit_count\n");
        }
    }

    private (int Count, string Csv) ReadAutofill(
        SafeFs safeFs,
        string profileDir,
        string tempDirectory)
    {
        string webData = Path.Combine(profileDir, "Web Data");
        if (!safeFs.FileExists(webData))
        {
            return (0, "kind,name,value\n");
        }

        try
        {
            string copy = ReadOnlySqlite.CopyToTemp(safeFs, webData, tempDirectory, "Web Data");
            return SqliteExports.ChromiumAutofill(copy);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            return (0, "kind,name,value\n");
        }
    }

    private static (int Count, string Html) ReadTabs(SafeFs safeFs, string sessionsDir)
    {
        if (!safeFs.DirectoryExists(sessionsDir))
        {
            return (0, "<!DOCTYPE html><title>Open tabs</title>");
        }

        string? newest = safeFs.EnumerateFileSystemEntries(sessionsDir)
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

        IReadOnlyList<string> urls = SnssReader.ReadTabUrls(safeFs.ReadAllBytes(newest));
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

    private string BuildExtensionsHtml(SafeFs safeFs, string extensionsRoot)
    {
        StringBuilder html = new();
        html.AppendLine("<!DOCTYPE html><title>Extensions</title><h1>Extensions</h1><ul>");
        if (!safeFs.DirectoryExists(extensionsRoot))
        {
            html.AppendLine("</ul>");
            return html.ToString();
        }

        string store = Id == "edge"
            ? "https://microsoftedge.microsoft.com/addons/detail/"
            : "https://chromewebstore.google.com/detail/";
        foreach (string idDir in safeFs.EnumerateFileSystemEntries(extensionsRoot))
        {
            if (!safeFs.DirectoryExists(idDir))
            {
                continue;
            }

            string id = Path.GetFileName(idDir);
            string name = id;
            foreach (string versionDir in safeFs.EnumerateFileSystemEntries(idDir))
            {
                string manifest = Path.Combine(versionDir, "manifest.json");
                if (!safeFs.FileExists(manifest))
                {
                    continue;
                }

                try
                {
                    using JsonDocument document = JsonDocument.Parse(safeFs.ReadAllText(manifest));
                    if (document.RootElement.TryGetProperty("name", out JsonElement nameElement) &&
                        nameElement.GetString() is string manifestName)
                    {
                        name = ResolveExtensionName(safeFs, versionDir, document.RootElement, manifestName);
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

    private static string ResolveExtensionName(
        SafeFs safeFs,
        string versionDir,
        JsonElement manifest,
        string manifestName)
    {
        if (!manifestName.StartsWith("__MSG_", StringComparison.Ordinal) ||
            !manifestName.EndsWith("__", StringComparison.Ordinal) ||
            manifestName.Length <= 8)
        {
            return manifestName;
        }

        string key = manifestName[6..^2];
        string locale = manifest.TryGetProperty("default_locale", out JsonElement localeElement)
            ? localeElement.GetString() ?? "en"
            : "en";
        foreach (string candidate in new[] { locale, "en", "en_US" })
        {
            string messages = Path.Combine(versionDir, "_locales", candidate, "messages.json");
            if (!safeFs.FileExists(messages))
            {
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(safeFs.ReadAllText(messages));
                if (document.RootElement.TryGetProperty(key, out JsonElement entry) &&
                    entry.TryGetProperty("message", out JsonElement message) &&
                    message.GetString() is string resolved &&
                    !string.IsNullOrWhiteSpace(resolved))
                {
                    return resolved;
                }
            }
            catch (JsonException)
            {
            }
        }

        return manifestName;
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
