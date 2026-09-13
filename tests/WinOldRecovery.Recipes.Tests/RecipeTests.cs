using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Processes;
using WinOldRecovery.Core.Recipes;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Core.Scan;
using WinOldRecovery.Core.Sessions;

namespace WinOldRecovery.Recipes.Tests;

public sealed class RecipeTests
{
    private const string Canary = "WINOLD_RECOVERY_CANARY_DO_NOT_LOG_7F3A91";

    [Fact]
    public async Task SshAndChromeAndFirefoxAndGit_DetectWithoutLeakingCanaries()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        Directory.CreateDirectory(Path.Combine(alice, ".ssh"));
        await File.WriteAllTextAsync(Path.Combine(alice, ".ssh", "id_ed25519"), Canary);
        await File.WriteAllTextAsync(Path.Combine(alice, ".ssh", "id_ed25519.pub"), "ssh-ed25519 FIXTURE");
        string chrome = Path.Combine(alice, "AppData", "Local", "Google", "Chrome", "User Data", "Default");
        Directory.CreateDirectory(Path.Combine(chrome, "Sessions"));
        Directory.CreateDirectory(Path.Combine(chrome, "Extensions", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "1.0"));
        await File.WriteAllTextAsync(
            Path.Combine(chrome, "Bookmarks"),
            """{"roots":{"bookmark_bar":{"children":[{"type":"url","url":"https://example.com"}]}}}""");
        await File.WriteAllTextAsync(Path.Combine(chrome, "Login Data"), Canary);
        await File.WriteAllTextAsync(
            Path.Combine(chrome, "Extensions", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "1.0", "manifest.json"),
            """{"name":"Fixture Extension","version":"1.0"}""");
        WriteSqlite(
            Path.Combine(chrome, "History"),
            """
            CREATE TABLE urls(id INTEGER PRIMARY KEY, url TEXT, title TEXT, visit_count INTEGER, last_visit_time INTEGER, hidden INTEGER);
            INSERT INTO urls(url, title, visit_count, last_visit_time, hidden)
            VALUES ('https://example.com/history', 'History item', 2, 1, 0);
            """);
        await File.WriteAllBytesAsync(
            Path.Combine(chrome, "Sessions", "Session_1"),
            SnssReader.CreateSessionFile(3, [(SnssReader.UpdateTabNavigation, "https://tabs.example/open"u8.ToArray())]));
        string firefox = Path.Combine(alice, "AppData", "Roaming", "Mozilla", "Firefox", "Profiles", "fixture.default");
        Directory.CreateDirectory(firefox);
        await File.WriteAllTextAsync(Path.Combine(firefox, "logins.json"), """{"logins":[{"encryptedUsername":"WINOLD_RECOVERY_CANARY_DO_NOT_LOG_7F3A91"}]}""");
        await File.WriteAllTextAsync(Path.Combine(firefox, "key4.db"), "k");
        WriteSqlite(
            Path.Combine(firefox, "places.sqlite"),
            """
            CREATE TABLE moz_places(id INTEGER PRIMARY KEY, url TEXT, title TEXT, hidden INTEGER DEFAULT 0);
            CREATE TABLE moz_bookmarks(id INTEGER PRIMARY KEY, type INTEGER, fk INTEGER, title TEXT, parent INTEGER);
            INSERT INTO moz_places(id, url, title) VALUES (1, 'https://firefox.example', 'Fx');
            INSERT INTO moz_bookmarks(id, type, fk, title, parent) VALUES (1, 1, 1, 'Fx', 0);
            """);
        Directory.CreateDirectory(Path.Combine(alice, "Projects", "local-repository", ".git"));
        await File.WriteAllTextAsync(
            Path.Combine(alice, "Projects", "local-repository", ".git", "HEAD"),
            "ref: refs/heads/main\n");
        await File.WriteAllTextAsync(Path.Combine(alice, "Projects", "local-repository", "README.md"), "local work");
        await File.WriteAllTextAsync(Path.Combine(alice, ".gitconfig"), "[user]\n\tname = Alice\n[credential]\n\thelper = store\n");

