using System.Globalization;
using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Core.Scan;

namespace WinOldRecovery.Core.Tests.Persistence;

public sealed class SessionDbTests
{
    private static readonly string[] ExpectedTables =
    [
        "badges",
        "cards",
        "components",
        "decisions",
        "journal",
        "kv",
        "nodes",
        "plan_items",
        "profiles",
        "schema_migrations",
        "sessions",
        "verify_results",
    ];

    [Fact]
    public async Task SchemaV1_CreatesAllRequiredTablesAndWalMode()
    {
        await using SessionDbTestContext context = await SessionDbTestContext.CreateAsync();
        using SqliteConnection reader = context.Database.OpenReadConnection();

        await using SqliteCommand tablesCommand = reader.CreateCommand();
        tablesCommand.CommandText =
            """
            SELECT name
            FROM sqlite_schema
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;

        List<string> tables = [];
        await using (SqliteDataReader rows = await tablesCommand.ExecuteReaderAsync())
        {
            while (await rows.ReadAsync())
            {
                tables.Add(rows.GetString(0));
            }
        }

        await using SqliteCommand journalCommand = reader.CreateCommand();
        journalCommand.CommandText = "PRAGMA journal_mode;";
        string? journalMode = Convert.ToString(await journalCommand.ExecuteScalarAsync());

        await using SqliteCommand versionCommand = reader.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        long version = (long)(await versionCommand.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("No schema version returned."));

        Assert.Equal(ExpectedTables, tables);
        Assert.Equal("wal", journalMode, ignoreCase: true);
        Assert.Equal(SessionDb.CurrentSchemaVersion, version);
        Assert.Equal(4, SessionDb.CurrentSchemaVersion);

        await using SqliteCommand indexCommand = reader.CreateCommand();
        indexCommand.CommandText =
            "SELECT sql FROM sqlite_schema WHERE type = 'index' AND name = 'ix_nodes_parent';";
        string? parentIndex = Convert.ToString(await indexCommand.ExecuteScalarAsync());
        Assert.Contains("NOCASE", parentIndex, StringComparison.OrdinalIgnoreCase);
        indexCommand.CommandText =
            "SELECT name FROM sqlite_schema WHERE type = 'index' AND name IN ('ix_nodes_session_name', 'ix_nodes_session_rel') ORDER BY name;";
        List<string> recipeIndexes = [];
        await using (SqliteDataReader indexRows = await indexCommand.ExecuteReaderAsync())
        {
            while (await indexRows.ReadAsync())
            {
                recipeIndexes.Add(indexRows.GetString(0));
            }
        }

        Assert.Equal(["ix_nodes_session_name", "ix_nodes_session_rel"], recipeIndexes);
    }

    [Fact]
    public async Task ClearScanData_RemovesNodesWithoutDroppingTheSession()
    {
        await using SessionDbTestContext context = await SessionDbTestContext.CreateAsync();
        await context.Database.CreateSessionAsync(
            new SessionRecord("session-1", DateTimeOffset.UtcNow, "Scanning", "0.1.0"));
        await context.Database.InsertNodesAsync(
        [
            new PersistedNode(
                1,
                "session-1",
                null,
                null,
                "Windows.old",
                "",
                NodeKind.Directory,
                0,
                0,
                0,
                DateTime.UtcNow,
                0,
                NodeProblem.None),
        ]);

        Assert.Equal(1, context.Database.GetMaxNodeId("session-1"));
        await context.Database.ClearScanDataAsync("session-1");
        Assert.Equal(0, context.Database.GetMaxNodeId("session-1"));
    }

    [Fact]
    public async Task InsertNodes_WritesAcrossAMultiRowSqliteChunk()
    {
        await using SessionDbTestContext context = await SessionDbTestContext.CreateAsync();
        await context.Database.CreateSessionAsync(
            new SessionRecord("session-1", DateTimeOffset.UtcNow, "Scanning", "0.1.0"));
        List<PersistedNode> nodes = new(70);
        for (int id = 1; id <= 70; id++)
        {
            nodes.Add(
                new PersistedNode(
                    id,
                    "session-1",
                    null,
                    id == 1 ? null : 1L,
                    "n" + id.ToString(CultureInfo.InvariantCulture),
                    "p" + id.ToString(CultureInfo.InvariantCulture),
                    NodeKind.File,
                    id,
                    0,
                    0,
                    DateTime.UtcNow,
                    0,
                    NodeProblem.None));
        }

        await context.Database.InsertNodesAsync(nodes);
        Assert.Equal(70, context.Database.GetMaxNodeId("session-1"));
        using SqliteConnection reader = context.Database.OpenReadConnection();
        using SqliteCommand count = reader.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM nodes WHERE session_id = 'session-1';";
        Assert.Equal(70L, (long)(count.ExecuteScalar() ?? 0L));
    }

    [Fact]
    public async Task InsertBadges_WritesAcrossAMultiRowSqliteChunk()
    {
        await using SessionDbTestContext context = await SessionDbTestContext.CreateAsync();
        await context.Database.CreateSessionAsync(
            new SessionRecord("session-1", DateTimeOffset.UtcNow, "Scanning", "0.1.0"));
        List<PersistedNode> nodes = new(100);
        List<NodeBadgeRow> badges = new(100);
        for (int id = 1; id <= 100; id++)
        {
            nodes.Add(
                new PersistedNode(
                    id,
                    "session-1",
                    null,
                    null,
                    "n" + id.ToString(CultureInfo.InvariantCulture),
                    "p" + id.ToString(CultureInfo.InvariantCulture),
                    NodeKind.File,
                    0,
                    0,
                    0,
                    DateTime.UtcNow,
                    0,
                    NodeProblem.None));
            badges.Add(new NodeBadgeRow(id, "High-value", "d" + id.ToString(CultureInfo.InvariantCulture)));
        }

        await context.Database.InsertNodesAsync(nodes);
        await context.Database.InsertBadgesAsync(badges);
        using SqliteConnection reader = context.Database.OpenReadConnection();
        using SqliteCommand count = reader.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM badges;";
        Assert.Equal(100L, (long)(count.ExecuteScalar() ?? 0L));
    }

    [Fact]
    public async Task ReplaceKindBadges_ReplacesOnlyThatKindOnTheNode()
    {
        await using SessionDbTestContext context = await SessionDbTestContext.CreateAsync();
        await context.Database.CreateSessionAsync(
            new SessionRecord("session-1", DateTimeOffset.UtcNow, "Created", "0.1.0"));
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
        await context.Database.InsertBadgesAsync(
        [
            new NodeBadgeRow(1, "Git", "Git: local-only work"),
            new NodeBadgeRow(1, "Sensitive", "secret"),
        ]);

        await context.Database.ReplaceKindBadgesAsync(1, "Git", ["Git: clean, pushed"]);

        using SqliteConnection reader = context.Database.OpenReadConnection();
        await using SqliteCommand command = reader.CreateCommand();
        command.CommandText = "SELECT kind || ':' || detail FROM badges ORDER BY kind;";
        List<string> badges = [];
        await using SqliteDataReader rows = await command.ExecuteReaderAsync();
        while (await rows.ReadAsync())
        {
            badges.Add(rows.GetString(0));
        }

        Assert.Equal(["Git:Git: clean, pushed", "Sensitive:secret"], badges);
    }

    [Fact]
    public async Task ListSensitiveRelPaths_ReturnsMarkedNodes()
    {
        await using SessionDbTestContext context = await SessionDbTestContext.CreateAsync();
        await context.Database.CreateSessionAsync(
            new SessionRecord("session-1", DateTimeOffset.UtcNow, "Created", "0.1.0"));
        await context.Database.InsertNodesAsync(
        [
            new PersistedNode(
                1,
                "session-1",
                null,
                null,
                "id_ed25519",
                @".ssh\id_ed25519",
                NodeKind.File,
                32,
                32,
                1,
                DateTime.UtcNow,
                0,
                NodeProblem.None),
            new PersistedNode(
                2,
                "session-1",
                null,
                null,
                "readme.txt",
                "readme.txt",
                NodeKind.File,
                4,
                4,
                1,
                DateTime.UtcNow,
                0,
                NodeProblem.None),
        ]);
        await context.Database.MarkNodesSensitiveAsync([1]);

        IReadOnlySet<string> sensitive = context.Database.ListSensitiveRelPaths("session-1");
        Assert.Contains(@".ssh\id_ed25519", sensitive, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("readme.txt", sensitive, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriterChannel_RoundTripsSessionState()
    {
        await using SessionDbTestContext context = await SessionDbTestContext.CreateAsync();

        await context.Database.WriteAsync(
            async (connection, cancellationToken) =>
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO sessions(
                        id, started_at_utc, source_root, status, app_version)
                    VALUES (
                        $id, $startedAt, $sourceRoot, $status, $appVersion);
                    """;
                command.Parameters.AddWithValue("$id", "session-1");
                command.Parameters.AddWithValue("$startedAt", "2026-09-13T04:00:00Z");
                command.Parameters.AddWithValue("$sourceRoot", @"C:\Windows.old");
                command.Parameters.AddWithValue("$status", "Scanning");
                command.Parameters.AddWithValue("$appVersion", "0.1.0");
                await command.ExecuteNonQueryAsync(cancellationToken);
            });

        using SqliteConnection reader = context.Database.OpenReadConnection();
        await using SqliteCommand readCommand = reader.CreateCommand();
        readCommand.CommandText =
            "SELECT source_root, status FROM sessions WHERE id = 'session-1';";
        await using SqliteDataReader result = await readCommand.ExecuteReaderAsync();

        Assert.True(await result.ReadAsync());
        Assert.Equal(@"C:\Windows.old", result.GetString(0));
        Assert.Equal("Scanning", result.GetString(1));
    }

    [Fact]
    public async Task WriterChannel_SerializesConcurrentWriters()
    {
        await using SessionDbTestContext context = await SessionDbTestContext.CreateAsync();
        await InsertSessionAsync(context.Database);

        int activeWriters = 0;
        int maximumWriters = 0;
        Task[] writes = Enumerable.Range(0, 50)
            .Select(
                index => context.Database.WriteAsync(
                    async (connection, cancellationToken) =>
                    {
                        int active = Interlocked.Increment(ref activeWriters);
                        UpdateMaximum(ref maximumWriters, active);
                        await Task.Delay(2, cancellationToken);

                        await using SqliteCommand command = connection.CreateCommand();
                        command.CommandText =
                            """
                            INSERT INTO kv(session_id, key, value)
                            VALUES ('session-1', $key, $value);
                            """;
                        command.Parameters.AddWithValue("$key", $"key-{index}");
                        command.Parameters.AddWithValue("$value", $"value-{index}");
                        await command.ExecuteNonQueryAsync(cancellationToken);
                        Interlocked.Decrement(ref activeWriters);
                    }))
            .ToArray();

        await Task.WhenAll(writes);

        using SqliteConnection reader = context.Database.OpenReadConnection();
        await using SqliteCommand countCommand = reader.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM kv;";
        long count = (long)(await countCommand.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("No row count returned."));

        Assert.Equal(1, maximumWriters);
        Assert.Equal(50, count);
    }

    [Fact]
    public async Task MigrationScaffold_RecordsAppliedVersion()
    {
        await using SessionDbTestContext context = await SessionDbTestContext.CreateAsync();
        using SqliteConnection reader = context.Database.OpenReadConnection();
        await using SqliteCommand command = reader.CreateCommand();
        command.CommandText =
            "SELECT version FROM schema_migrations ORDER BY version;";

        List<long> versions = [];
        await using SqliteDataReader rows = await command.ExecuteReaderAsync();
        while (await rows.ReadAsync())
        {
            versions.Add(rows.GetInt64(0));
        }

        Assert.Equal(
            Enumerable.Range(1, SessionDb.CurrentSchemaVersion).Select(static version => (long)version),
            versions);
    }

    [Fact]
    public async Task NewerSchema_IsRejectedWithoutModification()
    {
        string root = CreateTestRoot();
        string databasePath = Path.Combine(root, "session.db");
        try
        {
            SQLitePCL.Batteries_V2.Init();
            await using (SqliteConnection connection = new($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version = 99;";
                await command.ExecuteNonQueryAsync();
            }

            SafeFs safeFs = new(new SourceGuard());
            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => SessionDb.OpenAsync(databasePath, safeFs));

            Assert.Contains("newer than supported", exception.Message);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task I1_DatabaseCannotBeCreatedInsideRegisteredSource()
    {
        string root = CreateTestRoot();
        string sourceRoot = Path.Combine(root, "Windows.old");
        Directory.CreateDirectory(sourceRoot);
        SourceGuard guard = new();
        guard.RegisterSourceRoot(sourceRoot);
        SafeFs safeFs = new(guard);
        string databasePath = Path.Combine(sourceRoot, "session.db");

        try
        {
            await Assert.ThrowsAsync<SourceWriteDeniedException>(
                () => SessionDb.OpenAsync(databasePath, safeFs));
            Assert.False(File.Exists(databasePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task InsertSessionAsync(SessionDb database)
    {
        await database.WriteAsync(
            async (connection, cancellationToken) =>
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO sessions(
                        id, started_at_utc, status, app_version)
                    VALUES (
                        'session-1', '2026-09-13T04:00:00Z', 'Created', '0.1.0');
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
            });
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref maximum);
            if (candidate <= observed)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref maximum, candidate, observed) != observed);
    }

    private static string CreateTestRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"WinOldRecovery-SessionDb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class SessionDbTestContext : IAsyncDisposable
    {
        private SessionDbTestContext(string root, SessionDb database)
        {
            Root = root;
            Database = database;
        }

        public string Root { get; }

        public SessionDb Database { get; }

        public static async Task<SessionDbTestContext> CreateAsync()
        {
            string root = CreateTestRoot();
            SafeFs safeFs = new(new SourceGuard());
            SessionDb database = await SessionDb.OpenAsync(
                Path.Combine(root, "session.db"),
                safeFs);
            return new SessionDbTestContext(root, database);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }
}
