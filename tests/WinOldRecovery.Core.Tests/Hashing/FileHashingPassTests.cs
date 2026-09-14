using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.Hashing;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Core.Scan;

namespace WinOldRecovery.Core.Tests.Hashing;

public sealed class FileHashingPassTests
{
    [Fact]
    public async Task HashSessionFiles_HashesSmallCleanFilesAndSkipsTheRest()
    {
        await using HashContext context = await HashContext.CreateAsync();
        string smallPath = Path.Combine(context.SourceRoot, "small.txt");
        await File.WriteAllTextAsync(smallPath, "hello-hash");
        await File.WriteAllTextAsync(Path.Combine(context.SourceRoot, "cloud.txt"), "placeholder");
        await File.WriteAllTextAsync(Path.Combine(context.SourceRoot, "empty.txt"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(context.SourceRoot, "huge.bin"), "tiny-on-disk");

        await context.Database.InsertNodesAsync(
        [
            Node(1, null, "", "root", NodeKind.Directory, 0, NodeProblem.None),
            Node(2, 1, "small.txt", "small.txt", NodeKind.File, 10, NodeProblem.None),
            Node(3, 1, "cloud.txt", "cloud.txt", NodeKind.File, 11, NodeProblem.CloudOnly),
            Node(4, 1, "empty.txt", "empty.txt", NodeKind.File, 0, NodeProblem.None),
            Node(5, 1, "huge.bin", "huge.bin", NodeKind.File, FileHashingPass.MaxFileBytes + 1, NodeProblem.None),
        ]);

        FileHashingPass hasher = new(context.Database, context.SafeFs);
        int hashed = await hasher.HashSessionFilesAsync("session-1", context.SourceRoot);

        Assert.Equal(1, hashed);
        string expected = Convert.ToHexString(SHA256.HashData("hello-hash"u8.ToArray()));
        Assert.Equal(expected, context.Database.GetKv("session-1", "filehash.2"));
        Assert.Null(context.Database.GetKv("session-1", "filehash.3"));
        Assert.Null(context.Database.GetKv("session-1", "filehash.4"));
        Assert.Null(context.Database.GetKv("session-1", "filehash.5"));
    }

    [Fact]
    public void RequiresContentHash_IncludesVhdxOverTheScanCap()
    {
        Assert.True(FileHashingPass.RequiresContentHash("note.txt", 12));
        Assert.False(FileHashingPass.RequiresContentHash("note.txt", 0));
        Assert.False(FileHashingPass.RequiresContentHash("huge.bin", FileHashingPass.MaxFileBytes + 1));
        Assert.True(FileHashingPass.RequiresContentHash(@"disk.vhdx", FileHashingPass.MaxFileBytes + 1));
        Assert.True(FileHashingPass.RequiresContentHash(@"disk.VHD", FileHashingPass.MaxFileBytes + 1));
    }

    private static PersistedNode Node(
        long id,
        long? parentId,
        string relPath,
        string name,
        NodeKind kind,
        long size,
        NodeProblem problem)
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
            problem);
    }

    private sealed class HashContext : IAsyncDisposable
    {
        private HashContext(string root, SessionDb database, SafeFs safeFs)
        {
            Root = root;
            Database = database;
            SafeFs = safeFs;
            SourceRoot = Path.Combine(root, "source");
        }

        public string Root { get; }
        public SessionDb Database { get; }
        public SafeFs SafeFs { get; }
        public string SourceRoot { get; }

        public static async Task<HashContext> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), $"WinOldRecovery-Hash-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(root, "source"));
            SafeFs safeFs = new(new SourceGuard());
            SessionDb database = await SessionDb.OpenAsync(Path.Combine(root, "session.db"), safeFs);
            await database.CreateSessionAsync(
                new SessionRecord("session-1", DateTimeOffset.UtcNow, "Scanning", "0.1.0"));
            return new HashContext(root, database, safeFs);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }
}