        RecipeHost host = new(context.Database, context.SafeFs, context.Runner, RecipeCatalog.All);
        IReadOnlyList<RecipeCard> cards = await host.DetectAsync(
            "session-1",
            [
                new DetectedProfile(
                    "Alice",
                    "Alice",
                    alice,
                    @"Users\Alice",
                    ProfileKind.Human,
                    null,
                    [],
                    [],
                    []),
            ],
            context.Destination,
            context.Temp,
            context.Exports);

        Assert.Contains(cards, card => card.RecipeId == "ssh");
        Assert.Contains(cards, card => card.RecipeId == "chrome");
        Assert.Contains(cards, card => card.RecipeId == "firefox");
        Assert.Contains(cards, card => card.Title.Contains("Git", StringComparison.Ordinal));
        string dump = string.Join('\n', cards.Select(card => card.Title + card.What + string.Join(';', card.Facts.Values)));
        Assert.DoesNotContain(Canary, dump, StringComparison.Ordinal);
        Assert.Contains(cards.Single(card => card.RecipeId == "chrome").Components, c => c.Fixed && c.Key == "passwords");

        RecipeCard ssh = cards.Single(card => card.RecipeId == "ssh");
        PlanResult sshPlan = host.PlanCard(new SshRecipe(), ssh, Dest(context));
        await host.ExecuteAsync("session-1", new SshRecipe(), sshPlan);
        Assert.True(new SshRecipe().Verify(sshPlan).Ok);
        Assert.True(File.Exists(Path.Combine(context.Destination, ".ssh", "id_ed25519.pub")));
        Assert.Contains(context.Runner.Requests, request => request.FileName == "ssh.exe" && request.Arguments.Contains("-G"));

        RecipeCard chromeCard = cards.Single(card => card.RecipeId == "chrome");
        PlanResult chromePlan = host.PlanCard(
            RecipeCatalog.All.Single(recipe => recipe.Id == "chrome"),
            chromeCard,
            Dest(context));
        await host.ExecuteAsync("session-1", RecipeCatalog.All.Single(recipe => recipe.Id == "chrome"), chromePlan);
        Assert.True(RecipeCatalog.All.Single(recipe => recipe.Id == "chrome").Verify(chromePlan).Ok);
        string bookmarks = await File.ReadAllTextAsync(chromePlan.Writes.Single(write => write.DestinationPath.EndsWith("bookmarks.html", StringComparison.Ordinal)).DestinationPath);
        Assert.Contains("NETSCAPE", bookmarks, StringComparison.Ordinal);
        Assert.Contains("example.com", bookmarks, StringComparison.Ordinal);
        Assert.Contains(
            "example.com/history",
            await File.ReadAllTextAsync(chromePlan.Writes.Single(write => write.DestinationPath.EndsWith("history.csv", StringComparison.Ordinal)).DestinationPath),
            StringComparison.Ordinal);
        Assert.Contains(
            "tabs.example",
            await File.ReadAllTextAsync(chromePlan.Writes.Single(write => write.DestinationPath.EndsWith("tabs.html", StringComparison.Ordinal)).DestinationPath),
            StringComparison.Ordinal);
        Assert.Contains(
            "chromewebstore.google.com/detail/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            await File.ReadAllTextAsync(chromePlan.Writes.Single(write => write.DestinationPath.EndsWith("extensions.html", StringComparison.Ordinal)).DestinationPath),
            StringComparison.Ordinal);

        RecipeCard firefoxCard = cards.Single(card => card.RecipeId == "firefox");
        PlanResult firefoxPlan = host.PlanCard(new FirefoxRecipe(), firefoxCard, Dest(context));
        await host.ExecuteAsync("session-1", new FirefoxRecipe(), firefoxPlan);
        Assert.True(File.Exists(Path.Combine(context.Destination, "AppData", "Roaming", "Mozilla", "Firefox", "Profiles", "fixture.default-recovered", "key4.db")));
        Assert.Contains("fixture.default-recovered", await File.ReadAllTextAsync(Path.Combine(context.Destination, "AppData", "Roaming", "Mozilla", "Firefox", "profiles.ini")), StringComparison.Ordinal);
        Assert.Contains(
            "firefox.example",
            await File.ReadAllTextAsync(firefoxPlan.Writes.Single(write => write.DestinationPath.EndsWith("bookmarks.html", StringComparison.Ordinal)).DestinationPath),
            StringComparison.Ordinal);

