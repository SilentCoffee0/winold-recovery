using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Recipes;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Core.Scan;

namespace WinOldRecovery.Core.Tests.Recipes;

public sealed class RecipeIndexTests
{
    [Fact]
    public async Task Index_FindsKeePassGitAndObsidianWithoutWalkingDisk()
    {
        await using RecipeIndexContext context = await RecipeIndexContext.CreateAsync();
        await context.Database.CreateSessionAsync(
            new SessionRecord("session-1", DateTimeOffset.UtcNow, "Created", "0.1.0"));
        await context.Database.InsertNodesAsync(
        [
            Node(1, null, "Windows.old", ""),
            Node(2, 1, "Alice", @"Users\Alice"),
            Node(3, 2, "Documents", @"Users\Alice\Documents"),
            Node(4, 3, "vault.kdbx", @"Users\Alice\Documents\vault.kdbx", NodeKind.File),
            Node(5, 3, "notes", @"Users\Alice\Documents\notes"),
            Node(6, 5, ".git", @"Users\Alice\Documents\notes\.git"),
            Node(7, 3, "Notes", @"Users\Alice\Documents\Notes"),
            Node(8, 7, ".obsidian", @"Users\Alice\Documents\Notes\.obsidian"),
            Node(9, 2, "AppData", @"Users\Alice\AppData"),
            Node(10, 9, "skip.kdbx", @"Users\Alice\AppData\skip.kdbx", NodeKind.File),
        ]);
        await context.Database.SetKvAsync(
            "session-1",
            "walker.checkpoint:",
            SessionDb.SerializeWalkerCheckpoint(new WalkerCheckpoint(1, 11, [])));

        Assert.True(context.Database.HasWalkerCheckpoint("session-1"));
        RecipeIndex index = new(
            context.Database,
            "session-1",
            @"Users\Alice",
            Path.Combine(context.Root, "Users", "Alice"));

        Assert.Equal(
            Path.Combine(context.Root, "Users", "Alice", "Documents", "vault.kdbx"),
            Assert.Single(index.FilesWithExtensions(".kdbx", ".kdb")));
        Assert.Equal(
            Path.Combine(context.Root, "Users", "Alice", "Documents", "notes"),
            Assert.Single(index.GitWorkingTrees()));
        Assert.Equal(
            Path.Combine(context.Root, "Users", "Alice", "Documents", "Notes"),
            Assert.Single(index.ParentsOfChildNamed(".obsidian")));

        IReadOnlyDictionary<string, long> ids = context.Database.FindNodeIds(
            "session-1",
            [@"Users\Alice\Documents\vault.kdbx", @"Users\Alice\Documents\notes"]);
        Assert.Equal(4, ids[@"Users\Alice\Documents\vault.kdbx"]);
        Assert.Equal(5, ids[@"Users\Alice\Documents\notes"]);
    }

    private static PersistedNode Node(
        long id,
        long? parentId,
        string name,
        string relPath,
        NodeKind kind = NodeKind.Directory)
    {
        return new PersistedNode(
            id,
            "session-1",
            null,
            parentId,
            name,
            relPath,
            kind,
            0,
            0,
            0,
            DateTime.UtcNow,
            0,
            NodeProblem.None);
    }

    private sealed class RecipeIndexContext : IAsyncDisposable
    {
        private RecipeIndexContext(string root, SessionDb database)
        {
            Root = root;
            Database = database;
        }

        public string Root { get; }

        public SessionDb Database { get; }

        public static async Task<RecipeIndexContext> CreateAsync()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                $"WinOldRecovery-RecipeIndex-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            SessionDb database = await SessionDb.OpenAsync(
                Path.Combine(root, "session.db"),
                new SafeFs(new SourceGuard()));
            return new RecipeIndexContext(root, database);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }
}
