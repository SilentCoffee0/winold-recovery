using System.Diagnostics;
using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.Browse;
using WinOldRecovery.Core.Classification;
using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Core.Scan;

namespace WinOldRecovery.Core.Tests.Classification;

public sealed class ClassificationEngineTests
{
    [Fact]
    public void I6_EmbeddedRulesNeverSuggestLeaveBehindForRegeneratableOrUnknown()
    {
        IReadOnlyList<ClassificationRule> rules = ClassificationRuleCatalog.LoadEmbedded();
        Assert.DoesNotContain(
            rules,
            rule => rule.SuggestedDefault == Decision.LeaveBehind);
        Assert.Contains(
            SuggestedDefaultTable.Entries,
            entry => entry.Id == "appdata-whole" && entry.Decision == Decision.LeaveBehind);
        Assert.All(
            SuggestedDefaultTable.Entries.Where(static entry => entry.Id != "appdata-whole"),
            entry => Assert.NotEqual(Decision.LeaveBehind, entry.Decision));
    }

    [Fact]
    public async Task Classify_BadgesKeepassAndNodeModulesWithoutLeavingJunkBehind()
    {
        await using ClassifyContext context = await ClassifyContext.CreateAsync();
        string source = Path.Combine(context.Root, "Windows.old");
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice", "Desktop"));
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice", "AppData"));
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice", "proj", "node_modules"));
        await File.WriteAllTextAsync(Path.Combine(source, "Users", "Alice", "NTUSER.DAT"), "hive");
        await File.WriteAllTextAsync(Path.Combine(source, "Users", "Alice", "vault.kdbx"), "keepass");
        await File.WriteAllTextAsync(Path.Combine(source, "Users", "Alice", "loose.txt"), "plain");
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", "proj", "node_modules", "pkg.js"),
            "js");

        ScanOrchestrator orchestrator = new(context.Database, context.SafeFs, context.Guard);
        ScanRunResult result = await orchestrator.RunAsync(
            context.SessionId,
            source,
            Path.Combine(context.Root, "tmp"));

        DecisionEngine engine = new(context.Database, context.SessionId);
        TreeNodeRow vault = Find(context, @"Users\Alice\vault.kdbx");
        TreeNodeRow modules = Find(context, @"Users\Alice\proj\node_modules");
        TreeNodeRow notes = Find(context, @"Users\Alice\loose.txt");
        TreeNodeRow desktop = Find(context, @"Users\Alice\Desktop");
        TreeNodeRow appData = Find(context, @"Users\Alice\AppData");

