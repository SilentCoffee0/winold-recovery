using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Safety;

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

        Assert.Equal([1L, 2L], versions);
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
