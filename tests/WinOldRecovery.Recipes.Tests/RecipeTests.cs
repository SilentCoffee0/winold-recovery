using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
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
    public async Task Ssh_DetectsOpenSshServerHostKeysAsUndecided()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        Directory.CreateDirectory(Path.Combine(alice, ".ssh"));
        await File.WriteAllTextAsync(Path.Combine(alice, ".ssh", "id_ed25519"), "user-key");
        string server = Path.Combine(context.Source, "ProgramData", "ssh");
        Directory.CreateDirectory(server);
        await File.WriteAllTextAsync(Path.Combine(server, "ssh_host_ed25519_key"), Canary);
        await File.WriteAllTextAsync(Path.Combine(server, "ssh_host_ed25519_key.pub"), "ssh-ed25519 HOST");
        await File.WriteAllTextAsync(Path.Combine(server, "sshd_config"), "Port 22");

        RecipeHost host = new(context.Database, context.SafeFs, context.Runner, [new SshRecipe()]);
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

        RecipeCard serverCard = Assert.Single(cards, card => card.InstanceKey == "openssh-server");
        Assert.Equal(Decision.Undecided, serverCard.Components.Single(component => component.Key == "host-keys").SuggestedDefault);
        Assert.DoesNotContain(Canary, string.Join(';', serverCard.Facts.Values), StringComparison.Ordinal);
        Assert.Empty(host.PlanCard(new SshRecipe(), serverCard, Dest(context)).Writes);

        await context.Database.SetKvAsync(
            "session-1",
            StoredRecipeDecisions.KvKey(serverCard.InstanceKey, "host-keys"),
            nameof(Decision.Restore));
        PlanResult plan = host.PlanCard(new SshRecipe(), serverCard, Dest(context), "session-1");
        await host.ExecuteAsync("session-1", new SshRecipe(), plan);
        Assert.True(new SshRecipe().Verify(plan).Ok);
        Assert.True(File.Exists(Path.Combine(context.Destination, "Recovered", "OpenSSH-Server", "sshd_config")));
        Assert.True(File.Exists(Path.Combine(context.Destination, "Recovered", "OpenSSH-Server", "ssh_host_ed25519_key")));
    }

    [Fact]
    public void OpenSshPrivateKeyHeader_ReadsCipherWithoutTheKeyBody()
    {
        Assert.True(OpenSshPrivateKeyHeader.IsUnencrypted(OpenSshPem("none")));
        Assert.False(OpenSshPrivateKeyHeader.IsUnencrypted(OpenSshPem("aes256-ctr")));
        Assert.True(OpenSshPrivateKeyHeader.TryReadCipherName(OpenSshPem("aes256-ctr"), out string cipher));
        Assert.Equal("aes256-ctr", cipher);
        Assert.True(SshRecipe.HasEfsAttribute(FileAttributes.Encrypted));
        Assert.False(SshRecipe.HasEfsAttribute(FileAttributes.Normal));
    }

    [Fact]
    public void Ssh_Verify_RejectsWorldReadablePrivateKeys()
    {
        string dir = Path.Combine(Path.GetTempPath(), "WinOldRecovery-SshAcl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string key = Path.Combine(dir, "id_ed25519");
            File.WriteAllText(key, "key");
            FileSecurity security = new FileInfo(key).GetAccessControl();
            security.AddAccessRule(
                new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    FileSystemRights.Read,
                    AccessControlType.Allow));
            new FileInfo(key).SetAccessControl(security);
            RecipeCard card = new(
                "ssh",
                "SSH",
                "w",
                "y",
                "r",
                "c",
                "g",
                "l",
                [],
                "t",
                new Dictionary<string, string>());
            PlanResult plan = new(
                card,
                [new RecipeWrite(RecipeWriteKind.CopyFile, key, key, null, 1, "files")]);
            Assert.True(SshRecipe.AclAllowsBroadUsers(key));
            Assert.False(new SshRecipe().Verify(plan).Ok);
            SshRecipe.HardenUserOnlyAcl(new SafeFs(new SourceGuard()), key);
            Assert.False(SshRecipe.AclAllowsBroadUsers(key));
            Assert.True(new SshRecipe().Verify(plan).Ok);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Ssh_VerifyAsync_FailsWhenSshGExitsNonZero()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string key = Path.Combine(context.Destination, ".ssh", "id_ed25519");
        Directory.CreateDirectory(Path.GetDirectoryName(key)!);
        await File.WriteAllTextAsync(key, "key");
        SshRecipe.HardenUserOnlyAcl(context.SafeFs, key);
        RecipeCard card = new(
            "ssh",
            "SSH",
            "what",
            "why",
            "restored",
            "cloud",
            "regen",
            "left",
            [],
            "ssh:test",
            new Dictionary<string, string>());
        PlanResult plan = new(
            card,
            [new RecipeWrite(RecipeWriteKind.CopyFile, key, key, null, 1, "files")],
            new DestinationContext(context.Destination, context.Exports, context.SafeFs, new ExitOneRunner(), context.Temp));
        RecipeVerifyResult result = await new SshRecipe().VerifyAsync(plan);
        Assert.False(result.Ok);
        Assert.Contains("ssh -G", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WslLxss_MapsOldUserBasePathAndMatchesPackageFamily()
    {
        string alice = @"D:\Windows.old\Users\Alice";
        string mapped = WslLxss.MapBasePath(
            @"\\?\C:\Users\Alice\AppData\Local\Packages\CanonicalGroupLimited.Ubuntu_79rhkp1fndgsc\LocalState",
            alice,
            "Alice");
        Assert.Equal(
            Path.GetFullPath(Path.Combine(alice, @"AppData\Local\Packages\CanonicalGroupLimited.Ubuntu_79rhkp1fndgsc\LocalState")),
            mapped);
        IReadOnlyList<WslLxss.Distro> distros = WslLxss.Read(
            new Dictionary<string, string> { ["DefaultDistribution"] = "{11111111-1111-1111-1111-111111111111}" },
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["{11111111-1111-1111-1111-111111111111}"] = new Dictionary<string, string>
                {
                    ["DistributionName"] = "Ubuntu",
                    ["BasePath"] = @"C:\Users\Alice\AppData\Local\Packages\CanonicalGroupLimited.Ubuntu_79rhkp1fndgsc\LocalState",
                    ["Version"] = "2",
                    ["DefaultUid"] = "1000",
                    ["PackageFamilyName"] = "CanonicalGroupLimited.Ubuntu_79rhkp1fndgsc",
                },
            },
            alice,
            "Alice");
        WslLxss.Distro? match = WslLxss.Match(
            Path.Combine(
                alice,
                "AppData",
                "Local",
                "Packages",
                "CanonicalGroupLimited.Ubuntu_79rhkp1fndgsc",
                "LocalState",
                "ext4.vhdx"),
            distros);
        Assert.NotNull(match);
        Assert.Equal("Ubuntu", match.Name);
        Assert.Equal("1000", match.DefaultUid);
        Assert.Equal("2", match.Version);
        Assert.True(match.IsDefault);
    }

    [Fact]
    public async Task Ssh_Detect_MarksUnencryptedOpenSshKeysAndCountsHosts()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string ssh = Path.Combine(alice, ".ssh");
        Directory.CreateDirectory(ssh);
        await File.WriteAllBytesAsync(Path.Combine(ssh, "id_ed25519"), OpenSshPem("none"));
        await File.WriteAllTextAsync(Path.Combine(ssh, "id_ed25519.pub"), "ssh-ed25519 AAAA-not-a-secret");
        await File.WriteAllBytesAsync(Path.Combine(ssh, "id_rsa"), OpenSshPem("aes256-ctr"));
        await File.WriteAllTextAsync(
            Path.Combine(ssh, "config"),
            """
            Host github.com
              HostName github.com
            Host *
              IdentityFile ~/.ssh/id_ed25519
            """);
        await File.WriteAllTextAsync(
            Path.Combine(ssh, "known_hosts"),
            """
            # comment
            github.com ssh-ed25519 AAAA-not-a-secret
            """);

        DetectResult detected = new SshRecipe().Detect(
            new ProfileContext(
                "Alice",
                alice,
                context.Destination,
                context.Temp,
                context.Exports,
                context.SafeFs,
                context.Runner));
        RecipeCard card = Assert.Single(detected.Cards);
        Assert.Equal("1", card.Facts["unencrypted"]);
        Assert.Equal("1", card.Facts["unencryptedCount"]);
        Assert.Equal("2", card.Facts["configHosts"]);
        Assert.Equal("1", card.Facts["knownHosts"]);
        Assert.Equal("ed25519", card.Facts["keyTypes"]);
        Assert.Equal("0", card.Facts["efsKeys"]);
        Assert.Contains("passphrase", card.WhatIsRestored, StringComparison.OrdinalIgnoreCase);
        string joined = string.Join(';', card.Facts.Values);
        Assert.DoesNotContain("b3BlbnNzaC", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("AAAA-not-a-secret", joined, StringComparison.Ordinal);
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
        await File.WriteAllTextAsync(
            Path.Combine(alice, "AppData", "Local", "Google", "Chrome", "User Data", "Last Version"),
            "131.0.6778.86\n");
        await File.WriteAllTextAsync(
            Path.Combine(alice, "AppData", "Local", "Google", "Chrome", "User Data", "Local State"),
            """
            {"profile":{"info_cache":{"Default":{"name":"Work","gaia_name":"Alice","user_name":"alice@example.invalid"}}},"os_crypt":{"encrypted_key":"WINOLD_RECOVERY_CANARY_DO_NOT_LOG_7F3A91"}}
            """);
        Directory.CreateDirectory(Path.Combine(chrome, "Extensions", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "1.0"));
        await File.WriteAllTextAsync(
            Path.Combine(chrome, "Bookmarks"),
            """{"roots":{"bookmark_bar":{"children":[{"type":"url","url":"https://example.com"}]}}}""");
        await File.WriteAllTextAsync(Path.Combine(chrome, "Login Data"), Canary);
        Directory.CreateDirectory(
            Path.Combine(chrome, "Extensions", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "1.0", "_locales", "en"));
        await File.WriteAllTextAsync(
            Path.Combine(chrome, "Extensions", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "1.0", "manifest.json"),
            """{"name":"__MSG_extName__","default_locale":"en","version":"1.0"}""");
        await File.WriteAllTextAsync(
            Path.Combine(chrome, "Extensions", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "1.0", "_locales", "en", "messages.json"),
            """{"extName":{"message":"Localized Fixture"}}""");
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
        RecipeCard chromeCard = cards.Single(card => card.RecipeId == "chrome");
        Assert.Equal("Google Chrome — Work (alice@example.invalid)", chromeCard.Title);
        Assert.Equal("131.0.6778.86", chromeCard.Facts["browserVersion"]);
        Assert.Equal("Work", chromeCard.Facts["displayName"]);
        Assert.True(DateTimeOffset.TryParse(chromeCard.Facts["lastUsed"], out _));
        Assert.False(chromeCard.Facts.ContainsKey("bookmarksJson"));
        Assert.False(chromeCard.Facts.ContainsKey("extensionsHtml"));
        Assert.False(chromeCard.Facts.ContainsKey("tabsHtml"));
        Assert.DoesNotContain("encrypted_key", string.Join(';', chromeCard.Facts.Values), StringComparison.Ordinal);
        Assert.Contains(cards, card => card.RecipeId == "firefox");
        Assert.Equal(
            "Firefox — fixture (default)",
            cards.Single(card => card.RecipeId == "firefox").Title);
        Assert.Contains(cards, card => card.Title.Contains("Git", StringComparison.Ordinal));
        string dump = string.Join('\n', cards.Select(card => card.Title + card.What + string.Join(';', card.Facts.Values)));
        Assert.DoesNotContain(Canary, dump, StringComparison.Ordinal);
        Assert.Contains(cards.Single(card => card.RecipeId == "chrome").Components, c => c.Fixed && c.Key == "passwords");

        ProfileContext profile = new(
            "Alice",
            alice,
            context.Destination,
            context.Temp,
            context.Exports,
            context.SafeFs,
            context.Runner);
        DetectResult chromeDetected = new ChromiumRecipe(
            "chrome",
            "Google Chrome",
            Path.Combine("AppData", "Local", "Google", "Chrome", "User Data")).Detect(profile);
        Assert.Contains(
            chromeDetected.Badges,
            badge => badge.Kind == "Chrome" && badge.Detail == "Work");
        DetectResult firefoxDetected = new FirefoxRecipe().Detect(profile);
        Assert.Contains(
            firefoxDetected.Badges,
            badge => badge.Kind == "Firefox" && badge.Detail == "fixture");
        string badgeDump = string.Join(
            ';',
            chromeDetected.Badges.Concat(firefoxDetected.Badges)
                .Select(badge => badge.RelativePath + badge.Kind + badge.Detail));
        Assert.DoesNotContain(Canary, badgeDump, StringComparison.Ordinal);

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

        PlanResult chromePlan = host.PlanCard(
            RecipeCatalog.All.Single(recipe => recipe.Id == "chrome"),
            chromeCard,
            Dest(context));
        await host.ExecuteAsync("session-1", RecipeCatalog.All.Single(recipe => recipe.Id == "chrome"), chromePlan);
        Assert.True(RecipeCatalog.All.Single(recipe => recipe.Id == "chrome").Verify(chromePlan).Ok);
        string bookmarksPath = chromePlan.Writes.Single(write => write.DestinationPath.EndsWith("bookmarks.html", StringComparison.Ordinal)).DestinationPath;
        string bookmarks = await File.ReadAllTextAsync(bookmarksPath);
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
        string extensionsHtml = await File.ReadAllTextAsync(
            chromePlan.Writes.Single(write => write.DestinationPath.EndsWith("extensions.html", StringComparison.Ordinal)).DestinationPath);
        Assert.Contains(
            "chromewebstore.google.com/detail/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            extensionsHtml,
            StringComparison.Ordinal);
        Assert.Contains("Localized Fixture", extensionsHtml, StringComparison.Ordinal);
        await File.WriteAllTextAsync(bookmarksPath, bookmarks.Replace("<A HREF=", "<SPAN ", StringComparison.Ordinal));
        RecipeVerifyResult stripped = RecipeCatalog.All.Single(recipe => recipe.Id == "chrome").Verify(chromePlan);
        Assert.False(stripped.Ok);
        Assert.Contains("Bookmark HTML", stripped.Detail, StringComparison.OrdinalIgnoreCase);

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
        Assert.True(new FirefoxRecipe().Verify(firefoxPlan).Ok);

        RecipeCard gitConfig = cards.Single(card => card.Title.StartsWith("Git configuration", StringComparison.Ordinal));
        PlanResult gitConfigPlan = host.PlanCard(new GitRecipe(), gitConfig, Dest(context));
        await host.ExecuteAsync("session-1", new GitRecipe(), gitConfigPlan);
        string restoredConfig = await File.ReadAllTextAsync(Path.Combine(context.Destination, ".gitconfig"));
        Assert.Contains("Alice", restoredConfig, StringComparison.Ordinal);
        Assert.Contains("helper = store", restoredConfig, StringComparison.Ordinal);
        Assert.Equal("Alice", gitConfig.Facts["userName"]);
        Assert.Equal("store", gitConfig.Facts["credentialHelper"]);

        RecipeCard gitRepo = cards.Single(card => card.Title.Contains("repository", StringComparison.Ordinal));
        PlanResult gitRepoPlan = host.PlanCard(new GitRecipe(), gitRepo, Dest(context));
        await host.ExecuteAsync("session-1", new GitRecipe(), gitRepoPlan);
        Assert.True(File.Exists(Path.Combine(context.Destination, "Recovered", "local-repository", "README.md")));
        Assert.True(File.Exists(Path.Combine(context.Destination, "Recovered", "local-repository", ".git", "HEAD")));
    }

    [Fact]
    public void GitScrub_HidesCredentialSecretsAndInsteadOfUserinfo()
    {
        string scrubbed = GitRecipe.Scrub(
            """
            [credential]
            	helper = store
            	username = alice
            [url "https://alice:token@github.com/"]
            	insteadOf = https://alice@example.invalid/
            [user]
            	name = Alice
            """);
        Assert.Contains("helper = store", scrubbed, StringComparison.Ordinal);
        Assert.Contains("Alice", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("username = alice", scrubbed, StringComparison.Ordinal);
        Assert.Contains("[url \"***\"]", scrubbed, StringComparison.Ordinal);
        Assert.Contains("insteadOf = ***", scrubbed, StringComparison.Ordinal);
        Dictionary<string, string> facts = GitConfigFacts.Read(
            "[user]\n\tname = Alice\n\temail = alice@example.invalid\n[core]\n\tsshCommand = ssh -i ~/.ssh/id_ed25519\n[credential]\n\thelper = store\n");
        Assert.Equal("Alice", facts["userName"]);
        Assert.Equal("alice@example.invalid", facts["userEmail"]);
        Assert.Equal("ssh -i ~/.ssh/id_ed25519", facts["sshCommand"]);
        Assert.Equal("store", facts["credentialHelper"]);
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
    public async Task Git_Detect_ListsVendoredReposWithoutAnalyzingStatus()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string app = Path.Combine(alice, "Projects", "app");
        Directory.CreateDirectory(Path.Combine(app, ".git"));
        await File.WriteAllTextAsync(Path.Combine(app, ".git", "HEAD"), "ref: refs/heads/main\n");
        string leftPad = Path.Combine(app, "node_modules", "left-pad");
        Directory.CreateDirectory(Path.Combine(leftPad, ".git"));
        await File.WriteAllTextAsync(Path.Combine(leftPad, ".git", "HEAD"), "ref: refs/heads/master\n");
        await File.WriteAllTextAsync(
            Path.Combine(leftPad, ".git", "config"),
            "[remote \"origin\"]\n\turl = https://user:" + Canary + "@example.invalid/left-pad.git\n");
        string scoped = Path.Combine(app, "node_modules", "@scope", "pkg");
        Directory.CreateDirectory(Path.Combine(scoped, ".git"));
        await File.WriteAllTextAsync(Path.Combine(scoped, ".git", "HEAD"), "ref: refs/heads/main\n");
        string cached = Path.Combine(alice, ".cache", "tool");
        Directory.CreateDirectory(Path.Combine(cached, ".git"));
        await File.WriteAllTextAsync(Path.Combine(cached, ".git", "HEAD"), "ref: refs/heads/main\n");
        string clutter = Path.Combine(app, "node_modules");
        for (int index = 0; index < 20; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(clutter, $"package-{index:D4}.tmp"), "file");
        }

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
        RecipeCard appCard = detected.Cards.Single(card =>
            card.Facts.GetValueOrDefault("kind") == "repo" &&
            card.Facts.GetValueOrDefault("vendored") != "1");
        Assert.Contains("app", appCard.Title, StringComparison.Ordinal);
        Assert.Equal(Decision.Restore, appCard.Components.Single(c => c.Key == "repo").SuggestedDefault);

        IReadOnlyList<RecipeCard> vendored = detected.Cards
            .Where(card => card.Facts.GetValueOrDefault("vendored") == "1")
            .ToArray();
        Assert.Equal(3, vendored.Count);
        Assert.All(
            vendored,
            card =>
            {
                Assert.Equal("Git: vendored", card.Facts["risk"]);
                Assert.Equal(Decision.LeaveBehind, card.Components.Single(c => c.Key == "repo").SuggestedDefault);
                Assert.DoesNotContain(Canary, string.Join(';', card.Facts.Values), StringComparison.Ordinal);
                Assert.Contains("(vendored)", card.Title, StringComparison.Ordinal);
            });
        Assert.Contains(vendored, card => card.Title.Contains("left-pad", StringComparison.Ordinal));
        Assert.Contains(vendored, card => card.Title.Contains("pkg", StringComparison.Ordinal));
        Assert.Contains(vendored, card => card.Title.Contains("tool", StringComparison.Ordinal));
        Assert.Contains(
            detected.Badges,
            badge => badge.Kind == "Git" && badge.Detail == "Git: vendored");
        Assert.Empty(context.Runner.Requests);
    }

    [Fact]
    public void GitAnalyze_CreateVerifyRequests_DestinationUsesDashC()
    {
        IReadOnlyList<ProcessRequest> requests = GitAnalyze.CreateVerifyRequests(
            @"C:\Users\Alice\Recovered\repo",
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
                Assert.Contains(@"C:\Users\Alice\Recovered\repo", request.Arguments);
                Assert.DoesNotContain("--git-dir", request.Arguments);
                Assert.Equal("0", request.Environment!["GIT_OPTIONAL_LOCKS"]);
                Assert.Equal(@"C:\Users\Alice", request.Environment["HOME"]);
            });
    }

    [Fact]
    public void GitAnalyze_CreateSourceVerifyRequests_UsesGitDirNotDashC()
    {
        IReadOnlyList<ProcessRequest> requests = GitAnalyze.CreateSourceVerifyRequests(
            @"D:\session\tmp\git-analyze\copy",
            @"D:\Windows.old\Users\Alice\repo",
            @"C:\Users\Alice");
        Assert.Equal(2, requests.Count);
        Assert.All(
            requests,
            request =>
            {
                Assert.Contains("--git-dir", request.Arguments);
                Assert.Contains(@"D:\session\tmp\git-analyze\copy", request.Arguments);
                Assert.Contains("--work-tree", request.Arguments);
                Assert.Contains(@"D:\Windows.old\Users\Alice\repo", request.Arguments);
                Assert.DoesNotContain("-C", request.Arguments);
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
    public async Task GitAnalyze_CompareRestored_CopiesSourceGitDirAndDoesNotDashCWindowsOld()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string repo = Path.Combine(context.Source, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        await File.WriteAllTextAsync(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main\n");
        string dest = Path.Combine(context.Destination, "Recovered", "repo");
        Directory.CreateDirectory(Path.Combine(dest, ".git"));
        await File.WriteAllTextAsync(Path.Combine(dest, ".git", "HEAD"), "ref: refs/heads/main\n");
        await GitAnalyze.CompareRestoredAsync(
            context.Runner,
            repo,
            dest,
            context.Destination,
            context.SafeFs,
            context.Temp);
        Assert.Contains(
            context.Runner.Requests,
            request => request.Arguments.Contains("--git-dir") &&
                request.Arguments.Contains("--work-tree") &&
                request.Arguments.Contains(repo));
        Assert.DoesNotContain(
            context.Runner.Requests,
            request =>
            {
                for (int i = 0; i < request.Arguments.Count - 1; i++)
                {
                    if (request.Arguments[i] == "-C" &&
                        request.Arguments[i + 1].Equals(repo, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            });
        Assert.Contains(
            context.Runner.Requests,
            request => request.Arguments.Any(static argument =>
                argument.Contains("git-analyze", StringComparison.OrdinalIgnoreCase)));
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
    public async Task RecipeDetectors_PersistTreeBadgesOntoScanNodes()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        Directory.CreateDirectory(Path.Combine(alice, "Documents", "Passwords"));
        await File.WriteAllTextAsync(Path.Combine(alice, "Documents", "Passwords", "vault.kdbx"), "keepass");
        Directory.CreateDirectory(Path.Combine(alice, "Sync"));
        string home = Path.Combine(alice, "AppData", "Local", "Syncthing");
        Directory.CreateDirectory(home);
        string certPem = CreateCertificatePem();
        await File.WriteAllTextAsync(Path.Combine(home, "cert.pem"), certPem);
        await File.WriteAllTextAsync(Path.Combine(home, "key.pem"), "key");
        await File.WriteAllTextAsync(
            Path.Combine(home, "config.xml"),
            $"""
            <configuration version="37">
              <folder id="default" label="Photos" path="{Path.Combine(alice, "Sync")}" paused="false" />
            </configuration>
            """);
        await context.Database.InsertNodesAsync(
        [
            new PersistedNode(
                1,
                "session-1",
                null,
                null,
                "vault.kdbx",
                @"Users\Alice\Documents\Passwords\vault.kdbx",
                NodeKind.File,
                0,
                0,
                0,
                DateTime.UtcNow,
                0,
                NodeProblem.None),
            new PersistedNode(
                2,
                "session-1",
                null,
                null,
                "Sync",
                @"Users\Alice\Sync",
                NodeKind.Directory,
                0,
                0,
                0,
                DateTime.UtcNow,
                0,
                NodeProblem.None),
        ]);

        RecipeHost host = new(
            context.Database,
            context.SafeFs,
            context.Runner,
            [new KeePassRecipe(), new SyncthingRecipe()]);
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
        command.CommandText = "SELECT kind || ':' || detail FROM badges ORDER BY kind;";
        List<string> stored = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            stored.Add(reader.GetString(0));
        }

        Assert.Contains(stored, row => row.StartsWith("KeePass:", StringComparison.Ordinal) && row.Contains("vault.kdbx", StringComparison.Ordinal));
        Assert.Contains(stored, row => row == "Syncthing folder:Photos");
        Assert.DoesNotContain(stored, row => row.Contains(Canary, StringComparison.Ordinal));
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
        Assert.False(card.Facts.ContainsKey("tabsHtml"));
        Assert.False(card.Facts.ContainsKey("extensionsHtml"));
        Assert.False(card.Facts.ContainsKey("bookmarksHtml"));
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
    public async Task Firefox_Detect_SkipsProfilesIniWhenStoreIdMarksProfileGroups()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string firefox = Path.Combine(alice, "AppData", "Roaming", "Mozilla", "Firefox");
        string profile = Path.Combine(firefox, "Profiles", "abcd.default");
        Directory.CreateDirectory(profile);
        WriteSqlite(
            Path.Combine(profile, "places.sqlite"),
            """
            CREATE TABLE moz_places(id INTEGER PRIMARY KEY, url TEXT, title TEXT, hidden INTEGER DEFAULT 0);
            CREATE TABLE moz_bookmarks(id INTEGER PRIMARY KEY, type INTEGER, fk INTEGER, title TEXT, parent INTEGER);
            INSERT INTO moz_places(id, url, title) VALUES (1, 'https://groups.firefox.example', 'Fx');
            INSERT INTO moz_bookmarks(id, type, fk, title, parent) VALUES (1, 1, 1, 'Fx', 0);
            """);
        await File.WriteAllTextAsync(
            Path.Combine(firefox, "profiles.ini"),
            """
            [Profile0]
            Name=default
            IsRelative=1
            Path=Profiles/abcd.default
            StoreID=abc123

            [General]
            StartWithLastProfile=1
            Version=2
            """);
        Assert.True(FirefoxProfileGroups.HasStoreId(await File.ReadAllTextAsync(Path.Combine(firefox, "profiles.ini"))));

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
        Assert.Equal("1", card.Facts["profileGroups"]);
        Assert.Contains("about:profiles", card.WhatIsRestored, StringComparison.OrdinalIgnoreCase);
        PlanResult plan = new RecipeHost(context.Database, context.SafeFs, context.Runner, [recipe])
            .PlanCard(recipe, card, Dest(context));
        Assert.DoesNotContain(
            plan.Writes,
            write => write.DestinationPath.EndsWith("profiles.ini", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            plan.Writes,
            write => write.DestinationPath.EndsWith("places.sqlite", StringComparison.OrdinalIgnoreCase));
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
    public async Task Firefox_Verify_ChecksIniPlacesPairAndBookmarkCount()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string firefox = Path.Combine(alice, "AppData", "Roaming", "Mozilla", "Firefox", "Profiles", "l3.default");
        Directory.CreateDirectory(firefox);
        await File.WriteAllTextAsync(Path.Combine(firefox, "logins.json"), """{"logins":[]}""");
        await File.WriteAllTextAsync(Path.Combine(firefox, "key4.db"), "k");
        WriteSqlite(
            Path.Combine(firefox, "places.sqlite"),
            """
            CREATE TABLE moz_places(id INTEGER PRIMARY KEY, url TEXT, title TEXT, hidden INTEGER DEFAULT 0);
            CREATE TABLE moz_bookmarks(id INTEGER PRIMARY KEY, type INTEGER, fk INTEGER, title TEXT, parent INTEGER);
            INSERT INTO moz_places(id, url, title) VALUES (1, 'https://l3.firefox.example', 'L3');
            INSERT INTO moz_bookmarks(id, type, fk, title, parent) VALUES (1, 1, 1, 'L3', 0);
            """);

        FirefoxRecipe recipe = new();
        RecipeHost host = new(context.Database, context.SafeFs, context.Runner, [recipe]);
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
        PlanResult plan = host.PlanCard(recipe, card, Dest(context));
        await host.ExecuteAsync("session-1", recipe, plan);
        Assert.True(recipe.Verify(plan).Ok);

        string destPlaces = Path.Combine(
            context.Destination,
            "AppData",
            "Roaming",
            "Mozilla",
            "Firefox",
            "Profiles",
            "l3.default-recovered",
            "places.sqlite");
        File.Delete(destPlaces);
        WriteSqlite(
            destPlaces,
            """
            CREATE TABLE moz_places(id INTEGER PRIMARY KEY, url TEXT, title TEXT, hidden INTEGER DEFAULT 0);
            CREATE TABLE moz_bookmarks(id INTEGER PRIMARY KEY, type INTEGER, fk INTEGER, title TEXT, parent INTEGER);
            """);
        Assert.False(recipe.Verify(plan).Ok);
        File.Copy(Path.Combine(firefox, "places.sqlite"), destPlaces, overwrite: true);
        Assert.True(recipe.Verify(plan).Ok);

        string destLogins = Path.Combine(
            context.Destination,
            "AppData",
            "Roaming",
            "Mozilla",
            "Firefox",
            "Profiles",
            "l3.default-recovered",
            "logins.json");
        File.Delete(destLogins);
        Assert.False(recipe.Verify(plan).Ok);
        await File.WriteAllTextAsync(destLogins, """{"logins":[]}""");
        Assert.True(recipe.Verify(plan).Ok);

        string ini = Path.Combine(
            context.Destination,
            "AppData",
            "Roaming",
            "Mozilla",
            "Firefox",
            "profiles.ini");
        await File.WriteAllTextAsync(ini, "[General]\nStartWithLastProfile=1\n");
        Assert.False(recipe.Verify(plan).Ok);
    }

    [Fact]
    public async Task Firefox_Transplant_CopiesAllowListedFoldersAndSkipsLocks()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string firefox = Path.Combine(alice, "AppData", "Roaming", "Mozilla", "Firefox", "Profiles", "folders.default");
        string backups = Path.Combine(firefox, "sessionstore-backups");
        string bookmarkBackups = Path.Combine(firefox, "bookmarkbackups");
        string extensions = Path.Combine(firefox, "extensions");
        string storage = Path.Combine(firefox, "storage", "default", "https+++example.com");
        Directory.CreateDirectory(backups);
        Directory.CreateDirectory(bookmarkBackups);
        Directory.CreateDirectory(extensions);
        Directory.CreateDirectory(storage);
        await File.WriteAllTextAsync(Path.Combine(firefox, "logins.json"), """{"logins":[]}""");
        await File.WriteAllTextAsync(Path.Combine(firefox, "key4.db"), "k");
        await File.WriteAllTextAsync(Path.Combine(firefox, "search.json.mozlz4"), "search");
        await File.WriteAllBytesAsync(
            Path.Combine(backups, "recovery.jsonlz4"),
            MozLz4.Encode("""{"windows":[]}"""u8));
        await File.WriteAllTextAsync(Path.Combine(backups, "parent.lock"), "skip");
        await File.WriteAllTextAsync(Path.Combine(bookmarkBackups, "bookmarks-2026.jsonlz4"), "bookmarks");
        await File.WriteAllTextAsync(Path.Combine(extensions, "ublock@raymondhill.net.xpi"), "xpi");
        await File.WriteAllTextAsync(Path.Combine(storage, "ls"), "site-storage");
        WriteSqlite(
            Path.Combine(firefox, "places.sqlite"),
            """
            CREATE TABLE moz_places(id INTEGER PRIMARY KEY, url TEXT, title TEXT, hidden INTEGER DEFAULT 0);
            CREATE TABLE moz_bookmarks(id INTEGER PRIMARY KEY, type INTEGER, fk INTEGER, title TEXT, parent INTEGER);
            INSERT INTO moz_places(id, url, title) VALUES (1, 'https://folders.firefox.example', 'Fx');
            INSERT INTO moz_bookmarks(id, type, fk, title, parent) VALUES (1, 1, 1, 'Fx', 0);
            """);

        FirefoxRecipe recipe = new();
        RecipeHost host = new(context.Database, context.SafeFs, context.Runner, [recipe]);
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
        Assert.Contains("sessionstore-backups", card.Facts["folders"], StringComparison.Ordinal);
        Assert.Contains("search.json.mozlz4", card.Facts["files"], StringComparison.Ordinal);
        PlanResult plan = host.PlanCard(recipe, card, Dest(context));
        await host.ExecuteAsync("session-1", recipe, plan);
        string dest = Path.Combine(
            context.Destination,
            "AppData",
            "Roaming",
            "Mozilla",
            "Firefox",
            "Profiles",
            "folders.default-recovered");
        Assert.True(File.Exists(Path.Combine(dest, "search.json.mozlz4")));
        Assert.True(File.Exists(Path.Combine(dest, "sessionstore-backups", "recovery.jsonlz4")));
        Assert.True(File.Exists(Path.Combine(dest, "bookmarkbackups", "bookmarks-2026.jsonlz4")));
        Assert.True(File.Exists(Path.Combine(dest, "extensions", "ublock@raymondhill.net.xpi")));
        Assert.True(File.Exists(Path.Combine(dest, "storage", "default", "https+++example.com", "ls")));
        Assert.False(File.Exists(Path.Combine(dest, "sessionstore-backups", "parent.lock")));
        Assert.True(recipe.Verify(plan).Ok);
        Assert.DoesNotContain(Canary, string.Join(';', card.Facts.Values), StringComparison.Ordinal);
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
        await File.WriteAllTextAsync(Path.Combine(home, "https-cert.pem"), "gui-cert");
        await File.WriteAllTextAsync(Path.Combine(home, "https-key.pem"), Canary);
        Directory.CreateDirectory(Path.Combine(home, "gui"));
        await File.WriteAllTextAsync(Path.Combine(home, "gui", "theme.css"), "custom-gui");
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
        File.SetLastWriteTimeUtc(Path.Combine(home, "config.xml"), DateTime.UtcNow);
        string stale = Path.Combine(context.Source, "ProgramData", "Syncthing");
        Directory.CreateDirectory(stale);
        string staleCert = CreateCertificatePem();
        await File.WriteAllTextAsync(Path.Combine(stale, "cert.pem"), staleCert);
        await File.WriteAllTextAsync(Path.Combine(stale, "key.pem"), "stale-key");
        await File.WriteAllTextAsync(Path.Combine(stale, "config.xml"), """<configuration version="30"></configuration>""");
        File.SetLastWriteTimeUtc(Path.Combine(stale, "config.xml"), DateTime.UtcNow.AddDays(-30));
        string trayzor = Path.Combine(alice, "AppData", "Roaming", "SyncTrayzor");
        Directory.CreateDirectory(trayzor);
        File.Copy(typeof(SyncthingRecipe).Assembly.Location, Path.Combine(trayzor, "syncthing.exe"));

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

        List<RecipeCard> synCards = cards.Where(item => item.RecipeId == "syncthing").ToList();
        Assert.Equal(2, synCards.Count);
        RecipeCard card = Assert.Single(synCards, item => item.Facts["probablyActive"] == "1");
        RecipeCard staleCard = Assert.Single(synCards, item => item.Facts["probablyActive"] == "0");
        Assert.Contains("probably active", card.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("probably active", staleCard.Title, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(card.Facts["lastActivity"]));
        Assert.Equal(
            FileVersionInfo.GetVersionInfo(typeof(SyncthingRecipe).Assembly.Location).FileVersion,
            card.Facts["syncthingExeVersion"]);
        string dump = string.Join(';', card.Facts.Values);
        Assert.DoesNotContain(Canary, dump, StringComparison.Ordinal);
        Assert.DoesNotContain("SUPERSECRETAPIKEY", dump, StringComparison.Ordinal);
        Assert.Contains("***", dump, StringComparison.Ordinal);
        Assert.Equal(SyncthingDeviceId.FromCertificatePem(certPem), card.Facts["deviceId"]);
        Assert.Contains(card.Components, component => component.Fixed && component.Key == "index");
        Assert.Equal(
            Decision.LeaveBehind,
            card.Components.Single(component => component.Key == "gui-tls").SuggestedDefault);
        Assert.Equal(Decision.Restore, card.Components.Single(component => component.Key == "gui").SuggestedDefault);
        Assert.Equal("1", card.Facts["guiTls"]);
        Assert.Equal("1", card.Facts["hasGui"]);

        DetectResult synDetected = new SyncthingRecipe().Detect(
            new ProfileContext(
                "Alice",
                alice,
                context.Destination,
                context.Temp,
                context.Exports,
                context.SafeFs,
                context.Runner));
        Assert.Contains(
            synDetected.Badges,
            badge => badge.Kind == "Syncthing folder" && badge.Detail == "Default Folder");
        Assert.Contains(
            synDetected.Badges,
            badge => badge.Kind == "Syncthing folder" && badge.Detail == "Docs");
        Assert.DoesNotContain(synDetected.Badges, badge => badge.Detail == "External");
        Assert.DoesNotContain(
            Canary,
            string.Join(';', synDetected.Badges.Select(badge => badge.RelativePath + badge.Kind + badge.Detail)),
            StringComparison.Ordinal);

        PlanResult plan = host.PlanCard(new SyncthingRecipe(), card, Dest(context));
        Assert.DoesNotContain(plan.Writes, write => write.DestinationPath.Contains("index", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            plan.Writes,
            write => write.DestinationPath.Contains("https-key.pem", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            plan.Writes,
            write => write.DestinationPath.EndsWith(Path.Combine("gui", "theme.css"), StringComparison.OrdinalIgnoreCase));
        await context.Database.SetKvAsync(
            "session-1",
            StoredRecipeDecisions.KvKey(card.InstanceKey, "gui-tls"),
            nameof(Decision.Restore));
        PlanResult tlsPlan = host.PlanCard(new SyncthingRecipe(), card, Dest(context), "session-1");
        Assert.Contains(
            tlsPlan.Writes,
            write => write.DestinationPath.EndsWith("https-key.pem", StringComparison.OrdinalIgnoreCase));
        await host.ExecuteAsync("session-1", new SyncthingRecipe(), plan);
        Assert.True(new SyncthingRecipe().Verify(plan).Ok);

        string destConfig = await File.ReadAllTextAsync(Path.Combine(context.Destination, "AppData", "Local", "Syncthing", "config.xml"));
        Assert.True(SyncthingConfig.AllFoldersPaused(destConfig));
        Assert.Contains(Path.Combine(context.Destination, "Sync"), destConfig, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"D:\Sync", destConfig, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(context.Destination, "AppData", "Local", "Syncthing", "key.pem")));
        Assert.True(File.Exists(Path.Combine(context.Destination, "AppData", "Local", "Syncthing", "gui", "theme.css")));
        Assert.False(File.Exists(Path.Combine(context.Destination, "AppData", "Local", "Syncthing", "https-key.pem")));
        Assert.False(Directory.Exists(Path.Combine(context.Destination, "AppData", "Local", "Syncthing", "index-v2")));
        Assert.Contains("SUPERSECRETAPIKEY", destConfig, StringComparison.Ordinal);
        PlanResult withDest = plan with { Destination = Dest(context) };
        Assert.True((await new SyncthingRecipe().VerifyAsync(withDest)).Ok);
        Assert.Contains(
            context.Runner.Requests,
            request => request.FileName.Equals("syncthing.exe", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.Contains("--device-id") &&
                request.Arguments.Contains("--home"));
        DestinationContext wrongCli = new(
            context.Destination,
            context.Exports,
            context.SafeFs,
            new FixedOutputRunner("NOT-A-SYNCTHING-DEVICE-ID"));
        Assert.False((await new SyncthingRecipe().VerifyAsync(plan with { Destination = wrongCli })).Ok);
        DestinationContext matchingCli = new(
            context.Destination,
            context.Exports,
            context.SafeFs,
            new FixedOutputRunner(card.Facts["deviceId"]));
        Assert.True((await new SyncthingRecipe().VerifyAsync(plan with { Destination = matchingCli })).Ok);
        string destCert = Path.Combine(context.Destination, "AppData", "Local", "Syncthing", "cert.pem");
        await File.WriteAllTextAsync(destCert, staleCert);
        Assert.False(new SyncthingRecipe().Verify(plan).Ok);
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
    public async Task Anki_Detect_FindsShortcutDashBBaseAndRecordsBackupFacts()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string custom = Path.Combine(alice, "CustomAnki");
        string profile = Path.Combine(custom, "Work");
        Directory.CreateDirectory(Path.Combine(profile, "backups"));
        Directory.CreateDirectory(Path.Combine(custom, "addons21", "2040501954"));
        WriteSqlite(
            Path.Combine(profile, "collection.anki2"),
            """
            CREATE TABLE notes(id INTEGER PRIMARY KEY, guid TEXT);
            CREATE TABLE cards(id INTEGER PRIMARY KEY, nid INTEGER);
            CREATE TABLE col(id INTEGER PRIMARY KEY, ver INTEGER, scm INTEGER);
            INSERT INTO notes(guid) VALUES ('note-1');
            INSERT INTO cards(nid) VALUES (1);
            INSERT INTO col(id, ver, scm) VALUES (1, 18, 18);
            """);
        await File.WriteAllTextAsync(Path.Combine(profile, "backups", "backup-1.colpkg"), "pkg");
        await File.WriteAllTextAsync(Path.Combine(custom, "addons21", "2040501954", "manifest.json"), """{"name":"Review Heatmap"}""");
        string programs = Path.Combine(alice, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs");
        Directory.CreateDirectory(programs);
        await File.WriteAllBytesAsync(
            Path.Combine(programs, "Anki.lnk"),
            System.Text.Encoding.Unicode.GetBytes("Anki.exe -b \"" + custom + "\""));

        DetectResult detected = new AnkiRecipe().Detect(
            new ProfileContext(
                "Alice",
                alice,
                context.Destination,
                context.Temp,
                context.Exports,
                context.SafeFs,
                context.Runner));
        RecipeCard card = Assert.Single(detected.Cards);
        Assert.Equal("Work", Path.GetFileName(card.Facts["source"]));
        Assert.Equal("18", card.Facts["schema"]);
        Assert.Equal("1", card.Facts["backups"]);
        Assert.Equal("backup-1.colpkg", card.Facts["newestBackup"]);
        Assert.Equal("1", card.Facts["addons"]);
    }

    [Fact]
    public async Task Anki_Detect_FindsShortcutDashBBaseFromRecipeIndex()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string custom = Path.Combine(alice, "CustomAnki");
        string profile = Path.Combine(custom, "Work");
        Directory.CreateDirectory(Path.Combine(profile, "backups"));
        WriteSqlite(
            Path.Combine(profile, "collection.anki2"),
            """
            CREATE TABLE notes(id INTEGER PRIMARY KEY, guid TEXT);
            CREATE TABLE cards(id INTEGER PRIMARY KEY, nid INTEGER);
            CREATE TABLE col(id INTEGER PRIMARY KEY, ver INTEGER, scm INTEGER);
            INSERT INTO notes(guid) VALUES ('note-1');
            INSERT INTO cards(nid) VALUES (1);
            INSERT INTO col(id, ver, scm) VALUES (1, 18, 18);
            """);
        string programs = Path.Combine(alice, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs");
        Directory.CreateDirectory(programs);
        await File.WriteAllBytesAsync(
            Path.Combine(programs, "Anki.lnk"),
            System.Text.Encoding.Unicode.GetBytes("Anki.exe -b \"" + custom + "\""));
        await context.Database.InsertNodesAsync(
        [
            new PersistedNode(
                1,
                "session-1",
                null,
                null,
                "Anki.lnk",
                @"Users\Alice\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Anki.lnk",
                NodeKind.File,
                0,
                0,
                0,
                DateTime.UtcNow,
                0,
                NodeProblem.None),
        ]);

        DetectResult detected = new AnkiRecipe().Detect(
            new ProfileContext(
                "Alice",
                alice,
                context.Destination,
                context.Temp,
                context.Exports,
                context.SafeFs,
                context.Runner,
                new RecipeIndex(
                    context.Database,
                    "session-1",
                    @"Users\Alice",
                    alice)));
        Assert.Equal("Work", Path.GetFileName(Assert.Single(detected.Cards).Facts["source"]));
    }

    [Fact]
    public async Task Anki_Detect_CountsMediaFromRecipeIndexExcludingTrash()
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
        await File.WriteAllTextAsync(Path.Combine(anki, "collection.media", "image.png"), "media");
        await File.WriteAllTextAsync(Path.Combine(anki, "collection.media", "extra-on-disk.png"), "disk");
        await File.WriteAllTextAsync(Path.Combine(anki, "collection.media", "media.trash", "gone.png"), "trash");
        await context.Database.InsertNodesAsync(
        [
            new PersistedNode(
                1,
                "session-1",
                null,
                null,
                "image.png",
                @"Users\Alice\AppData\Roaming\Anki2\User 1\collection.media\image.png",
                NodeKind.File,
                0,
                0,
                0,
                DateTime.UtcNow,
                0,
                NodeProblem.None),
            new PersistedNode(
                2,
                "session-1",
                null,
                null,
                "gone.png",
                @"Users\Alice\AppData\Roaming\Anki2\User 1\collection.media\media.trash\gone.png",
                NodeKind.File,
                0,
                0,
                0,
                DateTime.UtcNow,
                0,
                NodeProblem.None),
        ]);

        DetectResult fromIndex = new AnkiRecipe().Detect(
            new ProfileContext(
                "Alice",
                alice,
                context.Destination,
                context.Temp,
                context.Exports,
                context.SafeFs,
                context.Runner,
                new RecipeIndex(
                    context.Database,
                    "session-1",
                    @"Users\Alice",
                    alice)));
        Assert.Equal("1", Assert.Single(fromIndex.Cards).Facts["media"]);

        DetectResult fromDisk = new AnkiRecipe().Detect(
            new ProfileContext(
                "Alice",
                alice,
                context.Destination,
                context.Temp,
                context.Exports,
                context.SafeFs,
                context.Runner));
        Assert.Equal("2", Assert.Single(fromDisk.Cards).Facts["media"]);
    }

    [Fact]
    public async Task Wsl_Detect_FindsExt4VhdxFromRecipeIndexWithoutWalkingLocal()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string indexed = Path.Combine(alice, "AppData", "Local", "wsl", "{guid}", "ext4.vhdx");
        string walked = Path.Combine(alice, "AppData", "Local", "Packages", "walked", "ext4.vhdx");
        await context.Database.InsertNodesAsync(
        [
            new PersistedNode(
                1,
                "session-1",
                null,
                null,
                "ext4.vhdx",
                @"Users\Alice\AppData\Local\wsl\{guid}\ext4.vhdx",
                NodeKind.File,
                0,
                0,
                0,
                DateTime.UtcNow,
                0,
                NodeProblem.None),
        ]);

        DetectResult fromIndex = new WslRecipe().Detect(
            new ProfileContext(
                "Alice",
                alice,
                context.Destination,
                context.Temp,
                context.Exports,
                context.SafeFs,
                context.Runner,
                new RecipeIndex(
                    context.Database,
                    "session-1",
                    @"Users\Alice",
                    alice)));
        RecipeCard indexedCard = Assert.Single(fromIndex.Cards);
        Assert.Equal(indexed, indexedCard.Facts["source"]);

        Directory.CreateDirectory(Path.GetDirectoryName(walked)!);
        byte[] vhdx = new byte[512];
        System.Text.Encoding.ASCII.GetBytes("vhdxfile").CopyTo(vhdx, 0);
        await File.WriteAllBytesAsync(walked, vhdx);
        DetectResult fromDisk = new WslRecipe().Detect(
            new ProfileContext(
                "Alice",
                alice,
                context.Destination,
                context.Temp,
                context.Exports,
                context.SafeFs,
                context.Runner));
        string diskSource = Assert.Single(fromDisk.Cards).Facts["source"].Replace('/', '\\');
        Assert.EndsWith(
            @"Packages\walked\ext4.vhdx",
            diskSource,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            fromIndex.Cards,
            card => card.Facts["source"].Replace('/', '\\')
                .EndsWith(@"Packages\walked\ext4.vhdx", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Terminal_Detect_FindsSettingsFromRecipeIndexWithoutWalkingPackages()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string indexed = Path.Combine(
            alice,
            "AppData",
            "Local",
            "Packages",
            "Microsoft.WindowsTerminal_8wekyb3d8bbwe",
            "LocalState",
            "settings.json");
        string walked = Path.Combine(
            alice,
            "AppData",
            "Local",
            "Packages",
            "Microsoft.WindowsTerminal_walked",
            "LocalState",
            "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(walked)!);
        await File.WriteAllTextAsync(walked, "{ \"walked\": true }");

        await context.Database.InsertNodesAsync(
        [
            new PersistedNode(
                1,
                "session-1",
                null,
                null,
                "settings.json",
                @"Users\Alice\AppData\Local\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json",
                NodeKind.File,
                0,
                0,
                0,
                DateTime.UtcNow,
                0,
                NodeProblem.None),
        ]);

        RecipeIndex index = new(context.Database, "session-1", @"Users\Alice", alice);
        DetectResult fromIndex = new TerminalRecipe().Detect(
            new ProfileContext(
                "Alice",
                alice,
                context.Destination,
                context.Temp,
                context.Exports,
                context.SafeFs,
                context.Runner,
                index));
        Assert.Equal(
            indexed,
            Assert.Single(fromIndex.Cards).Facts["source"]);

        DetectResult fromDisk = new TerminalRecipe().Detect(
            new ProfileContext(
                "Alice",
                alice,
                context.Destination,
                context.Temp,
                context.Exports,
                context.SafeFs,
                context.Runner));
        Assert.Contains(
            fromDisk.Cards,
            card => card.Facts["source"].Replace('/', '\\')
                .EndsWith(@"Packages\Microsoft.WindowsTerminal_walked\LocalState\settings.json", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            fromDisk.Cards,
            card => card.Facts["package"]
                .Equals("Microsoft.WindowsTerminal_8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase));
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
        Assert.Equal("1", ankiCard.Facts["media"]);
        Assert.Equal("0", ankiCard.Facts["backups"]);
        Assert.Equal("0", ankiCard.Facts["addons"]);
        PlanResult ankiPlan = host.PlanCard(new AnkiRecipe(), ankiCard, Dest(context));
        await host.ExecuteAsync("session-1", new AnkiRecipe(), ankiPlan);
        Assert.True(new AnkiRecipe().Verify(ankiPlan).Ok);
        Assert.Contains("integrity ok", new AnkiRecipe().Verify(ankiPlan).Detail, StringComparison.OrdinalIgnoreCase);
        string destAnki = Path.Combine(context.Destination, "AppData", "Roaming", "Anki2", "User 1");
        Assert.True(File.Exists(Path.Combine(destAnki, "collection.anki2-wal")));
        Assert.True(File.Exists(Path.Combine(destAnki, "collection.media", "image.png")));
        Assert.False(File.Exists(Path.Combine(destAnki, "collection.media.db2")));
        Assert.False(Directory.Exists(Path.Combine(destAnki, "collection.media", "media.trash")));
        File.WriteAllText(Path.Combine(destAnki, "collection.media", "extra.png"), "extra");
        RecipeVerifyResult extraMedia = new AnkiRecipe().Verify(ankiPlan);
        Assert.False(extraMedia.Ok);
        Assert.Contains("media count", extraMedia.Detail, StringComparison.OrdinalIgnoreCase);

        RecipeCard wslCard = Assert.Single(cards, card => card.RecipeId == "wsl");
        Assert.Equal("512", wslCard.Facts["fileSize"]);
        Assert.True(long.Parse(wslCard.Facts["allocatedSize"]) >= 512);
        Assert.False(string.IsNullOrWhiteSpace(wslCard.Facts["lastModified"]));
        Assert.Equal("1", wslCard.Facts["wslInstalled"]);
        Assert.Equal(Decision.Restore, wslCard.Components.Single(component => component.Key == "register").SuggestedDefault);
        PlanResult wslPlan = host.PlanCard(new WslRecipe(), wslCard, Dest(context));
        await host.ExecuteAsync("session-1", new WslRecipe(), wslPlan);
        Assert.True(new WslRecipe().Verify(wslPlan).Ok);
        Assert.Contains(
            context.Runner.Requests,
            request => request.FileName.Equals("wsl.exe", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.Contains("--version"));
        string recoveredVhdx = Assert.Single(wslPlan.Writes).DestinationPath;
        Assert.Contains("recovered", recoveredVhdx, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Windows.old", recoveredVhdx, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            context.Runner.Requests,
            request => request.FileName.Equals("wsl.exe", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.Contains("--shutdown"));
        ProcessRequest import = Assert.Single(
            context.Runner.Requests,
            request => request.Arguments.Contains("--import-in-place"));
        Assert.Contains(recoveredVhdx, import.Arguments);
        Assert.DoesNotContain(
            context.Runner.Requests,
            request => request.Arguments.Contains("--import-in-place") &&
                request.Arguments.Any(argument => argument.Contains(wsl, StringComparison.OrdinalIgnoreCase)));
        IReadOnlyList<WinOldRecovery.Core.Processes.ProcessRequest> register = WslRecipe.CreateRegisterRequests("Ubuntu", "C:\\tmp\\ext4.vhdx");
        Assert.Equal("wsl.exe", register[0].FileName);
        Assert.Contains("--import-in-place", register[1].Arguments);
        Assert.Contains("C:\\tmp\\ext4.vhdx", register[1].Arguments);
        Assert.DoesNotContain(
            context.Runner.Requests,
            request => request.Arguments.Contains("--vhd"));
        Assert.Contains("getent passwd", wslCard.Facts["defaultUserCommands"], StringComparison.Ordinal);
        Assert.Contains("--set-default-user", wslCard.Facts["defaultUserCommands"], StringComparison.Ordinal);
        IReadOnlyList<WinOldRecovery.Core.Processes.ProcessRequest> defaultUser =
            WslRecipe.CreateDefaultUserRequests("Ubuntu", "1000");
        Assert.Contains("getent", defaultUser[1].Arguments);
        Assert.Contains("1000", defaultUser[1].Arguments);
        Assert.Contains("--terminate", defaultUser[^1].Arguments);
        Assert.DoesNotContain(
            defaultUser,
            request => request.Arguments.Any(static argument =>
                argument.Contains("Windows.old", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(
            context.Runner.Requests,
            request => request.Arguments.Contains("cat") &&
                request.Arguments.Contains("/etc/wsl.conf"));
        Assert.DoesNotContain(
            context.Runner.Requests,
            request => request.Arguments.Contains("getent") ||
                request.Arguments.Contains("--set-default-user") ||
                request.Arguments.Contains("--terminate"));

        RecipeCard gpgCard = Assert.Single(cards, card => card.RecipeId == "gpg");
        Assert.Equal(string.Empty, gpgCard.Facts.GetValueOrDefault("mergeHint"));
        PlanResult gpgPlan = host.PlanCard(new GpgRecipe(), gpgCard, Dest(context));
        await host.ExecuteAsync("session-1", new GpgRecipe(), gpgPlan);
        Assert.True(new GpgRecipe().Verify(gpgPlan).Ok);
        Assert.True(File.Exists(Path.Combine(context.Destination, "AppData", "Roaming", "gnupg", "private-keys-v1.d", "key")));
        Assert.False(File.Exists(Path.Combine(context.Destination, "AppData", "Roaming", "gnupg", "random_seed")));
        Assert.DoesNotContain(
            context.Runner.Requests,
            request => request.FileName.Equals("gpg.exe", StringComparison.OrdinalIgnoreCase));
        RecipeVerifyResult listed = await new GpgRecipe().VerifyAsync(gpgPlan);
        Assert.True(listed.Ok);
        Assert.Contains("list-secret-keys succeeded", listed.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            context.Runner.Requests,
            request => request.FileName == "gpg.exe" &&
                request.Environment is not null &&
                request.Environment.TryGetValue("GNUPGHOME", out string? home) &&
                home is not null &&
                home.EndsWith(Path.Combine("AppData", "Roaming", "gnupg"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Wsl_RegisterLeaveBehindDoesNotImportInPlace()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string wsl = Path.Combine(alice, "AppData", "Local", "wsl", "{guid}", "ext4.vhdx");
        Directory.CreateDirectory(Path.GetDirectoryName(wsl)!);
        byte[] vhdx = new byte[512];
        System.Text.Encoding.ASCII.GetBytes("vhdxfile").CopyTo(vhdx, 0);
        await File.WriteAllBytesAsync(wsl, vhdx);

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
        RecipeCard wslCard = Assert.Single(cards, card => card.RecipeId == "wsl");
        Dictionary<string, Decision> decisions = wslCard.Components.ToDictionary(
            static component => component.Key,
            static component => component.Key is "register" or "ownCopy" ? Decision.LeaveBehind : Decision.Restore);
        PlanResult plan = new WslRecipe().Plan(new CardDecisions(wslCard, decisions), Dest(context))
            with { Destination = Dest(context) };
        Assert.Equal("0", plan.Card.Facts["register"]);
        Assert.Equal("0", plan.Card.Facts["ownCopy"]);
        await host.ExecuteAsync("session-1", new WslRecipe(), plan);
        Assert.DoesNotContain(
            context.Runner.Requests,
            request => request.Arguments.Contains("--import-in-place") ||
                request.Arguments.Contains("--shutdown") ||
                request.Arguments.Contains("--vhd"));
        RecipeVerifyResult verify = await new WslRecipe().VerifyAsync(plan);
        Assert.True(verify.Ok);
        Assert.DoesNotContain(
            context.Runner.Requests,
            request => request.Arguments.Contains("-l") ||
                request.Arguments.Contains("--"));
    }

    [Fact]
    public void Wsl_ParsesWslConfDefaultAndPasswdName()
    {
        Assert.True(WslRecipe.TryParseWslConfUserDefault("[user]\ndefault=alice\n", out string confUser));
        Assert.Equal("alice", confUser);
        Assert.False(WslRecipe.TryParseWslConfUserDefault("[boot]\nsystemd=true\n", out _));
        Assert.True(WslRecipe.TryParsePasswdName("alice:x:1000:1000::/home/alice:/bin/bash", out string passwdUser));
        Assert.Equal("alice", passwdUser);
        Assert.False(WslRecipe.TryParsePasswdName("alice;id:x:1000:1000::/home/alice:/bin/bash", out _));
        Assert.False(WslRecipe.IsSafeLinuxUserName("Alice"));
        Assert.False(WslRecipe.IsSafeLinuxUserName("alice;id"));
        Assert.Equal("Ubuntu", WslRecipe.ChooseRegisteredDistroName("Ubuntu", string.Empty));
        Assert.Equal(
            "Ubuntu-recovered",
            WslRecipe.ChooseRegisteredDistroName("Ubuntu", "* Ubuntu  Running  2\nDebian  Stopped  2\n"));
        Assert.Equal(
            "Ubuntu-recovered",
            WslRecipe.ChooseRegisteredDistroName("Ubuntu", "U\0b\0u\0n\0t\0u\0 \0R\0u\0n\0n\0i\0n\0g\0"));
    }

    [Fact]
    public async Task Wsl_Register_SuffixesWhenNameAlreadyListed()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string wsl = Path.Combine(alice, "AppData", "Local", "wsl", "{guid}", "ext4.vhdx");
        Directory.CreateDirectory(Path.GetDirectoryName(wsl)!);
        byte[] vhdx = new byte[512];
        System.Text.Encoding.ASCII.GetBytes("vhdxfile").CopyTo(vhdx, 0);
        await File.WriteAllBytesAsync(wsl, vhdx);

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
        RecipeCard wslCard = Assert.Single(cards, card => card.RecipeId == "wsl");
        PlanResult planned = host.PlanCard(new WslRecipe(), wslCard, Dest(context));
        ScriptedWslRunner runner = new()
        {
            ListStdout = "Ubuntu  Running  2\n",
            GetentStdout = "alice:x:1000:1000::/home/alice:/bin/bash\n",
        };
        Dictionary<string, string> facts = new(planned.Card.Facts, StringComparer.Ordinal)
        {
            ["name"] = "Ubuntu",
            ["defaultUid"] = "1000",
        };
        PlanResult plan = planned with
        {
            Card = planned.Card with { Facts = facts },
            Destination = new DestinationContext(context.Destination, context.Exports, context.SafeFs, runner),
        };
        await host.ExecuteAsync("session-1", new WslRecipe(), plan);
        ProcessRequest import = Assert.Single(
            runner.Requests,
            request => request.Arguments.Contains("--import-in-place"));
        Assert.Contains("Ubuntu-recovered", import.Arguments);
        Assert.DoesNotContain(
            import.Arguments,
            static argument => argument.Equals("Ubuntu", StringComparison.Ordinal));
        Assert.Equal("Ubuntu-recovered", plan.Card.Facts["registerName"]);
        Assert.Contains(
            runner.Requests,
            request => request.Arguments.Contains("--set-default-user") &&
                request.Arguments.Contains("Ubuntu-recovered"));
    }

    [Fact]
    public async Task Wsl_OwnCopy_UsesImportVhdNotInPlace()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string wsl = Path.Combine(alice, "AppData", "Local", "wsl", "{guid}", "ext4.vhdx");
        Directory.CreateDirectory(Path.GetDirectoryName(wsl)!);
        byte[] vhdx = new byte[512];
        System.Text.Encoding.ASCII.GetBytes("vhdxfile").CopyTo(vhdx, 0);
        await File.WriteAllBytesAsync(wsl, vhdx);

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
        RecipeCard wslCard = Assert.Single(cards, card => card.RecipeId == "wsl");
        Assert.Equal(Decision.LeaveBehind, wslCard.Components.Single(component => component.Key == "ownCopy").SuggestedDefault);
        Dictionary<string, Decision> decisions = wslCard.Components.ToDictionary(
            static component => component.Key,
            static component => component.Key == "ownCopy" ? Decision.Restore : component.Key == "register" ? Decision.LeaveBehind : Decision.Restore);
        PlanResult planned = new WslRecipe().Plan(new CardDecisions(wslCard, decisions), Dest(context));
        Assert.Equal("1", planned.Card.Facts["ownCopy"]);
        Assert.Equal("0", planned.Card.Facts["register"]);
        ScriptedWslRunner runner = new();
        PlanResult plan = planned with
        {
            Destination = new DestinationContext(context.Destination, context.Exports, context.SafeFs, runner),
        };
        await host.ExecuteAsync("session-1", new WslRecipe(), plan);
        ProcessRequest import = Assert.Single(
            runner.Requests,
            request => request.Arguments.Contains("--import") && request.Arguments.Contains("--vhd"));
        Assert.Contains(Assert.Single(planned.Writes).DestinationPath, import.Arguments);
        Assert.Contains(
            import.Arguments,
            static argument => argument.Contains(Path.Combine("wsl", "owned"), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            runner.Requests,
            request => request.Arguments.Contains("--import-in-place"));
        IReadOnlyList<ProcessRequest> owned = WslRecipe.CreateOwnedImportRequests(
            "Ubuntu",
            @"C:\Users\VJ\AppData\Local\wsl\owned\Ubuntu",
            @"C:\tmp\ext4.vhdx");
        Assert.Contains("--import", owned[1].Arguments);
        Assert.Contains("--vhd", owned[1].Arguments);
        Assert.DoesNotContain("--import-in-place", owned[1].Arguments);
    }

    [Fact]
    public async Task Wsl_DefaultUser_SetsFromGetentWhenConfEmpty()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string wsl = Path.Combine(alice, "AppData", "Local", "wsl", "{guid}", "ext4.vhdx");
        Directory.CreateDirectory(Path.GetDirectoryName(wsl)!);
        byte[] vhdx = new byte[512];
        System.Text.Encoding.ASCII.GetBytes("vhdxfile").CopyTo(vhdx, 0);
        await File.WriteAllBytesAsync(wsl, vhdx);

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
        RecipeCard wslCard = Assert.Single(cards, card => card.RecipeId == "wsl");
        PlanResult planned = host.PlanCard(new WslRecipe(), wslCard, Dest(context));
        ScriptedWslRunner runner = new()
        {
            ConfStdout = string.Empty,
            GetentStdout = "alice:x:1000:1000::/home/alice:/bin/bash\n",
        };
        Dictionary<string, string> facts = new(planned.Card.Facts, StringComparer.Ordinal) { ["defaultUid"] = "1000" };
        PlanResult plan = planned with
        {
            Card = planned.Card with { Facts = facts },
            Destination = new DestinationContext(context.Destination, context.Exports, context.SafeFs, runner),
        };
        await host.ExecuteAsync("session-1", new WslRecipe(), plan);
        Assert.Contains(
            runner.Requests,
            request => request.Arguments.Contains("getent") && request.Arguments.Contains("1000"));
        ProcessRequest manage = Assert.Single(
            runner.Requests,
            request => request.Arguments.Contains("--set-default-user"));
        Assert.Contains("alice", manage.Arguments);
        Assert.Contains(
            runner.Requests,
            request => request.Arguments.Contains("--terminate"));
        Assert.DoesNotContain(
            runner.Requests,
            request => request.Arguments.Contains("sh"));
    }

    [Fact]
    public async Task Wsl_DefaultUser_SkipsWhenWslConfAlreadyHasDefault()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string wsl = Path.Combine(alice, "AppData", "Local", "wsl", "{guid}", "ext4.vhdx");
        Directory.CreateDirectory(Path.GetDirectoryName(wsl)!);
        byte[] vhdx = new byte[512];
        System.Text.Encoding.ASCII.GetBytes("vhdxfile").CopyTo(vhdx, 0);
        await File.WriteAllBytesAsync(wsl, vhdx);

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
        RecipeCard wslCard = Assert.Single(cards, card => card.RecipeId == "wsl");
        PlanResult planned = host.PlanCard(new WslRecipe(), wslCard, Dest(context));
        ScriptedWslRunner runner = new() { ConfStdout = "[user]\ndefault=bob\n" };
        Dictionary<string, string> facts = new(planned.Card.Facts, StringComparer.Ordinal) { ["defaultUid"] = "1000" };
        PlanResult plan = planned with
        {
            Card = planned.Card with { Facts = facts },
            Destination = new DestinationContext(context.Destination, context.Exports, context.SafeFs, runner),
        };
        await host.ExecuteAsync("session-1", new WslRecipe(), plan);
        Assert.Contains(
            runner.Requests,
            request => request.Arguments.Contains("cat") && request.Arguments.Contains("/etc/wsl.conf"));
        Assert.DoesNotContain(
            runner.Requests,
            request => request.Arguments.Contains("getent") ||
                request.Arguments.Contains("--set-default-user") ||
                request.Arguments.Contains("--terminate"));
    }

    [Fact]
    public async Task Wsl_DefaultUser_FallsBackToWslConfWhenManageFails()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string wsl = Path.Combine(alice, "AppData", "Local", "wsl", "{guid}", "ext4.vhdx");
        Directory.CreateDirectory(Path.GetDirectoryName(wsl)!);
        byte[] vhdx = new byte[512];
        System.Text.Encoding.ASCII.GetBytes("vhdxfile").CopyTo(vhdx, 0);
        await File.WriteAllBytesAsync(wsl, vhdx);

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
        RecipeCard wslCard = Assert.Single(cards, card => card.RecipeId == "wsl");
        PlanResult planned = host.PlanCard(new WslRecipe(), wslCard, Dest(context));
        ScriptedWslRunner runner = new()
        {
            GetentStdout = "alice:x:1000:1000::/home/alice:/bin/bash\n",
            ManageExit = 1,
        };
        Dictionary<string, string> facts = new(planned.Card.Facts, StringComparer.Ordinal) { ["defaultUid"] = "1000" };
        PlanResult plan = planned with
        {
            Card = planned.Card with { Facts = facts },
            Destination = new DestinationContext(context.Destination, context.Exports, context.SafeFs, runner),
        };
        await host.ExecuteAsync("session-1", new WslRecipe(), plan);
        ProcessRequest append = Assert.Single(
            runner.Requests,
            request => request.Arguments.Contains("sh"));
        Assert.Contains(
            append.Arguments,
            static argument => argument.Contains("default=alice", StringComparison.Ordinal));
        Assert.Contains(
            runner.Requests,
            request => request.Arguments.Contains("--terminate"));
    }

    [Fact]
    public async Task Wsl_VerifyAsync_RegisteredRecordsListAndTrue()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string wsl = Path.Combine(alice, "AppData", "Local", "wsl", "{guid}", "ext4.vhdx");
        Directory.CreateDirectory(Path.GetDirectoryName(wsl)!);
        byte[] vhdx = new byte[512];
        System.Text.Encoding.ASCII.GetBytes("vhdxfile").CopyTo(vhdx, 0);
        await File.WriteAllBytesAsync(wsl, vhdx);

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
        RecipeCard wslCard = Assert.Single(cards, card => card.RecipeId == "wsl");
        PlanResult plan = host.PlanCard(new WslRecipe(), wslCard, Dest(context));
        await host.ExecuteAsync("session-1", new WslRecipe(), plan);
        RecipeVerifyResult result = await new WslRecipe().VerifyAsync(plan);
        Assert.True(result.Ok);
        Assert.Contains("; registered", result.Detail);
        Assert.Contains(
            context.Runner.Requests,
            request => request.Arguments.Contains("-l") && request.Arguments.Contains("-v"));
        Assert.Contains(
            context.Runner.Requests,
            request => request.Arguments.Contains("-d") &&
                request.Arguments.Contains("root") &&
                request.Arguments.Contains("true"));
    }

    [Fact]
    public async Task Wsl_VerifyAsync_RegisteredFailsWhenWslExitsOne()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string wsl = Path.Combine(alice, "AppData", "Local", "wsl", "{guid}", "ext4.vhdx");
        Directory.CreateDirectory(Path.GetDirectoryName(wsl)!);
        byte[] vhdx = new byte[512];
        System.Text.Encoding.ASCII.GetBytes("vhdxfile").CopyTo(vhdx, 0);
        await File.WriteAllBytesAsync(wsl, vhdx);

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
        RecipeCard wslCard = Assert.Single(cards, card => card.RecipeId == "wsl");
        PlanResult plan = host.PlanCard(new WslRecipe(), wslCard, Dest(context));
        await host.ExecuteAsync("session-1", new WslRecipe(), plan);
        PlanResult failing = plan with
        {
            Destination = new DestinationContext(
                context.Destination,
                context.Exports,
                context.SafeFs,
                new ExitOneRunner()),
        };
        RecipeVerifyResult result = await new WslRecipe().VerifyAsync(failing);
        Assert.False(result.Ok);
        Assert.Contains("wsl -l -v exited 1", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gpg_MergeHintWhenDestinationRingExists()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string gpg = Path.Combine(alice, "AppData", "Roaming", "gnupg");
        Directory.CreateDirectory(Path.Combine(gpg, "private-keys-v1.d"));
        await File.WriteAllTextAsync(Path.Combine(gpg, "pubring.kbx"), "pub");
        Directory.CreateDirectory(Path.Combine(context.Destination, "AppData", "Roaming", "gnupg"));
        await File.WriteAllTextAsync(
            Path.Combine(context.Destination, "AppData", "Roaming", "gnupg", "pubring.kbx"),
            "existing");

        DetectResult detected = new GpgRecipe().Detect(
            new ProfileContext(
                "Alice",
                alice,
                context.Destination,
                context.Temp,
                context.Exports,
                context.SafeFs,
                context.Runner));
        RecipeCard card = Assert.Single(detected.Cards);
        Assert.Equal("gpg --import", card.Facts["mergeHint"]);
        Assert.Contains("gpg --import", card.WhatIsRestored, StringComparison.Ordinal);
        PlanResult plan = new GpgRecipe().Plan(
            new CardDecisions(card, new Dictionary<string, Decision> { ["ring"] = Decision.Restore }),
            Dest(context));
        Assert.Contains(
            plan.Writes,
            write => write.DestinationPath.Contains("gnupg.from-windows-old", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Gpg_VerifyAsync_FailsWhenListSecretKeysExitsNonZero()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string destKey = Path.Combine(context.Destination, "AppData", "Roaming", "gnupg", "pubring.kbx");
        Directory.CreateDirectory(Path.GetDirectoryName(destKey)!);
        await File.WriteAllTextAsync(destKey, "pub");
        RecipeCard card = new(
            "gpg",
            "GPG",
            "what",
            "why",
            "restored",
            "cloud",
            "regen",
            "left",
            [],
            "gpg:test",
            new Dictionary<string, string>());
        PlanResult plan = new(
            card,
            [new RecipeWrite(RecipeWriteKind.CopyFile, destKey, destKey, null, 1, "ring")],
            new DestinationContext(context.Destination, context.Exports, context.SafeFs, new ExitOneRunner()));
        RecipeVerifyResult result = await new GpgRecipe().VerifyAsync(plan);
        Assert.False(result.Ok);
        Assert.Contains("list-secret-keys failed", result.Detail, StringComparison.OrdinalIgnoreCase);
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
            Directory.CreateDirectory(Path.Combine(context.Destination, "AppData", "Local", "Google", "Chrome", "User Data"));
            await File.WriteAllTextAsync(
                Path.Combine(context.Destination, "AppData", "Local", "Google", "Chrome", "User Data", "Last Version"),
                "131.0.6778.86\n");
            await File.WriteAllTextAsync(
                Path.Combine(context.Destination, "AppData", "Local", "Google", "Chrome", "User Data", "Local State"),
                """{"profile":{"info_cache":{"Default":{"name":"Person 1"}}},"os_crypt":{"encrypted_key":"DEST_OS_CRYPT"}}""");
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
            string destUserData = Path.Combine(
                context.Destination,
                "AppData",
                "Local",
                "Google",
                "Chrome",
                "User Data");
            Assert.True(File.Exists(Path.Combine(destUserData, "Profile 1", "Bookmarks")));
            Assert.True(File.Exists(Path.Combine(destUserData, "Local State.winold-bak")));
            Assert.Equal("Default", flaggedCard.Facts["displayName"]);
            string destLocalState = await File.ReadAllTextAsync(Path.Combine(destUserData, "Local State"));
            Assert.Contains("Default (recovered)", destLocalState, StringComparison.Ordinal);
            Assert.Contains("DEST_OS_CRYPT", destLocalState, StringComparison.Ordinal);
            Assert.DoesNotContain(Canary, destLocalState, StringComparison.Ordinal);
            Assert.True(recipe.Verify(transplantPlan).Ok);
        }
        finally
        {
            ChromiumRecipe.NewProfileTransplantEnabled = false;
        }
    }

    [Fact]
    public async Task Chromium_Transplant_DisabledWhenDestLastVersionOlder()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string chrome = Path.Combine(alice, "AppData", "Local", "Google", "Chrome", "User Data", "Default");
        Directory.CreateDirectory(chrome);
        await File.WriteAllTextAsync(
            Path.Combine(alice, "AppData", "Local", "Google", "Chrome", "User Data", "Last Version"),
            "131.0.6778.86\n");
        await File.WriteAllTextAsync(
            Path.Combine(chrome, "Bookmarks"),
            """{"roots":{"bookmark_bar":{"children":[{"type":"url","url":"https://example.com"}]}}}""");
        Directory.CreateDirectory(Path.Combine(context.Destination, "AppData", "Local", "Google", "Chrome", "User Data"));
        await File.WriteAllTextAsync(
            Path.Combine(context.Destination, "AppData", "Local", "Google", "Chrome", "User Data", "Last Version"),
            "120.0.6099.109\n");

        ChromiumRecipe.NewProfileTransplantEnabled = true;
        try
        {
            RecipeCard card = Assert.Single(
                new ChromiumRecipe("chrome", "Chrome", Path.Combine("AppData", "Local", "Google", "Chrome", "User Data"))
                    .Detect(
                        new ProfileContext(
                            "Alice",
                            alice,
                            context.Destination,
                            context.Temp,
                            context.Exports,
                            context.SafeFs,
                            context.Runner)).Cards);
            RecipeComponent transplant = card.Components.Single(component => component.Key == "bookmarks-transplant");
            Assert.True(transplant.Fixed);
            Assert.Contains("older than the source", transplant.FixedReason);
            Dictionary<string, Decision> forced = card.Components.ToDictionary(
                static component => component.Key,
                static component => component.Key == "bookmarks-transplant" ? Decision.Restore : Decision.Undecided);
            PlanResult plan = new ChromiumRecipe(
                    "chrome",
                    "Chrome",
                    Path.Combine("AppData", "Local", "Google", "Chrome", "User Data"))
                .Plan(new CardDecisions(card, forced), Dest(context));
            Assert.DoesNotContain(plan.Writes, write => write.ComponentKey == "bookmarks-transplant");
        }
        finally
        {
            ChromiumRecipe.NewProfileTransplantEnabled = false;
        }
    }

    [Fact]
    public async Task Thunderbird_Detect_UsesIniAbsolutePathAndIgnoresOutside()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string thunderbird = Path.Combine(alice, "AppData", "Roaming", "Thunderbird");
        Directory.CreateDirectory(thunderbird);
        string relocated = Path.Combine(alice, "Documents", "relocated.thunderbird");
        Directory.CreateDirectory(Path.Combine(relocated, "Mail"));
        await File.WriteAllTextAsync(Path.Combine(relocated, "prefs.js"), "user_pref(\"fixture\", true);");
        await File.WriteAllTextAsync(Path.Combine(relocated, "Mail", "Inbox"), "mail");
        string outside = Path.Combine(context.Root, "outside.thunderbird");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "prefs.js"), "no");
        await File.WriteAllTextAsync(
            Path.Combine(thunderbird, "profiles.ini"),
            $"""
            [Profile0]
            Name=relocated
            IsRelative=0
            Path={relocated}

            [Profile1]
            Name=sneaky
            IsRelative=0
            Path={outside}
            """);

        DetectResult detected = new ThunderbirdRecipe().Detect(
            new ProfileContext(
                "Alice",
                alice,
                context.Destination,
                context.Temp,
                context.Exports,
                context.SafeFs,
                context.Runner));
        RecipeCard card = Assert.Single(detected.Cards);
        Assert.Equal("relocated", card.Facts["name"]);
        Assert.Equal(relocated, card.Facts["source"]);
        Assert.DoesNotContain(detected.Cards, item => item.Facts.GetValueOrDefault("name") == "sneaky");
        RecipeHost host = new(context.Database, context.SafeFs, context.Runner, [new ThunderbirdRecipe()]);
        PlanResult plan = host.PlanCard(new ThunderbirdRecipe(), card, Dest(context));
        await host.ExecuteAsync("session-1", new ThunderbirdRecipe(), plan);
        Assert.True(new ThunderbirdRecipe().Verify(plan).Ok);
        string destIni = Path.Combine(
            context.Destination,
            "AppData",
            "Roaming",
            "Thunderbird",
            "profiles.ini");
        Assert.Contains("relocated.thunderbird-recovered", await File.ReadAllTextAsync(destIni), StringComparison.Ordinal);
        Assert.True(File.Exists(
            Path.Combine(
                context.Destination,
                "AppData",
                "Roaming",
                "Thunderbird",
                "Profiles",
                "relocated.thunderbird-recovered",
                "prefs.js")));
        string destProfile = Path.Combine(
            context.Destination,
            "AppData",
            "Roaming",
            "Thunderbird",
            "Profiles",
            "relocated.thunderbird-recovered");
        await File.WriteAllTextAsync(Path.Combine(destProfile, "key4.db"), "key");
        RecipeVerifyResult unpaired = new ThunderbirdRecipe().Verify(plan);
        Assert.False(unpaired.Ok);
        Assert.Contains("key4.db", unpaired.Detail, StringComparison.OrdinalIgnoreCase);
        File.Delete(Path.Combine(destProfile, "key4.db"));
        await File.WriteAllTextAsync(destIni, "[General]\nStartWithLastProfile=1\n");
        RecipeVerifyResult unlisted = new ThunderbirdRecipe().Verify(plan);
        Assert.False(unlisted.Ok);
        Assert.Contains("profiles.ini", unlisted.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Thunderbird_Verify_AcceptsLoginsDbPairedWithKey4()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string profile = Path.Combine(alice, "AppData", "Roaming", "Thunderbird", "Profiles", "mail.loginsdb");
        Directory.CreateDirectory(Path.Combine(profile, "Mail"));
        await File.WriteAllTextAsync(Path.Combine(profile, "prefs.js"), "user_pref(\"fixture\", true);");
        await File.WriteAllTextAsync(Path.Combine(profile, "key4.db"), "key");
        await File.WriteAllTextAsync(Path.Combine(profile, "logins.db"), "logins");
        await File.WriteAllTextAsync(Path.Combine(profile, "Mail", "Inbox"), "mail");

        DetectResult detected = new ThunderbirdRecipe().Detect(
            new ProfileContext(
                "Alice",
                alice,
                context.Destination,
                context.Temp,
                context.Exports,
                context.SafeFs,
                context.Runner));
        RecipeCard card = Assert.Single(detected.Cards);
        Assert.Contains("logins.db", card.Facts["files"], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("logins.json", card.Facts["files"], StringComparison.OrdinalIgnoreCase);
        RecipeHost host = new(context.Database, context.SafeFs, context.Runner, [new ThunderbirdRecipe()]);
        PlanResult plan = host.PlanCard(new ThunderbirdRecipe(), card, Dest(context));
        await host.ExecuteAsync("session-1", new ThunderbirdRecipe(), plan);
        Assert.True(new ThunderbirdRecipe().Verify(plan).Ok);
        Assert.True(
            File.Exists(
                Path.Combine(
                    context.Destination,
                    "AppData",
                    "Roaming",
                    "Thunderbird",
                    "Profiles",
                    "mail.loginsdb-recovered",
                    "logins.db")));
        Assert.False(
            File.Exists(
                Path.Combine(
                    context.Destination,
                    "AppData",
                    "Roaming",
                    "Thunderbird",
                    "Profiles",
                    "mail.loginsdb-recovered",
                    "logins.json")));
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
        await File.WriteAllTextAsync(Path.Combine(thunder, "logins.json"), "{}");
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
        await File.WriteAllTextAsync(Path.Combine(alice, "Documents", "Notes", ".obsidian", "app.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(alice, "Documents", "Notes", "welcome.md"), "hello");
        Directory.CreateDirectory(Path.Combine(alice, "Desktop"));
        await File.WriteAllTextAsync(Path.Combine(alice, "Desktop", "backup.pst"), "desktop-pst");
        Directory.CreateDirectory(Path.Combine(alice, "Documents", "Outlook Files"));
        await File.WriteAllTextAsync(Path.Combine(alice, "Documents", "Outlook Files", "archive.pst"), "pst");
        Directory.CreateDirectory(Path.Combine(alice, "AppData", "Local", "Microsoft", "Outlook"));
        await File.WriteAllTextAsync(Path.Combine(alice, "AppData", "Local", "Microsoft", "Outlook", "user.ost"), "ost");
        Directory.CreateDirectory(Path.Combine(alice, "Saved Games", "SomeTitle"));
        await File.WriteAllTextAsync(Path.Combine(alice, "Saved Games", "SomeTitle", "slot.sav"), "save");
        Directory.CreateDirectory(Path.Combine(alice, "AppData", "Roaming", "Goldberg SteamEmu Saves", "AppId"));
        await File.WriteAllTextAsync(
            Path.Combine(alice, "AppData", "Roaming", "Goldberg SteamEmu Saves", "AppId", "achievements.json"),
            "{}");
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

        ProfileContext profile = new(
            "Alice",
            alice,
            context.Destination,
            context.Temp,
            context.Exports,
            context.SafeFs,
            context.Runner);
        DetectResult keepassDetected = new KeePassRecipe().Detect(profile);
        Assert.Contains(
            keepassDetected.Badges,
            badge => badge.Kind == "KeePass" && badge.Detail == "fixture.kdbx");
        Assert.Contains(new VsCodeRecipe().Detect(profile).Badges, badge => badge.Kind == "VS Code");
        Assert.Contains(
            new ThunderbirdRecipe().Detect(profile).Badges,
            badge => badge.Kind == "Thunderbird" && badge.Detail == "mail.default");
        Assert.Contains(
            new TerminalRecipe().Detect(profile).Badges,
            badge => badge.Kind == "Windows Terminal" && badge.Detail == "settings");
        Assert.Contains(
            new ObsidianRecipe().Detect(profile).Badges,
            badge => badge.Kind == "Obsidian" && badge.Detail == "Notes");
        DetectResult outlookDetected = new OutlookRecipe().Detect(profile);
        Assert.Contains(
            outlookDetected.Badges,
            badge => badge.Kind == "Outlook" && badge.Detail == "archive.pst");
        Assert.Contains(
            outlookDetected.Badges,
            badge => badge.Kind == "Outlook" && badge.Detail == "backup.pst");
        Assert.Contains(
            outlookDetected.Badges,
            badge => badge.Kind == "Outlook" && badge.Detail == "user.ost");
        string highValueBadgeDump = string.Join(
            ';',
            keepassDetected.Badges
                .Concat(outlookDetected.Badges)
                .Select(badge => badge.RelativePath + badge.Kind + badge.Detail));
        Assert.DoesNotContain(Canary, highValueBadgeDump, StringComparison.Ordinal);

        RecipeCard keepass = Assert.Single(cards, card => card.RecipeId == "keepass");
        PlanResult keepassPlan = host.PlanCard(new KeePassRecipe(), keepass, Dest(context));
        await host.ExecuteAsync("session-1", new KeePassRecipe(), keepassPlan);
        Assert.True(new KeePassRecipe().Verify(keepassPlan).Ok);
        Assert.True(File.Exists(Path.Combine(context.Destination, "Documents", "Passwords", "fixture.kdbx")));
        string destKeyx = Path.Combine(context.Destination, "Documents", "Passwords", "fixture.keyx");
        Assert.True(File.Exists(destKeyx));
        File.Delete(destKeyx);
        RecipeVerifyResult missingKey = new KeePassRecipe().Verify(keepassPlan);
        Assert.False(missingKey.Ok);
        Assert.Contains("key", missingKey.Detail, StringComparison.OrdinalIgnoreCase);
        await File.WriteAllTextAsync(destKeyx, "key");
        Assert.True(new KeePassRecipe().Verify(keepassPlan).Ok);
        string destVault = Path.Combine(context.Destination, "Documents", "Passwords", "fixture.kdbx");
        await File.WriteAllTextAsync(destVault, "truncated");
        RecipeVerifyResult truncatedVault = new KeePassRecipe().Verify(keepassPlan);
        Assert.False(truncatedVault.Ok);
        Assert.Contains("size", truncatedVault.Detail, StringComparison.OrdinalIgnoreCase);

        RecipeCard vscode = Assert.Single(cards, card => card.RecipeId == "vscode");
        PlanResult vscodePlan = host.PlanCard(new VsCodeRecipe(), vscode, Dest(context));
        await host.ExecuteAsync("session-1", new VsCodeRecipe(), vscodePlan);
        Assert.True(new VsCodeRecipe().Verify(vscodePlan).Ok);
        Assert.True(File.Exists(Path.Combine(context.Destination, "AppData", "Roaming", "Code", "User", "settings.json")));
        string cmd = await File.ReadAllTextAsync(Path.Combine(context.Exports, "install-extensions-code.cmd"));
        Assert.Contains("code --install-extension ms-python.python", cmd, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, cmd, StringComparison.Ordinal);
        string cmdPath = Path.Combine(context.Exports, "install-extensions-code.cmd");
        await File.WriteAllTextAsync(cmdPath, "@echo off\n");
        RecipeVerifyResult missingExt = new VsCodeRecipe().Verify(vscodePlan);
        Assert.False(missingExt.Ok);
        Assert.Contains("install-extensions", missingExt.Detail, StringComparison.OrdinalIgnoreCase);

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
        string destSettings = Path.Combine(context.Destination, "AppData", "Local", "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.json");
        Assert.True(File.Exists(Path.Combine(context.Destination, "AppData", "Local", "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.from-windows-old.json")));
        Assert.Contains("existing", await File.ReadAllTextAsync(destSettings), StringComparison.Ordinal);
        string terminalSource = Assert.Single(terminalPlan.Writes).SourcePath!;
        await File.WriteAllTextAsync(destSettings, await File.ReadAllTextAsync(terminalSource));
        RecipeVerifyResult clobberedSettings = new TerminalRecipe().Verify(terminalPlan);
        Assert.False(clobberedSettings.Ok);
        Assert.Contains("overwrote", clobberedSettings.Detail, StringComparison.OrdinalIgnoreCase);
        await File.WriteAllTextAsync(destSettings, """{"existing":true}""");
        Assert.True(new TerminalRecipe().Verify(terminalPlan).Ok);
        string destFromOld = Path.Combine(
            context.Destination,
            "AppData",
            "Local",
            "Packages",
            "Microsoft.WindowsTerminal_8wekyb3d8bbwe",
            "LocalState",
            "settings.from-windows-old.json");
        byte[] originalFromOld = await File.ReadAllBytesAsync(destFromOld);
        await File.WriteAllTextAsync(destFromOld, "truncated");
        RecipeVerifyResult truncatedSettings = new TerminalRecipe().Verify(terminalPlan);
        Assert.False(truncatedSettings.Ok);
        Assert.Contains("size", truncatedSettings.Detail, StringComparison.OrdinalIgnoreCase);
        await File.WriteAllBytesAsync(destFromOld, originalFromOld);
        Assert.True(new TerminalRecipe().Verify(terminalPlan).Ok);

        RecipeCard obsidian = Assert.Single(cards, card => card.RecipeId == "obsidian");
        PlanResult obsidianPlan = host.PlanCard(new ObsidianRecipe(), obsidian, Dest(context));
        await host.ExecuteAsync("session-1", new ObsidianRecipe(), obsidianPlan);
        Assert.True(new ObsidianRecipe().Verify(obsidianPlan).Ok);
        string welcome = Path.Combine(context.Destination, "Documents", "Notes", "welcome.md");
        Assert.True(File.Exists(welcome));
        byte[] originalWelcome = await File.ReadAllBytesAsync(welcome);
        await File.WriteAllTextAsync(welcome, "truncated");
        RecipeVerifyResult truncatedNotes = new ObsidianRecipe().Verify(obsidianPlan);
        Assert.False(truncatedNotes.Ok);
        Assert.Contains("size", truncatedNotes.Detail, StringComparison.OrdinalIgnoreCase);
        await File.WriteAllBytesAsync(welcome, originalWelcome);
        Assert.True(new ObsidianRecipe().Verify(obsidianPlan).Ok);
        Directory.Delete(Path.Combine(context.Destination, "Documents", "Notes", ".obsidian"), true);
        PlanResult notesWithoutConfig = obsidianPlan with
        {
            Writes = obsidianPlan.Writes
                .Where(static write =>
                    write.DestinationPath.IndexOf(".obsidian", StringComparison.OrdinalIgnoreCase) < 0)
                .ToArray(),
        };
        RecipeVerifyResult missingVaultConfig = new ObsidianRecipe().Verify(notesWithoutConfig);
        Assert.False(missingVaultConfig.Ok);
        Assert.Contains(".obsidian", missingVaultConfig.Detail, StringComparison.Ordinal);

        Assert.Equal(2, cards.Count(card => card.RecipeId == "outlook" && card.Facts["kind"] == "pst"));
        RecipeCard archivePst = Assert.Single(
            cards,
            card => card.Title.Contains("archive.pst", StringComparison.OrdinalIgnoreCase));
        PlanResult pstPlan = host.PlanCard(new OutlookRecipe(), archivePst, Dest(context));
        await host.ExecuteAsync("session-1", new OutlookRecipe(), pstPlan);
        Assert.True(new OutlookRecipe().Verify(pstPlan).Ok);
        Assert.True(File.Exists(Path.Combine(context.Destination, "Documents", "Outlook Files", "archive.pst")));
        RecipeCard desktopPst = Assert.Single(
            cards,
            card => card.Title.Contains("backup.pst", StringComparison.OrdinalIgnoreCase));
        PlanResult desktopPlan = host.PlanCard(new OutlookRecipe(), desktopPst, Dest(context));
        await host.ExecuteAsync("session-1", new OutlookRecipe(), desktopPlan);
        Assert.True(File.Exists(Path.Combine(context.Destination, "Desktop", "backup.pst")));

        RecipeCard ost = Assert.Single(cards, card => card.Title.Contains("OST", StringComparison.Ordinal));
        PlanResult ostPlan = host.PlanCard(new OutlookRecipe(), ost, Dest(context));
        Assert.Empty(ostPlan.Writes);
        RecipeVerifyResult ostVerify = new OutlookRecipe().Verify(ostPlan);
        Assert.True(ostVerify.Ok);
        Assert.Contains("OST", ostVerify.Detail, StringComparison.Ordinal);
        PlanResult leakedOst = ostPlan with
        {
            Writes =
            [
                new RecipeWrite(
                    RecipeWriteKind.CopyFile,
                    ost.Facts["source"],
                    Path.Combine(context.Destination, "AppData", "Local", "Microsoft", "Outlook", "user.ost"),
                    null,
                    1,
                    "mail"),
            ],
        };
        RecipeVerifyResult ostCopied = new OutlookRecipe().Verify(leakedOst);
        Assert.False(ostCopied.Ok);
        Assert.Contains("OST", ostCopied.Detail, StringComparison.Ordinal);

        RecipeCard savedGames = Assert.Single(
            cards,
            card => card.RecipeId == "game-saves" && card.Title.Contains("Saved Games", StringComparison.Ordinal));
        PlanResult gamesPlan = host.PlanCard(new GameSavesRecipe(), savedGames, Dest(context));
        await host.ExecuteAsync("session-1", new GameSavesRecipe(), gamesPlan);
        Assert.True(new GameSavesRecipe().Verify(gamesPlan).Ok);
        Assert.True(File.Exists(Path.Combine(context.Destination, "Saved Games", "SomeTitle", "slot.sav")));
        string restoredSave = Path.Combine(context.Destination, "Saved Games", "SomeTitle", "slot.sav");
        await File.WriteAllTextAsync(restoredSave, "truncated");
        RecipeVerifyResult truncated = new GameSavesRecipe().Verify(gamesPlan);
        Assert.False(truncated.Ok);
        Assert.Contains("size", truncated.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            cards,
            card => card.RecipeId == "game-saves" && card.Title.Contains("Steam-compatible", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GameSaves_Detect_FindsSteamUserdataAndLocalLowSaves()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string steam = Path.Combine(context.Source, "Program Files (x86)", "Steam", "userdata", "12345", "760");
        Directory.CreateDirectory(steam);
        await File.WriteAllTextAsync(Path.Combine(steam, "remote.sav"), "steam");
        string celeste = Path.Combine(
            context.Source,
            "Program Files (x86)",
            "Steam",
            "steamapps",
            "common",
            "Celeste",
            "saves");
        Directory.CreateDirectory(celeste);
        await File.WriteAllTextAsync(Path.Combine(celeste, "slot.sav"), "celeste");
        string riot = Path.Combine(alice, "AppData", "LocalLow", "Riot Games", "League");
        Directory.CreateDirectory(riot);
        await File.WriteAllTextAsync(Path.Combine(riot, "settings.yaml"), "riot");

        DetectResult detected = new GameSavesRecipe().Detect(
            new ProfileContext(
                "Alice",
                alice,
                context.Destination,
                context.Temp,
                context.Exports,
                context.SafeFs,
                context.Runner));
        RecipeCard steamCard = Assert.Single(
            detected.Cards,
            card => card.Title.Contains("Steam userdata", StringComparison.Ordinal));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(context.Source, "Program Files (x86)", "Steam", "userdata")),
            steamCard.Facts["source"]);
        Assert.Equal(Path.Combine("Saved Games", "Steam userdata"), steamCard.Facts["relative"]);
        Assert.Contains(
            detected.Cards,
            card => card.Title.Contains("Riot Games", StringComparison.Ordinal));

        PlanResult plan = new GameSavesRecipe().Plan(
            new CardDecisions(steamCard, new Dictionary<string, Decision> { ["saves"] = Decision.Restore }),
            Dest(context));
        Assert.Contains(
            plan.Writes,
            write => write.DestinationPath.EndsWith(
                Path.Combine("Saved Games", "Steam userdata", "12345", "760", "remote.sav"),
                StringComparison.OrdinalIgnoreCase));
        RecipeCard library = Assert.Single(
            detected.Cards,
            card => card.Title.Contains("Celeste", StringComparison.Ordinal));
        Assert.Equal(Path.Combine("Saved Games", "Celeste", "saves"), library.Facts["relative"]);
        PlanResult libraryPlan = new GameSavesRecipe().Plan(
            new CardDecisions(library, new Dictionary<string, Decision> { ["saves"] = Decision.Restore }),
            Dest(context));
        Assert.Contains(
            libraryPlan.Writes,
            write => write.DestinationPath.EndsWith(
                Path.Combine("Saved Games", "Celeste", "saves", "slot.sav"),
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GameSaves_Detect_FindsSteamLibrarySavesFromRecipeIndexWithoutWalkingCommon()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string indexed = Path.Combine(
            context.Source,
            "Program Files (x86)",
            "Steam",
            "steamapps",
            "common",
            "Celeste",
            "saves");
        string walked = Path.Combine(
            context.Source,
            "Program Files (x86)",
            "Steam",
            "steamapps",
            "common",
            "Hollow Knight",
            "saves");
        Directory.CreateDirectory(walked);
        await File.WriteAllTextAsync(Path.Combine(walked, "slot.sav"), "hk");
        await context.Database.InsertNodesAsync(
        [
            new PersistedNode(
                1,
                "session-1",
                null,
                null,
                "Celeste",
                @"Program Files (x86)\Steam\steamapps\common\Celeste",
                NodeKind.Directory,
                0,
                0,
                0,
                DateTime.UtcNow,
                0,
                NodeProblem.None),
            new PersistedNode(
                2,
                "session-1",
                null,
                1,
                "saves",
                @"Program Files (x86)\Steam\steamapps\common\Celeste\saves",
                NodeKind.Directory,
                0,
                0,
                0,
                DateTime.UtcNow,
                0,
                NodeProblem.None),
        ]);

        RecipeIndex index = new(context.Database, "session-1", @"Users\Alice", alice);
        DetectResult fromIndex = new GameSavesRecipe().Detect(
            new ProfileContext(
                "Alice",
                alice,
                context.Destination,
                context.Temp,
                context.Exports,
                context.SafeFs,
                context.Runner,
                index));
        RecipeCard indexedCard = Assert.Single(
            fromIndex.Cards,
            card => card.Title.Contains("Steam library", StringComparison.Ordinal));
        Assert.Equal(indexed, indexedCard.Facts["source"]);
        Assert.DoesNotContain(
            fromIndex.Cards,
            card => card.Title.Contains("Hollow Knight", StringComparison.Ordinal));

        DetectResult fromDisk = new GameSavesRecipe().Detect(
            new ProfileContext(
                "Alice",
                alice,
                context.Destination,
                context.Temp,
                context.Exports,
                context.SafeFs,
                context.Runner));
        Assert.Contains(
            fromDisk.Cards,
            card => card.Title.Contains("Hollow Knight", StringComparison.Ordinal));
        Assert.DoesNotContain(
            fromDisk.Cards,
            card => card.Title.Contains("Celeste", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Libraries_Detect_FindsZoteroCalibreJoplinAndLogseq()
    {
        await using RecipeContext context = await RecipeContext.CreateAsync();
        string alice = Path.Combine(context.Source, "Users", "Alice");
        string zotero = Path.Combine(alice, "Projects", "MyRefs");
        Directory.CreateDirectory(zotero);
        await File.WriteAllTextAsync(Path.Combine(zotero, "zotero.sqlite"), "zotero");
        string calibre = Path.Combine(alice, "Calibre Library");
        Directory.CreateDirectory(calibre);
        await File.WriteAllTextAsync(Path.Combine(calibre, "metadata.db"), "calibre");
        await File.WriteAllTextAsync(Path.Combine(calibre, "cover.jpg"), "cover");
        string joplin = Path.Combine(alice, "AppData", "Roaming", "Joplin");
        Directory.CreateDirectory(joplin);
        await File.WriteAllTextAsync(Path.Combine(joplin, "database.sqlite"), "joplin");
        string graph = Path.Combine(alice, "Documents", "Notes");
        Directory.CreateDirectory(Path.Combine(graph, "logseq"));
        await File.WriteAllTextAsync(Path.Combine(graph, "logseq", "config.edn"), "{:meta {}}");
        await File.WriteAllTextAsync(Path.Combine(graph, "page.md"), "note");
        await context.Database.InsertNodesAsync(
        [
            new PersistedNode(
                1,
                "session-1",
                null,
                null,
                "MyRefs",
                @"Users\Alice\Projects\MyRefs",
                NodeKind.Directory,
                0,
                0,
                0,
                DateTime.UtcNow,
                0,
                NodeProblem.None),
            new PersistedNode(
                2,
                "session-1",
                null,
                1,
                "zotero.sqlite",
                @"Users\Alice\Projects\MyRefs\zotero.sqlite",
                NodeKind.File,
                0,
                0,
                0,
                DateTime.UtcNow,
                0,
                NodeProblem.None),
        ]);

        DetectResult fromIndex = new LibrariesRecipe().Detect(
            new ProfileContext(
                "Alice",
                alice,
                context.Destination,
                context.Temp,
                context.Exports,
                context.SafeFs,
                context.Runner,
                new RecipeIndex(
                    context.Database,
                    "session-1",
                    @"Users\Alice",
                    alice)));
        Assert.Contains(fromIndex.Cards, card => card.Facts["kind"] == "zotero");
        DetectResult fromDisk = new LibrariesRecipe().Detect(
            new ProfileContext(
                "Alice",
                alice,
                context.Destination,
                context.Temp,
                context.Exports,
                context.SafeFs,
                context.Runner));
        Assert.DoesNotContain(fromDisk.Cards, card => card.Facts["kind"] == "zotero");
        Assert.Contains(fromDisk.Cards, card => card.Facts["kind"] == "calibre");
        Assert.Contains(fromDisk.Cards, card => card.Facts["kind"] == "joplin");
        Assert.Contains(fromDisk.Cards, card => card.Facts["kind"] == "logseq");

        RecipeCard calibreCard = Assert.Single(fromDisk.Cards, card => card.Facts["kind"] == "calibre");
        PlanResult plan = new LibrariesRecipe().Plan(
            new CardDecisions(calibreCard, new Dictionary<string, Decision> { ["library"] = Decision.Restore }),
            Dest(context));
        RecipeHost host = new(context.Database, context.SafeFs, context.Runner, [new LibrariesRecipe()]);
        await host.ExecuteAsync("session-1", new LibrariesRecipe(), plan);
        Assert.True(new LibrariesRecipe().Verify(plan).Ok);
        string destCatalog = Path.Combine(context.Destination, "Calibre Library", "metadata.db");
        await File.WriteAllTextAsync(destCatalog, "x");
        RecipeVerifyResult truncated = new LibrariesRecipe().Verify(plan);
        Assert.False(truncated.Ok);
        Assert.Contains("size", truncated.Detail, StringComparison.OrdinalIgnoreCase);
        await File.WriteAllTextAsync(destCatalog, "calibre");
        Assert.True(new LibrariesRecipe().Verify(plan).Ok);
        PlanResult missingCatalog = plan with
        {
            Writes = plan.Writes
                .Where(static write =>
                    !Path.GetFileName(write.DestinationPath).Equals("metadata.db", StringComparison.OrdinalIgnoreCase))
                .ToArray(),
        };
        RecipeVerifyResult missing = new LibrariesRecipe().Verify(missingCatalog);
        Assert.False(missing.Ok);
        Assert.Contains("metadata.db", missing.Detail, StringComparison.OrdinalIgnoreCase);
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
        return new DestinationContext(context.Destination, context.Exports, context.SafeFs, context.Runner, context.Temp);
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

    private static byte[] OpenSshPem(string cipher)
    {
        using MemoryStream payload = new();
        payload.Write("openssh-key-v1\0"u8);
        WriteSshString(payload, cipher);
        WriteSshString(payload, cipher == "none" ? "none" : "bcrypt");
        WriteSshString(payload, string.Empty);
        string b64 = Convert.ToBase64String(payload.ToArray());
        return System.Text.Encoding.ASCII.GetBytes(
            "-----BEGIN OPENSSH PRIVATE KEY-----\n" + b64 + "\n-----END OPENSSH PRIVATE KEY-----\n");
    }

    private static void WriteSshString(Stream stream, string value)
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
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

    private sealed class ScriptedWslRunner : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public string ListStdout { get; init; } = string.Empty;

        public string ConfStdout { get; init; } = string.Empty;

        public string GetentStdout { get; init; } = string.Empty;

        public int ManageExit { get; init; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (request.Arguments.Contains("-l"))
            {
                return Task.FromResult(new ProcessResult(0, ListStdout, string.Empty));
            }

            if (request.Arguments.Contains("cat"))
            {
                return Task.FromResult(new ProcessResult(0, ConfStdout, string.Empty));
            }

            if (request.Arguments.Contains("getent"))
            {
                return Task.FromResult(new ProcessResult(0, GetentStdout, string.Empty));
            }

            if (request.Arguments.Contains("--set-default-user"))
            {
                return Task.FromResult(new ProcessResult(ManageExit, string.Empty, string.Empty));
            }

            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class FixedOutputRunner(string stdout) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ProcessResult(0, stdout, string.Empty));
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
                if (request.Arguments[i] == "-C" || request.Arguments[i] == "--work-tree")
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
