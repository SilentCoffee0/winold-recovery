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
