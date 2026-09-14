using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Processes;
using WinOldRecovery.Core.Recipes;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Core.Scan;
using WinOldRecovery.Core.Sessions;
using WinOldRecovery.Core.Verify;
using WinOldRecovery.Recipes;

namespace WinOldRecovery.Recipes.Tests;

public sealed class RecipeTests
{
    private const string Canary = "WINOLD_RECOVERY_CANARY_DO_NOT_LOG_7F3A91";

    [Fact]
    public void RecipeSourcePaths_OpenPath_ReadsTheSourceFact()
    {
        RecipeCard card = new(
            "ssh",
            "SSH",
            "what",
            "why",
            "restore",
            "cloud",
            "regen",
            "leave",
            [],
            "alice",
            new Dictionary<string, string> { ["source"] = @"D:\Windows.old\Users\Alice\.ssh" });

        Assert.Equal(@"D:\Windows.old\Users\Alice\.ssh", RecipeSourcePaths.OpenPath(card));
        Assert.Null(RecipeSourcePaths.OpenPath(card with { Facts = new Dictionary<string, string>() }));
    }

    [Fact]
    public async Task I9_ExecuteWritesOnlyPlannedDestinations()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        Directory.CreateDirectory(Path.Combine(alice, ".ssh"));
        await File.WriteAllTextAsync(Path.Combine(alice, ".ssh", "id_ed25519"), "key");
        await File.WriteAllTextAsync(Path.Combine(alice, ".ssh", "id_ed25519.pub"), "pub");

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