        RecipeCard gitConfig = cards.Single(card => card.Title.StartsWith("Git configuration", StringComparison.Ordinal));
        PlanResult gitConfigPlan = host.PlanCard(new GitRecipe(), gitConfig, Dest(context));
        await host.ExecuteAsync("session-1", new GitRecipe(), gitConfigPlan);
        string restoredConfig = await File.ReadAllTextAsync(Path.Combine(context.Destination, ".gitconfig"));
        Assert.Contains("Alice", restoredConfig, StringComparison.Ordinal);
        Assert.DoesNotContain("store", restoredConfig, StringComparison.Ordinal);

        RecipeCard gitRepo = cards.Single(card => card.Title.Contains("repository", StringComparison.Ordinal));
        PlanResult gitRepoPlan = host.PlanCard(new GitRecipe(), gitRepo, Dest(context));
        await host.ExecuteAsync("session-1", new GitRecipe(), gitRepoPlan);
        Assert.True(File.Exists(Path.Combine(context.Destination, "Recovered", "local-repository", "README.md")));
        Assert.True(File.Exists(Path.Combine(context.Destination, "Recovered", "local-repository", ".git", "HEAD")));
    }

    [Fact]
    public void GitScrub_HidesCredentialHelperValues()
    {
        string scrubbed = GitRecipe.Scrub("[credential]\n\thelper = store\n");
        Assert.Contains("***", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("store", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitAnalyze_UsesExactFlagsAndNeverInvokesMissingGitFromThisTest()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        IReadOnlyList<ProcessRequest> requests = GitAnalyze.CreateRequests(
            Path.Combine(context.Source, "repo"),
            context.Destination);
        Assert.Equal(5, requests.Count);
        Assert.All(requests, request =>
        {
            Assert.Equal("git.exe", request.FileName);
            Assert.Contains("--no-optional-locks", request.Arguments);
            Assert.Contains("safe.directory=*", request.Arguments);
            Assert.Equal("0", request.Environment!["GIT_OPTIONAL_LOCKS"]);
            Assert.Equal(context.Destination, request.Environment["HOME"]);
        });
        await GitAnalyze.AnalyzeAsync(context.Runner, Path.Combine(context.Source, "repo"), context.Destination);
        Assert.Equal(5, context.Runner.Requests.Count);
        Assert.Contains(context.Runner.Requests, request => request.Arguments.Contains("status"));
    }

    [Fact]
    public void SnssReader_ExtractsNavigationUrls()
    {
        byte[] session = SnssReader.CreateSessionFile(
            3,
            [(SnssReader.UpdateTabNavigation, "padding https://tabs.example/open more"u8.ToArray())]);
        Assert.Contains("https://tabs.example/open", SnssReader.ReadTabUrls(session));
    }

    private static DestinationContext Dest(RecipeContext context)
    {
        return new DestinationContext(context.Destination, context.Exports, context.SafeFs, context.Runner);
    }

    private static void WriteSqlite(string path, string sql)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class RecipeContext : IAsyncDisposable
    {
        private RecipeContext(string root, SessionDb database, SafeFs safeFs, RecordingRunner runner)
        {
            Root = root;
            Database = database;
            SafeFs = safeFs;
            Runner = runner;
            Source = Path.Combine(root, "Windows.old");
            Destination = Path.Combine(root, "dest-profile");
            Temp = Path.Combine(root, "tmp");
            Exports = Path.Combine(root, "exports");
            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(Destination);
            Directory.CreateDirectory(Temp);
            Directory.CreateDirectory(Exports);
        }

        public string Root { get; }
        public SessionDb Database { get; }
        public SafeFs SafeFs { get; }
        public RecordingRunner Runner { get; }
        public string Source { get; }
        public string Destination { get; }
        public string Temp { get; }
        public string Exports { get; }

        public static async Task<RecipeContext> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), $"WinOldRecovery-Recipe-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            SafeFs safeFs = new(new SourceGuard());
            SessionDb database = await SessionDb.OpenAsync(Path.Combine(root, "session.db"), safeFs);
            await database.CreateSessionAsync(
                new SessionRecord("session-1", DateTimeOffset.UtcNow, "Scanning", "0.1.0"));
            return new RecipeContext(root, database, safeFs, new RecordingRunner());
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }
}