        Assert.Contains("KeePass", vault.BadgeText);
        Assert.True(context.Database.ListClassificationNodes(context.SessionId)
            .Single(row => row.Id == vault.Id)
            .Sensitive);
        Assert.Equal(Decision.Restore, engine.GetEffectiveDecision(vault.Id));
        Assert.Contains("Regeneratable", modules.BadgeText);
        Assert.Equal(Decision.Undecided, engine.GetEffectiveDecision(modules.Id));
        Assert.Equal(Decision.Undecided, engine.GetEffectiveDecision(notes.Id));
        Assert.Equal(Decision.Restore, engine.GetEffectiveDecision(desktop.Id));
        Assert.Equal(Decision.LeaveBehind, engine.GetEffectiveDecision(appData.Id));
        Assert.True(result.Classification.HighValueCount >= 1);
        Assert.True(result.Classification.RegeneratableCount >= 1);
    }

    [Fact]
    public async Task Classify_RequiresCargoSiblingForTargetAndDoesNotOpenSensitiveHeaders()
    {
        await using ClassifyContext context = await ClassifyContext.CreateAsync();
        await context.Database.InsertNodesAsync(
        [
            Node(1, null, "", "root", NodeKind.Directory, 0),
            Node(2, 1, "proj", "proj", NodeKind.Directory, 0),
            Node(3, 2, @"proj\target", "target", NodeKind.Directory, 0),
            Node(4, 2, @"proj\Cargo.toml", "Cargo.toml", NodeKind.File, 10),
            Node(5, 1, "other", "other", NodeKind.Directory, 0),
            Node(6, 5, @"other\target", "target", NodeKind.Directory, 0),
            Node(7, 1, "secret.pem", "secret.pem", NodeKind.File, 20),
        ]);

        ClassificationEngine classifier = new(context.Database, context.SafeFs);
        await classifier.ClassifyAsync(context.SessionId, context.Root, []);

        TreeNodeRow cargoTarget = Find(context, @"proj\target");
        TreeNodeRow lonelyTarget = Find(context, @"other\target");
        TreeNodeRow pem = Find(context, "secret.pem");
        Assert.Contains("Regeneratable", cargoTarget.BadgeText);
        Assert.DoesNotContain("Regeneratable", lonelyTarget.BadgeText);
        Assert.Contains("Sensitive", pem.BadgeText);
        Assert.Equal(Decision.Undecided, new DecisionEngine(context.Database, context.SessionId)
            .GetEffectiveDecision(pem.Id));
    }

    [Fact]
    public async Task Classify_BadgesPowerShellProfileScriptsAsHighValue()
    {
        await using ClassifyContext context = await ClassifyContext.CreateAsync();
        string source = Path.Combine(context.Root, "Windows.old");
        string powershell = Path.Combine(source, "Users", "Alice", "Documents", "PowerShell");
        string windowsPowerShell = Path.Combine(source, "Users", "Alice", "Documents", "WindowsPowerShell");
        Directory.CreateDirectory(powershell);
        Directory.CreateDirectory(windowsPowerShell);
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice", "Desktop"));
        await File.WriteAllTextAsync(
            Path.Combine(powershell, "Microsoft.PowerShell_profile.ps1"),
            "Set-Alias ll Get-ChildItem");
        await File.WriteAllTextAsync(
            Path.Combine(windowsPowerShell, "Microsoft.PowerShell_profile.ps1"),
            "Write-Host hi");
        await File.WriteAllTextAsync(Path.Combine(source, "Users", "Alice", "scratch.ps1"), "not a profile");
        await File.WriteAllTextAsync(
            Path.Combine(source, "Users", "Alice", ".gitignore_global"),
            "*~");

        ScanOrchestrator orchestrator = new(context.Database, context.SafeFs, context.Guard);
        await orchestrator.RunAsync(context.SessionId, source, Path.Combine(context.Root, "tmp"));

        TreeNodeRow pwsh = Find(context, @"Users\Alice\Documents\PowerShell\Microsoft.PowerShell_profile.ps1");
        TreeNodeRow windows = Find(
            context,
            @"Users\Alice\Documents\WindowsPowerShell\Microsoft.PowerShell_profile.ps1");
        TreeNodeRow scratch = Find(context, @"Users\Alice\scratch.ps1");
        TreeNodeRow ignore = Find(context, @"Users\Alice\.gitignore_global");

        Assert.Contains("PowerShell", pwsh.BadgeText);
        Assert.Contains("PowerShell", windows.BadgeText);
        Assert.DoesNotContain("PowerShell", scratch.BadgeText);
        Assert.Contains("Dotfile", ignore.BadgeText);
        DecisionEngine engine = new(context.Database, context.SessionId);
        Assert.Equal(Decision.Restore, engine.GetEffectiveDecision(pwsh.Id));
        Assert.Equal(Decision.Restore, engine.GetEffectiveDecision(ignore.Id));
        Assert.Equal(Decision.Undecided, engine.GetEffectiveDecision(scratch.Id));
    }

    [Fact]
    public async Task Classify_BadgesKnownPublisherGameFolders()
    {
        await using ClassifyContext context = await ClassifyContext.CreateAsync();
        string alice = Path.Combine(context.Root, "Windows.old", "Users", "Alice");
        Directory.CreateDirectory(Path.Combine(alice, "Desktop"));
        Directory.CreateDirectory(Path.Combine(alice, "AppData", "Roaming", ".minecraft", "saves", "world"));
        Directory.CreateDirectory(Path.Combine(alice, "AppData", "Local", "EpicGamesLauncher", "Saved"));
        Directory.CreateDirectory(Path.Combine(alice, "AppData", "Local", "Ubisoft Game Launcher"));
        Directory.CreateDirectory(Path.Combine(alice, "AppData", "Roaming", "Electronic Arts", "EA Desktop"));
        Directory.CreateDirectory(Path.Combine(alice, "AppData", "Roaming", "Battle.net"));
        Directory.CreateDirectory(Path.Combine(alice, "AppData", "Local", "Riot Games", "Riot Client"));
        Directory.CreateDirectory(Path.Combine(alice, "AppData", "Roaming", "NotAGame"));
        await File.WriteAllTextAsync(Path.Combine(alice, "NTUSER.DAT"), "hive");
        await File.WriteAllTextAsync(Path.Combine(alice, "AppData", "Roaming", ".minecraft", "saves", "world", "level.dat"), "save");
        await File.WriteAllTextAsync(Path.Combine(alice, "AppData", "Local", "EpicGamesLauncher", "Saved", "Config.ini"), "epic");
        await File.WriteAllTextAsync(Path.Combine(alice, "AppData", "Local", "Ubisoft Game Launcher", "settings.yml"), "ubi");
        await File.WriteAllTextAsync(Path.Combine(alice, "AppData", "Roaming", "Electronic Arts", "EA Desktop", "user.ini"), "ea");
        await File.WriteAllTextAsync(Path.Combine(alice, "AppData", "Roaming", "Battle.net", "Battle.net.config"), "bnet");
        await File.WriteAllTextAsync(Path.Combine(alice, "AppData", "Local", "Riot Games", "Riot Client", "config.yaml"), "riot");
        await File.WriteAllTextAsync(Path.Combine(alice, "AppData", "Roaming", "NotAGame", "notes.txt"), "plain");

        ScanOrchestrator orchestrator = new(context.Database, context.SafeFs, context.Guard);
        await orchestrator.RunAsync(
            context.SessionId,
            Path.Combine(context.Root, "Windows.old"),
            Path.Combine(context.Root, "tmp"));

        Assert.Contains("Game save", Find(context, @"Users\Alice\AppData\Roaming\.minecraft\saves\world\level.dat").BadgeText);
        Assert.Contains("Game save", Find(context, @"Users\Alice\AppData\Local\EpicGamesLauncher\Saved\Config.ini").BadgeText);
        Assert.Contains("Game save", Find(context, @"Users\Alice\AppData\Local\Ubisoft Game Launcher\settings.yml").BadgeText);
        Assert.Contains("Game save", Find(context, @"Users\Alice\AppData\Roaming\Electronic Arts\EA Desktop\user.ini").BadgeText);
        Assert.Contains("Game save", Find(context, @"Users\Alice\AppData\Roaming\Battle.net\Battle.net.config").BadgeText);
        Assert.Contains("Game save", Find(context, @"Users\Alice\AppData\Local\Riot Games\Riot Client\config.yaml").BadgeText);
        Assert.DoesNotContain("Game save", Find(context, @"Users\Alice\AppData\Roaming\NotAGame\notes.txt").BadgeText);
    }

    [Fact]
    public async Task Classify_BadgesRemainingR9Detectors()
    {
        await using ClassifyContext context = await ClassifyContext.CreateAsync();
        string alice = Path.Combine(context.Root, "Windows.old", "Users", "Alice");
        Directory.CreateDirectory(Path.Combine(alice, "AppData", "Roaming", "Joplin"));
        Directory.CreateDirectory(Path.Combine(alice, "Notes", "graph", "logseq"));
        Directory.CreateDirectory(Path.Combine(alice, ".nuget"));
        Directory.CreateDirectory(Path.Combine(alice, ".m2"));
        Directory.CreateDirectory(Path.Combine(alice, "certs"));
        Directory.CreateDirectory(Path.Combine(alice, "AppData", "Local", "MyGame", "Saved", "SaveGames"));
        Directory.CreateDirectory(Path.Combine(alice, "AppData", "Local", "GOG.com", "Galaxy", "Applications", "title"));
        Directory.CreateDirectory(Path.Combine(alice, "AppData", "Roaming", "Notion"));
        await File.WriteAllTextAsync(Path.Combine(alice, "AppData", "Roaming", "Joplin", "database.sqlite"), "joplin");
        await File.WriteAllTextAsync(Path.Combine(alice, "Notes", "graph", "logseq", "config.edn"), "{:meta/version 1}");
        await File.WriteAllTextAsync(Path.Combine(alice, ".nuget", "NuGet.Config"), "<configuration />");
        await File.WriteAllTextAsync(Path.Combine(alice, ".m2", "settings.xml"), "<settings />");
        await File.WriteAllTextAsync(Path.Combine(alice, "machine.vmx"), "guestOS = \"windows9-64\"");
        await File.WriteAllTextAsync(Path.Combine(alice, ".yarnrc.yml"), "npmAuthToken: secret");
        await File.WriteAllTextAsync(Path.Combine(alice, "certs", "site.crt"), "cert");
        await File.WriteAllTextAsync(Path.Combine(alice, "certs", "site.key"), "key");
        await File.WriteAllTextAsync(Path.Combine(alice, "lonely.crt"), "no-key");
        await File.WriteAllTextAsync(
            Path.Combine(alice, "AppData", "Local", "MyGame", "Saved", "SaveGames", "slot.sav"),
            "save");
        await File.WriteAllTextAsync(
            Path.Combine(alice, "AppData", "Roaming", "Notion", "offline"),
            "cache");

        ScanOrchestrator orchestrator = new(context.Database, context.SafeFs, context.Guard);
        await orchestrator.RunAsync(
            context.SessionId,
            Path.Combine(context.Root, "Windows.old"),
            Path.Combine(context.Root, "tmp"));

        DecisionEngine engine = new(context.Database, context.SessionId);
        TreeNodeRow joplin = Find(context, @"Users\Alice\AppData\Roaming\Joplin\database.sqlite");
        TreeNodeRow logseq = Find(context, @"Users\Alice\Notes\graph\logseq\config.edn");
        TreeNodeRow nuget = Find(context, @"Users\Alice\.nuget\NuGet.Config");
        TreeNodeRow maven = Find(context, @"Users\Alice\.m2\settings.xml");
        TreeNodeRow vmx = Find(context, @"Users\Alice\machine.vmx");
        TreeNodeRow yarn = Find(context, @"Users\Alice\.yarnrc.yml");
        TreeNodeRow cert = Find(context, @"Users\Alice\certs\site.crt");
        TreeNodeRow lonely = Find(context, @"Users\Alice\lonely.crt");
        TreeNodeRow saves = Find(context, @"Users\Alice\AppData\Local\MyGame\Saved\SaveGames");
        TreeNodeRow gog = Find(context, @"Users\Alice\AppData\Local\GOG.com\Galaxy\Applications");
        TreeNodeRow notion = Find(context, @"Users\Alice\AppData\Roaming\Notion");

        Assert.Contains("Joplin", joplin.BadgeText);
        Assert.Contains("Logseq", logseq.BadgeText);
        Assert.Contains("Dev config", nuget.BadgeText);
        Assert.Contains("Dev config", maven.BadgeText);
        Assert.Contains("VM disk", vmx.BadgeText);
        Assert.Contains("Sensitive", yarn.BadgeText);
        Assert.Contains("Sensitive", cert.BadgeText);
        Assert.DoesNotContain("Sensitive", lonely.BadgeText);
        Assert.Contains("Game save", saves.BadgeText);
        Assert.Contains("Game save", gog.BadgeText);
        Assert.Contains("Regeneratable", notion.BadgeText);
        Assert.Equal(Decision.Restore, engine.GetEffectiveDecision(joplin.Id));
        Assert.Equal(Decision.Restore, engine.GetEffectiveDecision(nuget.Id));
        Assert.Equal(Decision.Restore, engine.GetEffectiveDecision(saves.Id));
        Assert.Equal(Decision.Undecided, engine.GetEffectiveDecision(vmx.Id));
        Assert.Equal(Decision.Undecided, engine.GetEffectiveDecision(yarn.Id));
        Assert.Equal(Decision.Undecided, engine.GetEffectiveDecision(gog.Id));
        Assert.Equal(Decision.Undecided, engine.GetEffectiveDecision(notion.Id));
    }

    [Fact]
    public async Task Classify_BadgesBrowserCachesAndSafetyModelSecrets()
    {
        await using ClassifyContext context = await ClassifyContext.CreateAsync();
        string alice = Path.Combine(context.Root, "Windows.old", "Users", "Alice");
        string chromeCache = Path.Combine(alice, "AppData", "Local", "Google", "Chrome", "User Data", "Default", "Cache");
        string decoyCache = Path.Combine(alice, "Documents", "Cache");
        Directory.CreateDirectory(chromeCache);
        Directory.CreateDirectory(decoyCache);
        Directory.CreateDirectory(Path.Combine(alice, "AppData", "Local", "D3DSCache"));
        Directory.CreateDirectory(Path.Combine(alice, ".ollama", "models"));
        Directory.CreateDirectory(Path.Combine(alice, "AppData", "Roaming", "Microsoft", "Protect"));
        Directory.CreateDirectory(Path.Combine(alice, "AppData", "Local", "Microsoft", "Credentials"));
        await File.WriteAllTextAsync(Path.Combine(alice, "NTUSER.DAT"), "hive");
        await File.WriteAllTextAsync(Path.Combine(chromeCache, "data_0"), "cache");
        await File.WriteAllTextAsync(Path.Combine(decoyCache, "notes.txt"), "not a browser cache");
        await File.WriteAllTextAsync(
            Path.Combine(alice, "AppData", "Local", "Google", "Chrome", "User Data", "Default", "Login Data"),
            "dpapi");
        await File.WriteAllTextAsync(Path.Combine(alice, ".ollama", "models", "blob"), "weights");

        ScanOrchestrator orchestrator = new(context.Database, context.SafeFs, context.Guard);
        await orchestrator.RunAsync(
            context.SessionId,
            Path.Combine(context.Root, "Windows.old"),
            Path.Combine(context.Root, "tmp"));

        TreeNodeRow cache = Find(
            context,
            @"Users\Alice\AppData\Local\Google\Chrome\User Data\Default\Cache");
        TreeNodeRow decoy = Find(context, @"Users\Alice\Documents\Cache");
        TreeNodeRow login = Find(
            context,
            @"Users\Alice\AppData\Local\Google\Chrome\User Data\Default\Login Data");
        TreeNodeRow hive = Find(context, @"Users\Alice\NTUSER.DAT");
        TreeNodeRow d3d = Find(context, @"Users\Alice\AppData\Local\D3DSCache");
        TreeNodeRow ollama = Find(context, @"Users\Alice\.ollama\models");
        TreeNodeRow protect = Find(context, @"Users\Alice\AppData\Roaming\Microsoft\Protect");
        TreeNodeRow creds = Find(context, @"Users\Alice\AppData\Local\Microsoft\Credentials");

        Assert.Contains("Regeneratable", cache.BadgeText);
        Assert.DoesNotContain("Regeneratable", decoy.BadgeText);
        Assert.Contains("Regeneratable", d3d.BadgeText);
        Assert.Contains("Regeneratable", ollama.BadgeText);
        Assert.Contains("Sensitive", login.BadgeText);
        Assert.Contains("Sensitive", hive.BadgeText);
        Assert.Contains("Sensitive", protect.BadgeText);
        Assert.Contains("Sensitive", creds.BadgeText);
        Assert.True(context.Database.ListClassificationNodes(context.SessionId)
            .Single(row => row.Id == login.Id)
            .Sensitive);
        DecisionEngine engine = new(context.Database, context.SessionId);
        Assert.Equal(Decision.Undecided, engine.GetEffectiveDecision(cache.Id));
        Assert.Equal(Decision.Undecided, engine.GetEffectiveDecision(login.Id));
        Assert.Equal(Decision.Undecided, engine.GetEffectiveDecision(hive.Id));
    }

    [Fact]
    public async Task Classify_BadgesPasswordManagerExportsAsSensitive()
    {
        await using ClassifyContext context = await ClassifyContext.CreateAsync();
        string alice = Path.Combine(context.Root, "Windows.old", "Users", "Alice");
        Directory.CreateDirectory(Path.Combine(alice, "Documents"));
        await File.WriteAllTextAsync(Path.Combine(alice, "NTUSER.DAT"), "hive");
        await File.WriteAllTextAsync(Path.Combine(alice, "Documents", "bitwarden_export_2026.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(alice, "Documents", "1Password Emergency Kit.pdf"), "kit");
        await File.WriteAllTextAsync(Path.Combine(alice, "Documents", "notes.pdf"), "not-a-kit");

        ScanOrchestrator orchestrator = new(context.Database, context.SafeFs, context.Guard);
        await orchestrator.RunAsync(
            context.SessionId,
            Path.Combine(context.Root, "Windows.old"),
            Path.Combine(context.Root, "tmp"));

        TreeNodeRow bitwarden = Find(context, @"Users\Alice\Documents\bitwarden_export_2026.json");
        TreeNodeRow kit = Find(context, @"Users\Alice\Documents\1Password Emergency Kit.pdf");
        TreeNodeRow notes = Find(context, @"Users\Alice\Documents\notes.pdf");
        Assert.Contains("Password export", bitwarden.BadgeText);
        Assert.Contains("Password export", kit.BadgeText);
        Assert.DoesNotContain("Password export", notes.BadgeText);
        DecisionEngine engine = new(context.Database, context.SessionId);
        Assert.Equal(Decision.Undecided, engine.GetEffectiveDecision(bitwarden.Id));
    }

    [Fact]
    public async Task Classify_OneHundredThousandScaleNodesStayUnderFifteenSeconds()
    {
        await using ClassifyContext context = await ClassifyContext.CreateAsync();
        List<PersistedNode> batch = [Node(1, null, string.Empty, "root", NodeKind.Directory, 0)];
        long id = 1;
        for (int directory = 0; directory < 100; directory++)
        {
            long directoryId = ++id;
            string directoryName = "d" + directory.ToString("D3");
            batch.Add(Node(directoryId, 1, directoryName, directoryName, NodeKind.Directory, 0));
            for (int file = 0; file < 1000; file++)
            {
                long fileId = ++id;
                string fileName = "f" + file.ToString("D4") + ".txt";
                batch.Add(
                    Node(
                        fileId,
                        directoryId,
                        directoryName + "\\" + fileName,
                        fileName,
                        NodeKind.File,
                        0));
            }

            if (batch.Count >= 5000)
            {
                await context.Database.InsertNodesAsync(batch);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await context.Database.InsertNodesAsync(batch);
        }

        ClassificationEngine classifier = new(context.Database, context.SafeFs);
        Stopwatch clock = Stopwatch.StartNew();
        ClassificationSummary summary = await classifier.ClassifyAsync(
            context.SessionId,
            context.Root,
            []);
        clock.Stop();

        Assert.Equal(0, summary.HighValueCount);
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(15),
            "ClassifyAsync for 100,101 nodes took " + clock.Elapsed.TotalSeconds.ToString("0.000") + " s.");
    }

    [Fact]
    public async Task Classify_OneMillionScaleLikeNodesMatchUnderFiveSeconds()
    {
        await using ClassifyContext context = await ClassifyContext.CreateAsync();
        const int directoryCount = 1000;
        const int filesPerDirectory = 1000;
        List<ClassificationNodeRow> nodes = new((directoryCount * filesPerDirectory) + directoryCount + 4);
        nodes.Add(new ClassificationNodeRow(1, null, string.Empty, string.Empty, NodeKind.Directory, 0, 0, NodeProblem.None, false));
        long id = 1;
        for (int directory = 0; directory < directoryCount; directory++)
        {
            long directoryId = ++id;
            string directoryName = "d" + directory.ToString("D4");
            string directoryPath = @"Users\Alice\Scale\" + directoryName;
            nodes.Add(
                new ClassificationNodeRow(
                    directoryId,
                    1,
                    directoryName,
                    directoryPath,
                    NodeKind.Directory,
                    0,
                    0,
                    NodeProblem.None,
                    false));
            for (int file = 0; file < filesPerDirectory; file++)
            {
                string fileName = "f" + file.ToString("D4") + ".txt";
                nodes.Add(
                    new ClassificationNodeRow(
                        ++id,
                        directoryId,
                        fileName,
                        directoryPath + "\\" + fileName,
                        NodeKind.File,
                        0,
                        0,
                        NodeProblem.None,
                        false));
            }
        }

        ClassificationEngine classifier = new(context.Database, context.SafeFs);
        Stopwatch clock = Stopwatch.StartNew();
        int hits = classifier.CountMatchesForTests(nodes, context.Root);
        clock.Stop();

        Assert.Equal(0, hits);
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(5),
            "In-memory classify for 1,001,001 scale-like nodes took " +
                clock.Elapsed.TotalSeconds.ToString("0.000") +
                " s.");
    }

    private static TreeNodeRow Find(ClassifyContext context, string relPath)
    {
        return new NodeBrowser(context.Database, context.SessionId).FindByRelPath(relPath)
            ?? throw new InvalidOperationException(relPath);
    }

    private static PersistedNode Node(
        long id,
        long? parentId,
        string relPath,
        string name,
        NodeKind kind,
        long size)
    {
        return new PersistedNode(
            id,
            "session-1",
            null,
            parentId,
            name,
            relPath,
            kind,
            size,
            size,
            kind == NodeKind.File ? 1 : 0,
            DateTime.UtcNow,
            0,
            NodeProblem.None);
    }

    private sealed class ClassifyContext : IAsyncDisposable
    {
        private ClassifyContext(string root, SessionDb database, SafeFs safeFs, SourceGuard guard)
        {
            Root = root;
            Database = database;
            SafeFs = safeFs;
            Guard = guard;
        }

        public string Root { get; }
        public SessionDb Database { get; }
        public SafeFs SafeFs { get; }
        public SourceGuard Guard { get; }
        public string SessionId => "session-1";

        public static async Task<ClassifyContext> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), $"WinOldRecovery-Class-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "tmp"));
            SourceGuard guard = new();
            SafeFs safeFs = new(guard);
            SessionDb database = await SessionDb.OpenAsync(Path.Combine(root, "session.db"), safeFs);
            await database.CreateSessionAsync(
                new SessionRecord("session-1", DateTimeOffset.UtcNow, "Scanning", "0.1.0"));
            return new ClassifyContext(root, database, safeFs, guard);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }
}