        RecipeCard ssh = cards.Single(card => card.RecipeId == "ssh");
        PlanResult plan = host.PlanCard(new SshRecipe(), ssh, Dest(context));
        Assert.NotEmpty(plan.Writes);
        HashSet<string> planned = plan.Writes
            .Select(write => Path.GetFullPath(write.DestinationPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> before = SnapshotFiles(context.Destination, context.Exports);
        await host.ExecuteAsync("session-1", new SshRecipe(), plan);
        HashSet<string> created = SnapshotFiles(context.Destination, context.Exports);
        created.ExceptWith(before);

        Assert.True(new SshRecipe().Verify(plan).Ok);
        Assert.Equal(planned, created);

        IReadOnlyList<VerifyResultRow> level3 = await host.CollectLevel3Async(
            "session-1",
            "report-ssh",
            cards,
            Dest(context));
        Assert.Contains(level3, row => row.Level == 3 && row.Ok);
        await context.Database.InsertVerifyResultsAsync(level3);
        Assert.True(context.Database.LastVerifyReportAllOk("session-1"));
    }

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
        await File.WriteAllTextAsync(
            Path.Combine(alice, "AppData", "Roaming", "Mozilla", "Firefox", "profiles.ini"),
            """
            [General]
            StartWithLastProfile=1
            Version=2

            [Install308046B0AF4A39CB]
            Default=Profiles/fixture.default
            Locked=1

            [Profile0]
            Name=fixture
            IsRelative=1
            Path=Profiles/fixture.default
            Default=1
            """);
        await File.WriteAllTextAsync(Path.Combine(firefox, "logins.json"), """{"logins":[{"encryptedUsername":"WINOLD_RECOVERY_CANARY_DO_NOT_LOG_7F3A91"}]}""");
        await File.WriteAllTextAsync(Path.Combine(firefox, "key4.db"), "k");
        await File.WriteAllBytesAsync(
            Path.Combine(firefox, "sessionstore.jsonlz4"),
            MozLz4.Encode("""{"windows":[{"tabs":[{"entries":[{"url":"https://tabs.firefox.example/open"}],"index":1}]}]}"""u8));
        await File.WriteAllTextAsync(
            Path.Combine(firefox, "extensions.json"),
            """{"addons":[{"id":"ublock@raymondhill.net","type":"extension","location":"app-profile","defaultLocale":{"name":"uBlock Origin"}}]}""");
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
        await File.WriteAllTextAsync(
            Path.Combine(alice, "Projects", "local-repository", ".git", "config"),
            "[remote \"origin\"]\n\turl = https://user:" + Canary + "@example.invalid/repo.git\n");
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
        Assert.Equal(
            "Firefox — fixture (default)",
            cards.Single(card => card.RecipeId == "firefox").Title);
        Assert.Contains(cards, card => card.Title.Contains("Git", StringComparison.Ordinal));
        string dump = string.Join('\n', cards.Select(card => card.Title + card.What + string.Join(';', card.Facts.Values)));
        Assert.DoesNotContain(Canary, dump, StringComparison.Ordinal);
        Assert.Contains(cards.Single(card => card.RecipeId == "chrome").Components, c => c.Fixed && c.Key == "passwords");

        RecipeCard ssh = cards.Single(card => card.RecipeId == "ssh");
        PlanResult sshPlan = host.PlanCard(new SshRecipe(), ssh, Dest(context));
        await context.Database.SetKvAsync(
            "session-1",
            StoredRecipeDecisions.KvKey(ssh.InstanceKey, "files"),
            nameof(Decision.LeaveBehind));
        PlanResult sshLeft = host.PlanCard(new SshRecipe(), ssh, Dest(context), "session-1");
        Assert.Empty(sshLeft.Writes);
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
        Assert.Contains(
            "tabs.firefox.example",
            await File.ReadAllTextAsync(firefoxPlan.Writes.Single(write => write.DestinationPath.EndsWith("tabs.html", StringComparison.Ordinal)).DestinationPath),
            StringComparison.Ordinal);
        Assert.Contains(
            "addons.mozilla.org/firefox/search/?guid=ublock%40raymondhill.net",
            await File.ReadAllTextAsync(firefoxPlan.Writes.Single(write => write.DestinationPath.EndsWith("extensions.html", StringComparison.Ordinal)).DestinationPath),
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
        string repo = Path.Combine(context.Source, "repo");
        string gitDir = Path.Combine(repo, ".git");
        Directory.CreateDirectory(gitDir);
        await File.WriteAllTextAsync(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/main\n");
        IReadOnlyList<ProcessRequest> requests = GitAnalyze.CreateRequests(
            gitDir,
            repo,
            context.Destination);
        Assert.Equal(5, requests.Count);
        Assert.All(requests, request =>
        {
            Assert.Equal("git.exe", request.FileName);
            Assert.Contains("--no-optional-locks", request.Arguments);
            Assert.Contains("--git-dir", request.Arguments);
            Assert.Contains(gitDir, request.Arguments);
            Assert.Contains("--work-tree", request.Arguments);
            Assert.Contains(repo, request.Arguments);
            Assert.DoesNotContain("-C", request.Arguments);
            Assert.Contains("safe.directory=*", request.Arguments);
            Assert.Equal("0", request.Environment!["GIT_OPTIONAL_LOCKS"]);
            Assert.Equal(context.Destination, request.Environment["HOME"]);
        });
        await GitAnalyze.AnalyzeAsync(
            context.Runner,
            context.SafeFs,
            repo,
            context.Destination,
            context.Temp);
        Assert.Equal(5, context.Runner.Requests.Count);
        Assert.All(
            context.Runner.Requests,
            request =>
            {
                Assert.EndsWith("git.exe", request.FileName, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("--git-dir", request.Arguments);
                Assert.Contains("--work-tree", request.Arguments);
                Assert.Contains(repo, request.Arguments);
                Assert.DoesNotContain("-C", request.Arguments);
            });
        Assert.Contains(
            context.Runner.Requests,
            request => request.Arguments.Any(static argument =>
                argument.Contains("git-analyze", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(context.Runner.Requests, request => request.Arguments.Contains("status"));
    }

    [Fact]
    public async Task GitAnalyze_CopiesGitDirAndLeavesSourceHeadUntouched()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string repo = Path.Combine(context.Source, "repo");
        string head = Path.Combine(repo, ".git", "HEAD");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        await File.WriteAllTextAsync(head, "ref: refs/heads/main\n");
        await GitAnalyze.AnalyzeAsync(
            context.Runner,
            context.SafeFs,
            repo,
            context.Destination,
            context.Temp);
        Assert.Equal("ref: refs/heads/main\n", await File.ReadAllTextAsync(head));
        Assert.DoesNotContain(
            context.Runner.Requests,
            request =>
            {
                int index = request.Arguments.ToList().IndexOf("--git-dir");
                return index >= 0 &&
                    index + 1 < request.Arguments.Count &&
                    request.Arguments[index + 1].Equals(
                        Path.Combine(repo, ".git"),
                        StringComparison.OrdinalIgnoreCase);
            });
        string copiedHead = Directory.GetFiles(context.Temp, "HEAD", SearchOption.AllDirectories).Single();
        Assert.Contains("git-analyze", copiedHead, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ref: refs/heads/main\n", await File.ReadAllTextAsync(copiedHead));
    }

    [Fact]
    public void GitAnalyze_Interpret_CleanPushedWhenARemoteExistsAndTheTreeIsClean()
    {
        GitAnalyzeResult result = GitAnalyze.Interpret(
            "# branch.head main\n# branch.upstream origin/main\n# stash 0\n",
            "main\torigin/main\t",
            string.Empty,
            string.Empty);
        Assert.Equal("Git: clean, pushed", result.Badge);
        Assert.True(result.HasRemote);
        Assert.False(result.Uncommitted);
        Assert.False(result.Unpushed);
        Assert.False(result.Stash);
        Assert.False(result.LocalOnlyBranch);
    }

    [Fact]
    public void GitAnalyze_Interpret_UnpushedCommitsAreLocalOnlyWork()
    {
        GitAnalyzeResult result = GitAnalyze.Interpret(
            "# branch.head main\n# branch.upstream origin/main\n",
            "main\torigin/main\t[ahead 1]",
            "abc123\tWIP\n",
            string.Empty);
        Assert.Equal("Git: local-only work", result.Badge);
        Assert.True(result.Unpushed);
        Assert.True(result.HasRemote);
    }

    [Fact]
    public void GitAnalyze_Interpret_HeadsWithoutUpstreamAreNoRemote()
    {
        GitAnalyzeResult result = GitAnalyze.Interpret(
            "# branch.head main\n",
            "main\t\t",
            string.Empty,
            string.Empty);
        Assert.Equal("Git: no remote", result.Badge);
        Assert.False(result.HasRemote);
        Assert.True(result.LocalOnlyBranch);
    }

    [Fact]
    public void GitAnalyze_Interpret_UnknownAnalysisIsLocalOnlyWork()
    {
        Assert.Equal("Git: local-only work", GitAnalyzeResult.Unknown.Badge);
    }

    [Fact]
    public void GitAnalyze_Interpret_UncommittedUntrackedAndStashAreLocalOnlyWork()
    {
        GitAnalyzeResult result = GitAnalyze.Interpret(
            "1 .M N...\n? scratch.txt\n# stash 2\n# branch.upstream origin/main\n",
            "main\torigin/main\t",
            string.Empty,
            "stash@{0}\tWIP");
        Assert.True(result.Uncommitted);
        Assert.True(result.Untracked);
        Assert.True(result.Stash);
        Assert.Equal("Git: local-only work", result.Badge);
    }

    [Fact]
    public void GitOffline_StripRemoteUrl_RemovesUserInfo()
    {
        Assert.Equal(
            "https://example.invalid/repo.git",
            GitOffline.StripRemoteUrl("https://user:secret@example.invalid/repo.git"));
        Assert.Equal(
            "https://example.invalid/repo.git",
            GitOffline.StripRemoteUrl("https://example.invalid/repo.git"));
    }

    [Fact]
    public async Task GitOffline_Analyze_PackedRefsRemoteAndLocalOnlyBranch()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string gitDir = Path.Combine(context.Source, "packed.git");
        Directory.CreateDirectory(gitDir);
        await File.WriteAllTextAsync(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/main\n");
        await File.WriteAllTextAsync(
            Path.Combine(gitDir, "config"),
            """
            [remote "origin"]
            	url = https://example.invalid/repo.git
            [branch "main"]
            	remote = origin
            	merge = refs/heads/main
            """);
        await File.WriteAllTextAsync(
            Path.Combine(gitDir, "packed-refs"),
            """
            # pack-refs with: peeled
            1111111111111111111111111111111111111111 refs/heads/main
            2222222222222222222222222222222222222222 refs/heads/wip
            3333333333333333333333333333333333333333 refs/remotes/origin/main
            4444444444444444444444444444444444444444 refs/stash
            """);

        GitOfflineResult result = GitOffline.Analyze(context.SafeFs, gitDir);
        Assert.Equal("main", result.CurrentBranch);
        Assert.Contains("main", result.Branches);
        Assert.Contains("wip", result.Branches);
        Assert.True(result.HasRemote);
        Assert.True(result.LocalOnlyBranch);
        Assert.True(result.Stash);
        Assert.Equal("Git: local-only work", result.Badge);
        Assert.Equal("origin", result.Remotes.Single().Name);
        Assert.Equal("https://example.invalid/repo.git", result.Remotes.Single().Url);
    }

    [Fact]
    public async Task GitOffline_Analyze_StripsRemoteCredentialsAndRecordsUnknownWork()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string gitDir = Path.Combine(context.Source, "secret.git");
        Directory.CreateDirectory(Path.Combine(gitDir, "refs", "heads"));
        Directory.CreateDirectory(Path.Combine(gitDir, "logs"));
        await File.WriteAllTextAsync(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/main\n");
        await File.WriteAllTextAsync(
            Path.Combine(gitDir, "refs", "heads", "main"),
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n");
        await File.WriteAllTextAsync(
            Path.Combine(gitDir, "config"),
            "[remote \"origin\"]\n\turl = https://user:" + Canary + "@example.invalid/repo.git\n");
        await File.WriteAllTextAsync(
            Path.Combine(gitDir, "logs", "HEAD"),
            "0000000000000000000000000000000000000000 aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa Alice <a@b.com> 1700000000 +0000\tcommit: init\n");
        await File.WriteAllTextAsync(Path.Combine(gitDir, "index"), "DIRC");

        GitOfflineResult result = GitOffline.Analyze(context.SafeFs, gitDir);
        Assert.DoesNotContain(Canary, result.Remotes.Single().Url, StringComparison.Ordinal);
        Assert.Equal("https://example.invalid/repo.git", result.Remotes.Single().Url);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), result.LastActivity);
        Assert.NotNull(result.IndexMtime);
        Assert.False(result.Reftable);
    }

    [Fact]
    public async Task GitOffline_Analyze_ReftableSkipsLooseHeads()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string gitDir = Path.Combine(context.Source, "reftable.git");
        Directory.CreateDirectory(Path.Combine(gitDir, "refs", "heads"));
        Directory.CreateDirectory(Path.Combine(gitDir, "reftable"));
        await File.WriteAllTextAsync(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/main\n");
        await File.WriteAllTextAsync(
            Path.Combine(gitDir, "refs", "heads", "main"),
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n");
        await File.WriteAllTextAsync(
            Path.Combine(gitDir, "config"),
            "[remote \"origin\"]\n\turl = https://example.invalid/repo.git\n");

        GitOfflineResult result = GitOffline.Analyze(context.SafeFs, gitDir);
        Assert.True(result.Reftable);
        Assert.Empty(result.Branches);
        Assert.False(result.LocalOnlyBranch);
        Assert.True(result.HasRemote);
        Assert.Equal("Git: local-only work", result.Badge);
    }

    [Fact]
    public async Task Git_Detect_UnknownUncommittedUntilGitIsInstalled()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string notes = Path.Combine(alice, "Documents", "notes");
        Directory.CreateDirectory(Path.Combine(notes, ".git", "refs", "heads"));
        await File.WriteAllTextAsync(Path.Combine(notes, ".git", "HEAD"), "ref: refs/heads/main\n");
        await File.WriteAllTextAsync(
            Path.Combine(notes, ".git", "refs", "heads", "main"),
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n");
        await File.WriteAllTextAsync(
            Path.Combine(notes, ".git", "config"),
            "[remote \"origin\"]\n\turl = https://user:" + Canary + "@example.invalid/notes.git\n");

        GitRecipe recipe = new();
        DetectResult detected = recipe.Detect(
            new ProfileContext(
                "Alice",
                alice,
                context.Destination,
                context.Temp,
                context.Exports,
                context.SafeFs,
                context.Runner));
        RecipeCard card = detected.Cards.Single(item => item.Facts.GetValueOrDefault("kind") == "repo");
        Assert.Equal(GitOffline.UnknownUntilGit, card.Facts["uncommitted"]);
        Assert.Equal(GitOffline.UnknownUntilGit, card.Facts["unpushed"]);
        Assert.Equal("Git: local-only work", card.Facts["risk"]);
        Assert.Equal("main", card.Facts["branch"]);
        Assert.Contains("origin=https://example.invalid/notes.git", card.Facts["remotes"], StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, string.Join(';', card.Facts.Values), StringComparison.Ordinal);
        Assert.Contains(
            detected.Badges,
            badge => badge.Kind == "Git" &&
                badge.Detail.Contains("local-only work", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GitAnalyze_CreateVerifyRequests_UsesRevParseAndStatusFlags()
    {
        IReadOnlyList<ProcessRequest> requests = GitAnalyze.CreateVerifyRequests(
            @"D:\Windows.old\Users\Alice\repo",
            @"C:\Users\Alice");
        Assert.Equal(2, requests.Count);
        Assert.Contains("rev-parse", requests[0].Arguments);
        Assert.Contains("HEAD", requests[0].Arguments);
        Assert.Contains("status", requests[1].Arguments);
        Assert.Contains("--porcelain=v2", requests[1].Arguments);
        Assert.Contains("--branch", requests[1].Arguments);
        Assert.Contains("--show-stash", requests[1].Arguments);
        Assert.Contains("--untracked-files=all", requests[1].Arguments);
        Assert.Contains("--ignored=no", requests[1].Arguments);
        Assert.All(
            requests,
            request =>
            {
                Assert.Equal("git.exe", request.FileName);
                Assert.Contains("--no-optional-locks", request.Arguments);
                Assert.Contains("safe.directory=*", request.Arguments);
                Assert.Contains("-C", request.Arguments);
                Assert.Contains(@"D:\Windows.old\Users\Alice\repo", request.Arguments);
                Assert.Equal("0", request.Environment!["GIT_OPTIONAL_LOCKS"]);
                Assert.Equal(@"C:\Users\Alice", request.Environment["HOME"]);
            });
    }

    [Fact]
    public async Task GitAnalyze_CompareRestored_MismatchedHeadFails()
    {
        MappedGitRunner runner = new(
            @"C:\src",
            @"C:\dst",
            "abc123",
            "def456",
            "# branch.head main\n",
            "# branch.head main\n");
        GitLevel3Result result = await GitAnalyze.CompareRestoredAsync(
            runner,
            @"C:\src",
            @"C:\dst",
            @"C:\Users\Alice");
        Assert.True(result.GitAvailable);
        Assert.False(result.HeadMatches);
        Assert.True(result.StatusMatches);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task GitAnalyze_CompareRestored_MissingGitSkipsCompare()
    {
        GitLevel3Result result = await GitAnalyze.CompareRestoredAsync(
            new ExitOneRunner(),
            @"C:\src",
            @"C:\dst",
            @"C:\Users\Alice");
        Assert.False(result.GitAvailable);
        Assert.True(result.Ok);
    }

    [Fact]
    public async Task Git_Verify_MismatchedHeadFileFailsWithoutGit()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        (GitRecipe recipe, PlanResult plan) = await RestoreGitRepoAsync(context);
        Assert.True(recipe.Verify(plan).Ok);
        await File.WriteAllTextAsync(
            Path.Combine(plan.Writes[0].DestinationPath, ".git", "HEAD"),
            "ref: refs/heads/other\n");
        RecipeVerifyResult result = recipe.Verify(plan);
        Assert.False(result.Ok);
        Assert.Contains("HEAD", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Git_VerifyAsync_MissingGitStillOkWhenFilesPresent()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        (GitRecipe recipe, PlanResult plan) = await RestoreGitRepoAsync(context);
        PlanResult withMissingGit = plan with
        {
            Destination = new DestinationContext(
                context.Destination,
                context.Exports,
                context.SafeFs,
                new ExitOneRunner()),
        };
        RecipeVerifyResult result = await recipe.VerifyAsync(withMissingGit);
        Assert.True(result.Ok);
        Assert.Equal("Git files present", result.Detail);
    }

    [Fact]
    public async Task Git_VerifyAsync_StatusMismatchFailsWhenGitIsPresent()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        (GitRecipe recipe, PlanResult plan) = await RestoreGitRepoAsync(context);
        string source = plan.Writes[0].SourcePath!;
        string dest = plan.Writes[0].DestinationPath;
        PlanResult withGit = plan with
        {
            Destination = new DestinationContext(
                context.Destination,
                context.Exports,
                context.SafeFs,
                new MappedGitRunner(
                    source,
                    dest,
                    "abc123",
                    "abc123",
                    "# branch.head main\n",
                    "# branch.head main\n? scratch.txt\n")),
        };
        Assert.True(recipe.Verify(withGit).Ok);
        RecipeVerifyResult result = await recipe.VerifyAsync(withGit);
        Assert.False(result.Ok);
        Assert.Contains("status", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Git_CollectLevel3_FailsWhenRestoredHeadDiffers()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        GitRecipe recipe = new();
        RecipeHost host = new(context.Database, context.SafeFs, context.Runner, [recipe]);
        (_, PlanResult plan) = await RestoreGitRepoAsync(context, host, recipe);
        await File.WriteAllTextAsync(
            Path.Combine(plan.Writes[0].DestinationPath, ".git", "HEAD"),
            "ref: refs/heads/other\n");
        IReadOnlyList<VerifyResultRow> level3 = await host.CollectLevel3Async(
            "session-1",
            "report-git",
            [plan.Card],
            Dest(context));
        Assert.Contains(level3, row => row.Level == 3 && !row.Ok);
        Assert.Contains(
            level3,
            row => row.Detail.Contains("HEAD", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Git_DetectsReposOutsideProjectsAndKeepsOutermostCard()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string notes = Path.Combine(alice, "Documents", "notes");
        Directory.CreateDirectory(Path.Combine(notes, ".git", "refs", "heads"));
        await File.WriteAllTextAsync(Path.Combine(notes, ".git", "HEAD"), "ref: refs/heads/main\n");
        await File.WriteAllTextAsync(
            Path.Combine(notes, ".git", "config"),
            "[core]\n\trepositoryformatversion = 0\n");
        string nested = Path.Combine(notes, "vendor", "lib");
        Directory.CreateDirectory(Path.Combine(nested, ".git"));
        await File.WriteAllTextAsync(Path.Combine(nested, ".git", "HEAD"), "ref: refs/heads/main\n");
        Directory.CreateDirectory(Path.Combine(alice, ".config", "git"));
        await File.WriteAllTextAsync(
            Path.Combine(alice, ".config", "git", "config"),
            "[user]\n\tname = Alice\n");

        GitRecipe recipe = new();
        DetectResult detected = recipe.Detect(
            new ProfileContext(
                "Alice",
                alice,
                context.Destination,
                context.Temp,
                context.Exports,
                context.SafeFs,
                context.Runner));

        Assert.Contains(detected.Cards, card => card.Title.Contains("Git configuration", StringComparison.Ordinal));
        Assert.Contains(detected.Cards, card => card.Title.Contains("notes", StringComparison.Ordinal));
        Assert.DoesNotContain(detected.Cards, card => card.Title.Contains("lib", StringComparison.Ordinal));
        Assert.Contains(
            detected.Badges,
            badge => badge.Kind == "Git" &&
                badge.Detail.Contains("no remote", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Git_DetectPersistsBadgeOntoTheScanNode()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string notes = Path.Combine(alice, "Documents", "notes");
        Directory.CreateDirectory(Path.Combine(notes, ".git"));
        await File.WriteAllTextAsync(Path.Combine(notes, ".git", "HEAD"), "ref: refs/heads/main\n");
        await context.Database.InsertNodesAsync(
        [
            new PersistedNode(
                1,
                "session-1",
                null,
                null,
                "notes",
                @"Users\Alice\Documents\notes",
                NodeKind.Directory,
                0,
                0,
                0,
                DateTime.UtcNow,
                0,
                NodeProblem.None),
        ]);

        RecipeHost host = new(context.Database, context.SafeFs, context.Runner, [new GitRecipe()]);
        await host.DetectAsync(
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

        using SqliteConnection connection = new(
            new SqliteConnectionStringBuilder { DataSource = Path.Combine(context.Root, "session.db") }.ConnectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT kind || ':' || detail FROM badges;";
        string? badge = Convert.ToString(command.ExecuteScalar());
        Assert.Contains("Git:", badge, StringComparison.Ordinal);
        Assert.Contains("no remote", badge, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Git_ConfigPlanCopiesIgnoreAndGlobalIgnore()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        Directory.CreateDirectory(Path.Combine(alice, ".config", "git"));
        await File.WriteAllTextAsync(Path.Combine(alice, ".gitconfig"), "[user]\n\tname = Alice\n");
        await File.WriteAllTextAsync(Path.Combine(alice, ".config", "git", "ignore"), "*.log");
        await File.WriteAllTextAsync(Path.Combine(alice, ".gitignore_global"), "*~");

        GitRecipe recipe = new();
        DetectResult detected = recipe.Detect(
            new ProfileContext(
                "Alice",
                alice,
                context.Destination,
                context.Temp,
                context.Exports,
                context.SafeFs,
                context.Runner));
        RecipeCard config = detected.Cards.Single(card => card.Facts.GetValueOrDefault("kind") == "config");
        PlanResult plan = recipe.Plan(
            new CardDecisions(
                config,
                config.Components.ToDictionary(static component => component.Key, static component => component.SuggestedDefault)),
            Dest(context));

        Assert.Contains(
            plan.Writes,
            write => write.DestinationPath.EndsWith(@"\.config\git\ignore", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            plan.Writes,
            write => write.DestinationPath.EndsWith(".gitignore_global", StringComparison.OrdinalIgnoreCase));

        RecipeHost host = new(context.Database, context.SafeFs, context.Runner, [recipe]);
        await host.ExecuteAsync("session-1", recipe, plan with { Destination = Dest(context) });
        Assert.Equal(
            "*.log",
            await File.ReadAllTextAsync(Path.Combine(context.Destination, ".config", "git", "ignore")));
        Assert.Equal(
            "*~",
            await File.ReadAllTextAsync(Path.Combine(context.Destination, ".gitignore_global")));
    }

    [Fact]
    public void Key4PrimaryPassword_EmptyMasterIsNotSet()
    {
        WithTempKey4(path =>
        {
            Key4Fixture.WritePbes2(path, password: string.Empty);
            Assert.Equal(Key4PrimaryPassword.NotSet, Key4PrimaryPassword.DetectFromCopy(path));
        });
    }

    [Fact]
    public void Key4PrimaryPassword_NonEmptyMasterIsSet()
    {
        WithTempKey4(path =>
        {
            Key4Fixture.WritePbes2(path, password: "x");
            Assert.Equal(Key4PrimaryPassword.Set, Key4PrimaryPassword.DetectFromCopy(path));
        });
    }

    [Fact]
    public void Key4PrimaryPassword_3DesEmptyMasterIsNotSet()
    {
        WithTempKey4(path =>
        {
            Key4Fixture.Write3Des(path, password: string.Empty);
            Assert.Equal(Key4PrimaryPassword.NotSet, Key4PrimaryPassword.DetectFromCopy(path));
        });
    }

    [Fact]
    public void Key4PrimaryPassword_GarbageFileIsUnknown()
    {
        WithTempKey4(path =>
        {
            File.WriteAllText(path, "k");
            Assert.Equal(Key4PrimaryPassword.Unknown, Key4PrimaryPassword.DetectFromCopy(path));
        });
    }

    [Fact]
    public async Task Firefox_Detect_RecordsPrimaryPasswordFromKey4()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string profile = Path.Combine(alice, "AppData", "Roaming", "Mozilla", "Firefox", "Profiles", "pp.default");
        Directory.CreateDirectory(profile);
        await File.WriteAllTextAsync(Path.Combine(profile, "logins.json"), """{"logins":[]}""");
        Key4Fixture.WritePbes2(Path.Combine(profile, "key4.db"), password: string.Empty);

        FirefoxRecipe recipe = new();
        DetectResult detected = recipe.Detect(
            new ProfileContext(
                "Alice",
                alice,
                context.Destination,
                context.Temp,
                context.Exports,
                context.SafeFs,
                context.Runner));
        RecipeCard card = Assert.Single(detected.Cards);
        Assert.Equal(Key4PrimaryPassword.NotSet, card.Facts["primaryPassword"]);
        Assert.Contains("No Primary Password", card.WhatIsRestored, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, string.Join(';', card.Facts.Values), StringComparison.Ordinal);
    }

    [Fact]
    public void MozLz4_RoundtripAndRejectsBadMagic()
    {
        byte[] encoded = MozLz4.Encode("hello-firefox"u8);
        Assert.True(MozLz4.TryDecode(encoded, out byte[] decoded));
        Assert.Equal("hello-firefox"u8.ToArray(), decoded);
        Assert.False(MozLz4.TryDecode("not-mozlz4-data"u8, out _));
    }

    [Fact]
    public void FirefoxExports_TabsUseSelectedEntryAndSkipBuiltinAddons()
    {
        (int tabCount, string tabsHtml) = FirefoxExports.TabsFromJson(
            """
            {"windows":[{"tabs":[
              {"entries":[{"url":"https://first.example"},{"url":"https://selected.example"}],"index":2},
              {"entries":[{"url":"https://only.example"}]}
            ]}]}
            """);
        Assert.Equal(2, tabCount);
        Assert.Contains("selected.example", tabsHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("first.example", tabsHtml, StringComparison.Ordinal);
        Assert.Contains("only.example", tabsHtml, StringComparison.Ordinal);

        (int extensionCount, string extensionsHtml) = FirefoxExports.ExtensionsFromJson(
            """
            {"addons":[
              {"id":"ublock@raymondhill.net","type":"extension","location":"app-profile","defaultLocale":{"name":"uBlock Origin"}},
              {"id":"default-theme@mozilla.org","type":"theme","location":"app-builtin"},
              {"id":"built@mozilla.org","type":"extension","location":"app-builtin"}
            ]}
            """);
        Assert.Equal(1, extensionCount);
        Assert.Contains("addons.mozilla.org/firefox/search/?guid=", extensionsHtml, StringComparison.Ordinal);
        Assert.Contains("uBlock Origin", extensionsHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("built@mozilla.org", extensionsHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FirefoxExports_TabsPreferNewestSessionstore()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string profile = Path.Combine(context.Source, "firefox-profile");
        string backups = Path.Combine(profile, "sessionstore-backups");
        Directory.CreateDirectory(backups);
        string older = Path.Combine(profile, "sessionstore.jsonlz4");
        string newer = Path.Combine(backups, "recovery.jsonlz4");
        await File.WriteAllBytesAsync(
            older,
            MozLz4.Encode("""{"windows":[{"tabs":[{"entries":[{"url":"https://older.firefox.example"}]}]}]}"""u8));
        await File.WriteAllBytesAsync(
            newer,
            MozLz4.Encode("""{"windows":[{"tabs":[{"entries":[{"url":"https://newer.firefox.example"}]}]}]}"""u8));
        File.SetLastWriteTimeUtc(older, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newer, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        (int tabCount, string tabsHtml) = FirefoxExports.Tabs(context.SafeFs, profile);
        Assert.Equal(1, tabCount);
        Assert.Contains("newer.firefox.example", tabsHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("older.firefox.example", tabsHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void FirefoxIni_ParsesProfilesAndInstallDefaults()
    {
        IReadOnlyList<FirefoxProfileRecord> profiles = FirefoxIni.ParseProfiles(
            """
            [General]
            StartWithLastProfile=1

            [InstallABCDEF]
            Default=Profiles/from-install
            Locked=1

            [Profile0]
            Name=work
            IsRelative=1
            Path=Profiles/abcd.work
            Default=1

            [Profile1]
            Name=absolute
            IsRelative=0
            Path=C:\Users\Alice\ff-extra
            """);
        Assert.Equal(2, profiles.Count);
        Assert.Equal("work", profiles[0].Name);
        Assert.True(profiles[0].IsRelative);
        Assert.True(profiles[0].IsDefault);
        Assert.Equal(@"C:\Users\Alice\ff-extra", profiles[1].Path);
        Assert.False(profiles[1].IsRelative);
        Assert.Equal(
            "Profiles/from-install",
            Assert.Single(FirefoxIni.ParseInstallDefaults(
                """
                [ABCDEF]
                Default=Profiles/from-install
                Locked=1

                [General]
                StartWithLastProfile=1
                """)));
    }

    [Fact]
    public async Task Firefox_Detect_UsesIniAbsolutePathShowsEmptyAndIgnoresOutside()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string firefox = Path.Combine(alice, "AppData", "Roaming", "Mozilla", "Firefox");
        Directory.CreateDirectory(firefox);
        string empty = Path.Combine(firefox, "Profiles", "zzzz.empty");
        Directory.CreateDirectory(empty);
        string relocated = Path.Combine(alice, "Documents", "relocated.firefox");
        Directory.CreateDirectory(relocated);
        await File.WriteAllTextAsync(Path.Combine(relocated, "prefs.js"), "user_pref(\"fixture\", true);");
        string outside = Path.Combine(context.Root, "outside.firefox");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "places.sqlite"), "no");
        await File.WriteAllTextAsync(
            Path.Combine(firefox, "profiles.ini"),
            $"""
            [Profile0]
            Name=relocated
            IsRelative=0
            Path={relocated}

            [Profile1]
            Name=empty
            IsRelative=1
            Path=Profiles/zzzz.empty

            [Profile2]
            Name=sneaky
            IsRelative=0
            Path={outside}
            """);
        await File.WriteAllTextAsync(
            Path.Combine(firefox, "installs.ini"),
            """
            [308046B0AF4A39CB]
            Default=Profiles/zzzz.empty
            Locked=1
            """);

        FirefoxRecipe recipe = new();
        DetectResult detected = recipe.Detect(
            new ProfileContext(
                "Alice",
                alice,
                context.Destination,
                context.Temp,
                context.Exports,
                context.SafeFs,
                context.Runner));
        Assert.Equal(2, detected.Cards.Count);
        RecipeCard relocatedCard = detected.Cards.Single(card => card.Facts["name"] == "relocated");
        Assert.Equal(Decision.LeaveBehind, relocatedCard.Components.Single(c => c.Key == "transplant").SuggestedDefault);
        Assert.Contains("prefs.js", relocatedCard.Facts["files"], StringComparison.Ordinal);
        RecipeCard emptyCard = detected.Cards.Single(card => card.Facts["name"] == "empty");
        Assert.Equal("1", emptyCard.Facts["isDefault"]);
        Assert.Equal("Empty profile", emptyCard.Components.Single(c => c.Key == "transplant").Summary);
        Assert.Equal(Decision.LeaveBehind, emptyCard.Components.Single(c => c.Key == "transplant").SuggestedDefault);
        Assert.DoesNotContain(detected.Cards, card => card.Facts["name"] == "sneaky");
        Assert.DoesNotContain(Canary, string.Join(';', detected.Cards.SelectMany(card => card.Facts.Values)), StringComparison.Ordinal);
    }

    [Fact]
    public void SnssReader_ExtractsNavigationUrls()
    {
        byte[] session = SnssReader.CreateSessionFile(
            3,
            [(SnssReader.UpdateTabNavigation, "padding https://tabs.example/open more"u8.ToArray())]);
        Assert.Contains("https://tabs.example/open", SnssReader.ReadTabUrls(session));
    }

    [Fact]
    public async Task Syncthing_PausesFoldersRemapsPathsAndNeverCopiesIndex()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string home = Path.Combine(alice, "AppData", "Local", "Syncthing");
        Directory.CreateDirectory(Path.Combine(home, "index-v2"));
        string certPem = CreateCertificatePem();
        await File.WriteAllTextAsync(Path.Combine(home, "cert.pem"), certPem);
        await File.WriteAllTextAsync(Path.Combine(home, "key.pem"), Canary);
        await File.WriteAllTextAsync(Path.Combine(home, "index-v2", "index.db"), "index-must-not-copy");
        string syncPath = Path.Combine(alice, "Sync");
        await File.WriteAllTextAsync(
            Path.Combine(home, "config.xml"),
            $"""
            <configuration version="37">
              <folder id="default" label="Default Folder" path="{syncPath}" type="sendreceive" paused="false" />
              <folder id="docs" label="Docs" path="{Path.Combine(alice, "Documents")}" paused="false" />
              <folder id="ext" label="External" path="D:\Sync" paused="false" />
              <device id="OTHERDEVICE" name="Peer" />
              <gui>
                <address>127.0.0.1:8384</address>
                <apikey>SUPERSECRETAPIKEY</apikey>
                <password>hashvalue</password>
              </gui>
            </configuration>
            """);

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

        RecipeCard card = Assert.Single(cards, item => item.RecipeId == "syncthing");
        string dump = string.Join(';', card.Facts.Values);
        Assert.DoesNotContain(Canary, dump, StringComparison.Ordinal);
        Assert.DoesNotContain("SUPERSECRETAPIKEY", dump, StringComparison.Ordinal);
        Assert.Contains("***", dump, StringComparison.Ordinal);
        Assert.Equal(SyncthingDeviceId.FromCertificatePem(certPem), card.Facts["deviceId"]);
        Assert.Contains(card.Components, component => component.Fixed && component.Key == "index");

        PlanResult plan = host.PlanCard(new SyncthingRecipe(), card, Dest(context));
        Assert.DoesNotContain(plan.Writes, write => write.DestinationPath.Contains("index", StringComparison.OrdinalIgnoreCase));
        await host.ExecuteAsync("session-1", new SyncthingRecipe(), plan);
        Assert.True(new SyncthingRecipe().Verify(plan).Ok);

        string destConfig = await File.ReadAllTextAsync(Path.Combine(context.Destination, "AppData", "Local", "Syncthing", "config.xml"));
        Assert.True(SyncthingConfig.AllFoldersPaused(destConfig));
        Assert.Contains(Path.Combine(context.Destination, "Sync"), destConfig, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"D:\Sync", destConfig, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(context.Destination, "AppData", "Local", "Syncthing", "key.pem")));
        Assert.False(Directory.Exists(Path.Combine(context.Destination, "AppData", "Local", "Syncthing", "index-v2")));
        Assert.Contains("SUPERSECRETAPIKEY", destConfig, StringComparison.Ordinal);
    }

    [Fact]
    public void SyncthingConfig_Rewrite_AppliesFolderPathOverridesAndPausesDefaults()
    {
        string alice = @"C:\Users\Alice";
        string dest = @"C:\Users\New";
        string xml =
            $"""
            <configuration version="37">
              <folder id="default" label="Default Folder" path="{Path.Combine(alice, "Sync")}" paused="false" />
              <folder id="ext" label="External" path="D:\Sync" paused="false" />
              <defaults>
                <folder path="{Path.Combine(alice, "Sync")}" />
              </defaults>
            </configuration>
            """;

        string rewritten = SyncthingConfig.Rewrite(
            xml,
            alice,
            dest,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["ext"] = @"E:\Moved" });

        Assert.True(SyncthingConfig.AllFoldersPaused(rewritten));
        Assert.Contains(Path.Combine(dest, "Sync"), rewritten, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"E:\Moved", rewritten, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"D:\Sync", rewritten, StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<SyncthingFolderMapping> mappings = SyncthingConfig.PlanMappings(
            xml,
            alice,
            dest,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["ext"] = @"E:\Moved" });
        Assert.Equal(2, mappings.Count);
        Assert.Equal(@"E:\Moved", mappings.Single(item => item.Id == "ext").PlannedPath);
    }

    [Fact]
    public async Task Syncthing_PlanCardReadsPersistedFolderMap()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string home = Path.Combine(alice, "AppData", "Local", "Syncthing");
        Directory.CreateDirectory(home);
        await File.WriteAllTextAsync(Path.Combine(home, "cert.pem"), CreateCertificatePem());
        await File.WriteAllTextAsync(Path.Combine(home, "key.pem"), Canary);
        await File.WriteAllTextAsync(
            Path.Combine(home, "config.xml"),
            $"""
            <configuration version="37">
              <folder id="default" path="{Path.Combine(alice, "Sync")}" paused="false" />
              <folder id="ext" path="D:\Sync" paused="false" />
            </configuration>
            """);

        RecipeHost host = new(context.Database, context.SafeFs, context.Runner, [new SyncthingRecipe()]);
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
        RecipeCard card = Assert.Single(cards);
        string moved = Path.Combine(context.Destination, "ExternalSync");
        await context.Database.SetKvAsync(
            "session-1",
            RecipeFolderMap.KvKey(card.InstanceKey),
            RecipeFolderMap.Format(new Dictionary<string, string> { ["ext"] = moved }));

        PlanResult plan = host.PlanCard(new SyncthingRecipe(), card, Dest(context), "session-1");
        RecipeWrite config = Assert.Single(
            plan.Writes,
            write => write.DestinationPath.EndsWith("config.xml", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(moved, config.Utf8Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"D:\Sync", config.Utf8Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnkiWslAndGpg_RestoreWithoutTrashIndexOrRandomSeed()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string anki = Path.Combine(alice, "AppData", "Roaming", "Anki2", "User 1");
        Directory.CreateDirectory(Path.Combine(anki, "collection.media", "media.trash"));
        WriteSqlite(
            Path.Combine(anki, "collection.anki2"),
            """
            CREATE TABLE notes(id INTEGER PRIMARY KEY, guid TEXT);
            CREATE TABLE cards(id INTEGER PRIMARY KEY, nid INTEGER);
            INSERT INTO notes(guid) VALUES ('note-1');
            INSERT INTO cards(nid) VALUES (1);
            """);
        await File.WriteAllTextAsync(Path.Combine(anki, "collection.anki2-wal"), "wal-must-copy");
        await File.WriteAllTextAsync(Path.Combine(anki, "collection.media", "image.png"), "media");
        await File.WriteAllTextAsync(Path.Combine(anki, "collection.media", "media.trash", "gone.png"), "trash");
        await File.WriteAllTextAsync(Path.Combine(anki, "collection.media.db2"), "regenerate");

        string wsl = Path.Combine(alice, "AppData", "Local", "wsl", "{guid}", "ext4.vhdx");
        Directory.CreateDirectory(Path.GetDirectoryName(wsl)!);
        byte[] vhdx = new byte[512];
        System.Text.Encoding.ASCII.GetBytes("vhdxfile").CopyTo(vhdx, 0);
        await File.WriteAllBytesAsync(wsl, vhdx);

        string gpg = Path.Combine(alice, "AppData", "Roaming", "gnupg");
        Directory.CreateDirectory(Path.Combine(gpg, "private-keys-v1.d"));
        await File.WriteAllTextAsync(Path.Combine(gpg, "pubring.kbx"), "pub");
        await File.WriteAllTextAsync(Path.Combine(gpg, "private-keys-v1.d", "key"), Canary);
        await File.WriteAllTextAsync(Path.Combine(gpg, "random_seed"), "seed");

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

        string dump = string.Join('\n', cards.Select(card => string.Join(';', card.Facts.Values)));
        Assert.DoesNotContain(Canary, dump, StringComparison.Ordinal);

        RecipeCard ankiCard = Assert.Single(cards, card => card.RecipeId == "anki");
        Assert.Equal("1", ankiCard.Facts["notes"]);
        PlanResult ankiPlan = host.PlanCard(new AnkiRecipe(), ankiCard, Dest(context));
        await host.ExecuteAsync("session-1", new AnkiRecipe(), ankiPlan);
        Assert.True(new AnkiRecipe().Verify(ankiPlan).Ok);
        string destAnki = Path.Combine(context.Destination, "AppData", "Roaming", "Anki2", "User 1");
        Assert.True(File.Exists(Path.Combine(destAnki, "collection.anki2-wal")));
        Assert.True(File.Exists(Path.Combine(destAnki, "collection.media", "image.png")));
        Assert.False(File.Exists(Path.Combine(destAnki, "collection.media.db2")));
        Assert.False(Directory.Exists(Path.Combine(destAnki, "collection.media", "media.trash")));

        RecipeCard wslCard = Assert.Single(cards, card => card.RecipeId == "wsl");
        PlanResult wslPlan = host.PlanCard(new WslRecipe(), wslCard, Dest(context));
        await host.ExecuteAsync("session-1", new WslRecipe(), wslPlan);
        Assert.True(new WslRecipe().Verify(wslPlan).Ok);
        Assert.DoesNotContain(context.Runner.Requests, request => request.FileName.Equals("wsl.exe", StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<WinOldRecovery.Core.Processes.ProcessRequest> register = WslRecipe.CreateRegisterRequests("Ubuntu", "C:\\tmp\\ext4.vhdx");
        Assert.Equal("wsl.exe", register[0].FileName);
        Assert.Contains("--import-in-place", register[1].Arguments);
        Assert.Contains("C:\\tmp\\ext4.vhdx", register[1].Arguments);

        RecipeCard gpgCard = Assert.Single(cards, card => card.RecipeId == "gpg");
        PlanResult gpgPlan = host.PlanCard(new GpgRecipe(), gpgCard, Dest(context));
        await host.ExecuteAsync("session-1", new GpgRecipe(), gpgPlan);
        Assert.True(new GpgRecipe().Verify(gpgPlan).Ok);
        Assert.True(File.Exists(Path.Combine(context.Destination, "AppData", "Roaming", "gnupg", "private-keys-v1.d", "key")));
        Assert.False(File.Exists(Path.Combine(context.Destination, "AppData", "Roaming", "gnupg", "random_seed")));
        Assert.Contains(context.Runner.Requests, request => request.FileName == "gpg.exe");
    }

    [Fact]
    public async Task Chromium_AutofillCsvOmitsPaymentCardsAndTransplantStaysFlagged()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string chrome = Path.Combine(alice, "AppData", "Local", "Google", "Chrome", "User Data", "Default");
        Directory.CreateDirectory(chrome);
        await File.WriteAllTextAsync(
            Path.Combine(chrome, "Bookmarks"),
            """{"roots":{"bookmark_bar":{"children":[{"type":"url","url":"https://example.com"}]}}}""");
        WriteSqlite(
            Path.Combine(chrome, "Web Data"),
            """
            CREATE TABLE autofill(name TEXT, value TEXT);
            CREATE TABLE credit_cards(name_on_card TEXT, card_number_encrypted TEXT);
            INSERT INTO autofill(name, value) VALUES ('name', 'Alice Fixture');
            INSERT INTO credit_cards(name_on_card, card_number_encrypted) VALUES ('CANARY', 'WINOLD_RECOVERY_CANARY_DO_NOT_LOG_7F3A91');
            """);

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

        RecipeCard chromeCard = cards.Single(card => card.RecipeId == "chrome");
        string dump = string.Join(';', chromeCard.Facts.Values);
        Assert.DoesNotContain(Canary, dump, StringComparison.Ordinal);
        Assert.Contains("Alice Fixture", chromeCard.Facts["autofillCsv"], StringComparison.Ordinal);
        Assert.DoesNotContain(chromeCard.Components, component => component.Key == "bookmarks-transplant");

        ChromiumRecipe recipe = (ChromiumRecipe)RecipeCatalog.All.Single(item => item.Id == "chrome");
        Dictionary<string, Decision> decisions = chromeCard.Components.ToDictionary(
            static component => component.Key,
            static component => component.Key == "autofill-export" ? Decision.Restore : component.SuggestedDefault);
        PlanResult autofillPlan = recipe.Plan(new CardDecisions(chromeCard, decisions), Dest(context));
        await host.ExecuteAsync("session-1", recipe, autofillPlan with { Destination = Dest(context) });
        string csv = await File.ReadAllTextAsync(
            autofillPlan.Writes.Single(write => write.DestinationPath.EndsWith("autofill.csv", StringComparison.Ordinal)).DestinationPath);
        Assert.Contains("Alice Fixture", csv, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, csv, StringComparison.Ordinal);

        ChromiumRecipe.NewProfileTransplantEnabled = true;
        try
        {
            IReadOnlyList<RecipeCard> flagged = await host.DetectAsync(
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
            RecipeCard flaggedCard = flagged.Single(card => card.RecipeId == "chrome");
            Assert.Contains(flaggedCard.Components, component => component.Key == "bookmarks-transplant");
            Dictionary<string, Decision> transplant = flaggedCard.Components.ToDictionary(
                static component => component.Key,
                static component => component.Key == "bookmarks-transplant" ? Decision.Restore : Decision.Undecided);
            PlanResult transplantPlan = recipe.Plan(new CardDecisions(flaggedCard, transplant), Dest(context));
            await host.ExecuteAsync("session-1", recipe, transplantPlan with { Destination = Dest(context) });
            Assert.True(
                File.Exists(
                    Path.Combine(
                        context.Destination,
                        "AppData",
                        "Local",
                        "Google",
                        "Chrome",
                        "User Data",
                        "Recovered-from-Windows.old",
                        "Bookmarks")));
        }
        finally
        {
            ChromiumRecipe.NewProfileTransplantEnabled = false;
        }
    }

    [Fact]
    public async Task HighValueDetectors_CopyKeePassVsCodeThunderbirdTerminalObsidianAndOutlookPst()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        Directory.CreateDirectory(Path.Combine(alice, "Documents", "Passwords"));
        await File.WriteAllTextAsync(Path.Combine(alice, "Documents", "Passwords", "fixture.kdbx"), Canary);
        await File.WriteAllTextAsync(Path.Combine(alice, "Documents", "Passwords", "fixture.keyx"), "key");
        Directory.CreateDirectory(Path.Combine(alice, "AppData", "Roaming", "Code", "User", "snippets"));
        await File.WriteAllTextAsync(
            Path.Combine(alice, "AppData", "Roaming", "Code", "User", "settings.json"),
            """{"editor.fontSize":14}""");
        await File.WriteAllTextAsync(
            Path.Combine(alice, "AppData", "Roaming", "Code", "User", "snippets", "csharp.json"),
            "{}");
        Directory.CreateDirectory(Path.Combine(alice, ".vscode", "extensions"));
        await File.WriteAllTextAsync(
            Path.Combine(alice, ".vscode", "extensions", "extensions.json"),
            """[{"identifier":{"id":"ms-python.python"}}]""");
        string thunder = Path.Combine(alice, "AppData", "Roaming", "Thunderbird", "Profiles", "mail.default");
        Directory.CreateDirectory(Path.Combine(thunder, "Mail", "Local Folders"));
        await File.WriteAllTextAsync(Path.Combine(thunder, "prefs.js"), "user_pref(\"test\",1);");
        await File.WriteAllTextAsync(Path.Combine(thunder, "key4.db"), Canary);
        await File.WriteAllTextAsync(Path.Combine(thunder, "Mail", "Local Folders", "Inbox"), "mail");
        await File.WriteAllTextAsync(Path.Combine(thunder, "panacea.dat"), "regen");
        Directory.CreateDirectory(
            Path.Combine(
                alice,
                "AppData",
                "Local",
                "Packages",
                "Microsoft.WindowsTerminal_8wekyb3d8bbwe",
                "LocalState"));
        await File.WriteAllTextAsync(
            Path.Combine(
                alice,
                "AppData",
                "Local",
                "Packages",
                "Microsoft.WindowsTerminal_8wekyb3d8bbwe",
                "LocalState",
                "settings.json"),
            """{"profiles":{}}""");
        Directory.CreateDirectory(Path.Combine(alice, "Documents", "Notes", ".obsidian"));
        await File.WriteAllTextAsync(Path.Combine(alice, "Documents", "Notes", "welcome.md"), "hello");
        Directory.CreateDirectory(Path.Combine(alice, "Documents", "Outlook Files"));
        await File.WriteAllTextAsync(Path.Combine(alice, "Documents", "Outlook Files", "archive.pst"), "pst");
        Directory.CreateDirectory(Path.Combine(alice, "AppData", "Local", "Microsoft", "Outlook"));
        await File.WriteAllTextAsync(Path.Combine(alice, "AppData", "Local", "Microsoft", "Outlook", "user.ost"), "ost");
        Directory.CreateDirectory(Path.Combine(context.Destination, "AppData", "Local", "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState"));
        await File.WriteAllTextAsync(
            Path.Combine(context.Destination, "AppData", "Local", "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.json"),
            """{"existing":true}""");

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

        string dump = string.Join('\n', cards.Select(card => card.Title + string.Join(';', card.Facts.Values)));
        Assert.DoesNotContain(Canary, dump, StringComparison.Ordinal);

        RecipeCard keepass = Assert.Single(cards, card => card.RecipeId == "keepass");
        PlanResult keepassPlan = host.PlanCard(new KeePassRecipe(), keepass, Dest(context));
        await host.ExecuteAsync("session-1", new KeePassRecipe(), keepassPlan);
        Assert.True(new KeePassRecipe().Verify(keepassPlan).Ok);
        Assert.True(File.Exists(Path.Combine(context.Destination, "Documents", "Passwords", "fixture.kdbx")));
        Assert.True(File.Exists(Path.Combine(context.Destination, "Documents", "Passwords", "fixture.keyx")));

        RecipeCard vscode = Assert.Single(cards, card => card.RecipeId == "vscode");
        PlanResult vscodePlan = host.PlanCard(new VsCodeRecipe(), vscode, Dest(context));
        await host.ExecuteAsync("session-1", new VsCodeRecipe(), vscodePlan);
        Assert.True(new VsCodeRecipe().Verify(vscodePlan).Ok);
        Assert.True(File.Exists(Path.Combine(context.Destination, "AppData", "Roaming", "Code", "User", "settings.json")));
        string cmd = await File.ReadAllTextAsync(Path.Combine(context.Exports, "install-extensions.cmd"));
        Assert.Contains("code --install-extension ms-python.python", cmd, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, cmd, StringComparison.Ordinal);

        RecipeCard thunderbird = Assert.Single(cards, card => card.RecipeId == "thunderbird");
        PlanResult thunderPlan = host.PlanCard(new ThunderbirdRecipe(), thunderbird, Dest(context));
        await host.ExecuteAsync("session-1", new ThunderbirdRecipe(), thunderPlan);
        Assert.True(new ThunderbirdRecipe().Verify(thunderPlan).Ok);
        Assert.True(File.Exists(Path.Combine(context.Destination, "AppData", "Roaming", "Thunderbird", "Profiles", "mail.default-recovered", "key4.db")));
        Assert.True(File.Exists(Path.Combine(context.Destination, "AppData", "Roaming", "Thunderbird", "Profiles", "mail.default-recovered", "Mail", "Local Folders", "Inbox")));
        Assert.False(File.Exists(Path.Combine(context.Destination, "AppData", "Roaming", "Thunderbird", "Profiles", "mail.default-recovered", "panacea.dat")));

        RecipeCard terminal = Assert.Single(cards, card => card.RecipeId == "windows-terminal");
        PlanResult terminalPlan = host.PlanCard(new TerminalRecipe(), terminal, Dest(context));
        await host.ExecuteAsync("session-1", new TerminalRecipe(), terminalPlan);
        Assert.True(new TerminalRecipe().Verify(terminalPlan).Ok);
        Assert.True(File.Exists(Path.Combine(context.Destination, "AppData", "Local", "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.from-windows-old.json")));
        Assert.Contains("existing", await File.ReadAllTextAsync(Path.Combine(context.Destination, "AppData", "Local", "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.json")), StringComparison.Ordinal);

        RecipeCard obsidian = Assert.Single(cards, card => card.RecipeId == "obsidian");
        PlanResult obsidianPlan = host.PlanCard(new ObsidianRecipe(), obsidian, Dest(context));
        await host.ExecuteAsync("session-1", new ObsidianRecipe(), obsidianPlan);
        Assert.True(new ObsidianRecipe().Verify(obsidianPlan).Ok);
        Assert.True(File.Exists(Path.Combine(context.Destination, "Documents", "Notes", "welcome.md")));

        RecipeCard pst = Assert.Single(cards, card => card.Title.Contains("PST", StringComparison.Ordinal));
        PlanResult pstPlan = host.PlanCard(new OutlookRecipe(), pst, Dest(context));
        await host.ExecuteAsync("session-1", new OutlookRecipe(), pstPlan);
        Assert.True(new OutlookRecipe().Verify(pstPlan).Ok);
        Assert.True(File.Exists(Path.Combine(context.Destination, "Documents", "Outlook Files", "archive.pst")));

        RecipeCard ost = Assert.Single(cards, card => card.Title.Contains("OST", StringComparison.Ordinal));
        PlanResult ostPlan = host.PlanCard(new OutlookRecipe(), ost, Dest(context));
        Assert.Empty(ostPlan.Writes);
    }

    private static string CreateCertificatePem()
    {
        using System.Security.Cryptography.RSA rsa = System.Security.Cryptography.RSA.Create(2048);
        System.Security.Cryptography.X509Certificates.CertificateRequest request = new(
            "CN=syncthing-fixture",
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using System.Security.Cryptography.X509Certificates.X509Certificate2 cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));
        return cert.ExportCertificatePem();
    }

    private static DestinationContext Dest(RecipeContext context)
    {
        return new DestinationContext(context.Destination, context.Exports, context.SafeFs, context.Runner);
    }

    private static async Task<(GitRecipe Recipe, PlanResult Plan)> RestoreGitRepoAsync(
        RecipeContext context,
        RecipeHost? host = null,
        GitRecipe? recipe = null)
    {
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string notes = Path.Combine(alice, "Documents", "notes");
        Directory.CreateDirectory(Path.Combine(notes, ".git"));
        await File.WriteAllTextAsync(Path.Combine(notes, ".git", "HEAD"), "ref: refs/heads/main\n");
        recipe ??= new GitRecipe();
        host ??= new RecipeHost(context.Database, context.SafeFs, context.Runner, [recipe]);
        DetectResult detected = recipe.Detect(
            new ProfileContext(
                "Alice",
                alice,
                context.Destination,
                context.Temp,
                context.Exports,
                context.SafeFs,
                context.Runner));
        RecipeCard card = detected.Cards.Single(item => item.Facts.GetValueOrDefault("kind") == "repo");
        PlanResult plan = host.PlanCard(recipe, card, Dest(context));
        await host.ExecuteAsync("session-1", recipe, plan);
        return (recipe, plan);
    }

    private static HashSet<string> SnapshotFiles(params string[] roots)
    {
        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                files.Add(Path.GetFullPath(path));
            }
        }

        return files;
    }

    private static void WithTempKey4(Action<string> use)
    {
        string path = Path.Combine(Path.GetTempPath(), "WinOldRecovery-key4-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            use(path);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static void WriteSqlite(string path, string sql)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static class Key4Fixture
    {
        public static void WritePbes2(string path, string password)
        {
            byte[] globalSalt = Enumerable.Repeat((byte)0x11, 16).ToArray();
            byte[] entrySalt = Enumerable.Repeat((byte)0x22, 32).ToArray();
            byte[] iv = Enumerable.Repeat((byte)0x33, 16).ToArray();
            byte[] hp = PasswordHash(globalSalt, password);
            byte[] key = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
                hp,
                entrySalt,
                1,
                System.Security.Cryptography.HashAlgorithmName.SHA256,
                32);
            byte[] cipher = EncryptAes(key, iv, PasswordCheckPadded(16));
            byte[] item2 = Der.Seq(
                Der.Seq(
                    Der.Oid("1.2.840.113549.1.5.13"),
                    Der.Seq(
                        Der.Seq(
                            Der.Oid("1.2.840.113549.1.5.12"),
                            Der.Seq(
                                Der.Octet(entrySalt),
                                Der.Int(1),
                                Der.Int(32),
                                Der.Seq(Der.Oid("1.2.840.113549.2.9")))),
                        Der.Seq(Der.Oid("2.16.840.1.101.3.4.1.42"), Der.Octet(iv)))),
                Der.Octet(cipher));
            Write(path, globalSalt, item2);
        }

        public static void Write3Des(string path, string password)
        {
            byte[] globalSalt = Enumerable.Repeat((byte)0x11, 16).ToArray();
            byte[] entrySalt = Enumerable.Repeat((byte)0x44, 20).ToArray();
            byte[] hp = PasswordHash(globalSalt, password);
            byte[] pes = new byte[20];
            entrySalt.CopyTo(pes, 0);
#pragma warning disable CA5350
            byte[] chp = System.Security.Cryptography.SHA1.HashData([.. hp, .. entrySalt]);
            byte[] k1 = System.Security.Cryptography.HMACSHA1.HashData(
                (ReadOnlySpan<byte>)chp,
                (ReadOnlySpan<byte>)[.. pes, .. entrySalt]);
            byte[] tk = System.Security.Cryptography.HMACSHA1.HashData((ReadOnlySpan<byte>)chp, (ReadOnlySpan<byte>)pes);
            byte[] k2 = System.Security.Cryptography.HMACSHA1.HashData(
                (ReadOnlySpan<byte>)chp,
                (ReadOnlySpan<byte>)[.. tk, .. entrySalt]);
#pragma warning restore CA5350
            byte[] material = [.. k1, .. k2];
            byte[] cipher = Encrypt3Des(material[..24], material[^8..], PasswordCheckPadded(8));
            byte[] item2 = Der.Seq(
                Der.Seq(Der.Oid("1.2.840.113549.3.7"), Der.Seq(Der.Octet(entrySalt))),
                Der.Octet(cipher));
            Write(path, globalSalt, item2);
        }

        private static byte[] PasswordHash(byte[] globalSalt, string password)
        {
            byte[] secret = string.IsNullOrEmpty(password)
                ? globalSalt
                : [.. globalSalt, .. System.Text.Encoding.UTF8.GetBytes(password)];
#pragma warning disable CA5350
            return System.Security.Cryptography.SHA1.HashData(secret);
#pragma warning restore CA5350
        }

        private static byte[] PasswordCheckPadded(int blockSize)
        {
            byte[] plain = "password-check\0"u8.ToArray();
            int pad = blockSize - (plain.Length % blockSize);
            byte[] padded = new byte[plain.Length + pad];
            plain.CopyTo(padded, 0);
            Array.Fill(padded, (byte)pad, plain.Length, pad);
            return padded;
        }

        private static byte[] EncryptAes(byte[] key, byte[] iv, byte[] padded)
        {
            using System.Security.Cryptography.Aes aes = System.Security.Cryptography.Aes.Create();
            aes.Mode = System.Security.Cryptography.CipherMode.CBC;
            aes.Padding = System.Security.Cryptography.PaddingMode.None;
            aes.Key = key;
            aes.IV = iv;
            using System.Security.Cryptography.ICryptoTransform transform = aes.CreateEncryptor();
            return transform.TransformFinalBlock(padded, 0, padded.Length);
        }

        private static byte[] Encrypt3Des(byte[] key, byte[] iv, byte[] padded)
        {
#pragma warning disable SYSLIB0021
            using System.Security.Cryptography.TripleDES des = System.Security.Cryptography.TripleDES.Create();
#pragma warning restore SYSLIB0021
            des.Mode = System.Security.Cryptography.CipherMode.CBC;
            des.Padding = System.Security.Cryptography.PaddingMode.None;
            des.Key = key;
            des.IV = iv;
            using System.Security.Cryptography.ICryptoTransform transform = des.CreateEncryptor();
            return transform.TransformFinalBlock(padded, 0, padded.Length);
        }

        private static void Write(string path, byte[] globalSalt, byte[] item2)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using SqliteConnection connection = new(
                new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString);
            connection.Open();
            using SqliteCommand create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE metaData (id PRIMARY KEY UNIQUE ON CONFLICT REPLACE, item1, item2);";
            create.ExecuteNonQuery();
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO metaData (id, item1, item2) VALUES ('password', $s, $i);";
            insert.Parameters.AddWithValue("$s", globalSalt);
            insert.Parameters.AddWithValue("$i", item2);
            insert.ExecuteNonQuery();
        }

        private static class Der
        {
            public static byte[] Seq(params byte[][] parts)
            {
                return Tlv(0x30, parts.SelectMany(static part => part).ToArray());
            }

            public static byte[] Octet(byte[] value) => Tlv(0x04, value);

            public static byte[] Int(int value)
            {
                byte[] raw = BitConverter.GetBytes(value);
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(raw);
                }

                int start = 0;
                while (start < raw.Length - 1 && raw[start] == 0)
                {
                    start++;
                }

                return raw[start] >= 0x80
                    ? Tlv(0x02, [0, .. raw[start..]])
                    : Tlv(0x02, raw[start..]);
            }

            public static byte[] Oid(string oid)
            {
                int[] numbers = oid.Split('.').Select(int.Parse).ToArray();
                List<byte> body = [(byte)(40 * numbers[0] + numbers[1])];
                for (int i = 2; i < numbers.Length; i++)
                {
                    int value = numbers[i];
                    Stack<byte> encoded = new();
                    encoded.Push((byte)(value & 0x7F));
                    value >>= 7;
                    while (value > 0)
                    {
                        encoded.Push((byte)(0x80 | (value & 0x7F)));
                        value >>= 7;
                    }

                    while (encoded.Count > 0)
                    {
                        body.Add(encoded.Pop());
                    }
                }

                return Tlv(0x06, [.. body]);
            }

            private static byte[] Tlv(byte tag, byte[] body)
            {
                byte[] length = body.Length < 128
                    ? [(byte)body.Length]
                    : body.Length < 256
                        ? [0x81, (byte)body.Length]
                        : [0x82, (byte)(body.Length >> 8), (byte)body.Length];
                return [tag, .. length, .. body];
            }
        }
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

    private sealed class ExitOneRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ProcessResult(1, string.Empty, string.Empty));
        }
    }

    private sealed class MappedGitRunner(
        string sourceRepo,
        string destRepo,
        string sourceHead,
        string destHead,
        string sourceStatus,
        string destStatus) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            string? repo = null;
            for (int i = 0; i < request.Arguments.Count - 1; i++)
            {
                if (request.Arguments[i] == "-C")
                {
                    repo = request.Arguments[i + 1];
                    break;
                }
            }

            bool dest = repo is not null && repo.Equals(destRepo, StringComparison.OrdinalIgnoreCase);
            bool source = repo is not null && repo.Equals(sourceRepo, StringComparison.OrdinalIgnoreCase);
            if (!dest && !source)
            {
                return Task.FromResult(new ProcessResult(1, string.Empty, string.Empty));
            }
            if (request.Arguments.Contains("rev-parse"))
            {
                return Task.FromResult(new ProcessResult(0, dest ? destHead : sourceHead, string.Empty));
            }

            if (request.Arguments.Contains("status"))
            {
                return Task.FromResult(new ProcessResult(0, dest ? destStatus : sourceStatus, string.Empty));
            }

            return Task.FromResult(new ProcessResult(1, string.Empty, string.Empty));
        }
    }
}
