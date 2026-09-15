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
