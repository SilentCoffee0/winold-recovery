using System.Globalization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.IO;

namespace WinOldRecovery.Core.Persistence;

public sealed class SessionDb : IAsyncDisposable
{
    private const int WriterQueueCapacity = 1024;

    private readonly SqliteConnection writerConnection;
    private readonly Channel<IWriteRequest> writerQueue;
    private readonly Task writerTask;
    private int disposeState;

    private SessionDb(string databasePath, SqliteConnection writerConnection)
    {
        DatabasePath = databasePath;
        this.writerConnection = writerConnection;
        writerQueue = Channel.CreateBounded<IWriteRequest>(
            new BoundedChannelOptions(WriterQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
        writerTask = RunWriterAsync();
    }

    public string DatabasePath { get; }

    public static int CurrentSchemaVersion => SessionDbSchema.CurrentVersion;

    public Task CreateSessionAsync(
        SessionRecord session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.Status);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.AppVersion);

        return WriteAsync(
            async (connection, token) =>
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO sessions(
                        id,
                        started_at_utc,
                        source_root,
                        status,
                        app_version,
                        options_json)
                    VALUES (
                        $id,
                        $startedAt,
                        $sourceRoot,
                        $status,
                        $appVersion,
                        $optionsJson);
                    """;
                command.Parameters.AddWithValue("$id", session.Id);
                command.Parameters.AddWithValue(
                    "$startedAt",
                    session.StartedAt.ToUniversalTime().ToString(
                        "O",
                        CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue(
                    "$sourceRoot",
                    (object?)session.SourceRoot ?? DBNull.Value);
                command.Parameters.AddWithValue("$status", session.Status);
                command.Parameters.AddWithValue("$appVersion", session.AppVersion);
                command.Parameters.AddWithValue("$optionsJson", session.OptionsJson);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public static async Task<SessionDb> OpenAsync(
        string databasePath,
        SafeFs safeFs,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(safeFs);

        string? parent = Path.GetDirectoryName(databasePath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new ArgumentException(
                "The database path must include a parent directory.",
                nameof(databasePath));
        }

        safeFs.CreateDirectory(parent);
        string validatedPath = safeFs.GetValidatedWritePath(databasePath);

        SQLitePCL.Batteries_V2.Init();
        SqliteConnection connection = new(CreateWriterConnectionString(validatedPath));

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureWriterAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationsAsync(connection, cancellationToken).ConfigureAwait(false);
            return new SessionDb(validatedPath, connection);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task WriteAsync(
        Func<SqliteConnection, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await WriteAsync(
                async (connection, token) =>
                {
                    await operation(connection, token).ConfigureAwait(false);
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<T> WriteAsync<T>(
        Func<SqliteConnection, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposeState) != 0,
            this);

        WriteRequest<T> request = new(operation, cancellationToken);
        await writerQueue.Writer
            .WriteAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return await request.Completion.ConfigureAwait(false);
    }

    public SqliteConnection OpenReadConnection()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposeState) != 0,
            this);

        SqliteConnection connection = new(CreateReaderConnectionString(DatabasePath));
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA query_only = ON;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            """;
        command.ExecuteNonQuery();
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
        {
            return;
        }

        writerQueue.Writer.TryComplete();
        await writerTask.ConfigureAwait(false);
        await writerConnection.DisposeAsync().ConfigureAwait(false);
    }

    private static string CreateWriterConnectionString(string databasePath)
    {
        return new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString();
    }

    private static string CreateReaderConnectionString(string databasePath)
    {
        return new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = true,
            DefaultTimeout = 5,
        }.ToString();
    }

    private static async Task ConfigureWriterAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand journalCommand = connection.CreateCommand();
        journalCommand.CommandText = "PRAGMA journal_mode = WAL;";
        object? journalMode = await journalCommand
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                Convert.ToString(journalMode, CultureInfo.InvariantCulture),
                "wal",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("SQLite could not enable WAL journal mode.");
        }

        await using SqliteCommand settingsCommand = connection.CreateCommand();
        settingsCommand.CommandText =
            """
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            """;
        await settingsCommand
            .ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ApplyMigrationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        int currentVersion = Convert.ToInt32(
            await versionCommand
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false),
            CultureInfo.InvariantCulture);

        if (currentVersion > SessionDbSchema.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Session database schema {currentVersion} is newer than supported schema {SessionDbSchema.CurrentVersion}.");
        }

        foreach (SchemaMigration migration in SessionDbSchema.Migrations
                     .Where(migration => migration.Version > currentVersion)
                     .OrderBy(static migration => migration.Version))
        {
            using SqliteTransaction transaction = connection.BeginTransaction();

            await using (SqliteCommand migrationCommand = connection.CreateCommand())
            {
                migrationCommand.Transaction = transaction;
                migrationCommand.CommandText = migration.Sql;
                await migrationCommand
                    .ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            await using (SqliteCommand recordCommand = connection.CreateCommand())
            {
                recordCommand.Transaction = transaction;
                recordCommand.CommandText =
                    """
                    INSERT INTO schema_migrations(version, applied_at_utc)
                    VALUES ($version, $appliedAt);
                    """;
                recordCommand.Parameters.AddWithValue("$version", migration.Version);
                recordCommand.Parameters.AddWithValue(
                    "$appliedAt",
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                await recordCommand
                    .ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            await using (SqliteCommand updateVersionCommand = connection.CreateCommand())
            {
                updateVersionCommand.Transaction = transaction;
                updateVersionCommand.CommandText =
                    $"PRAGMA user_version = {migration.Version.ToString(CultureInfo.InvariantCulture)};";
                await updateVersionCommand
                    .ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            transaction.Commit();
            currentVersion = migration.Version;
        }
    }

    private async Task RunWriterAsync()
    {
        await foreach (IWriteRequest request in writerQueue.Reader.ReadAllAsync())
        {
            await request.ExecuteAsync(writerConnection).ConfigureAwait(false);
        }
    }

    private interface IWriteRequest
    {
        ValueTask ExecuteAsync(SqliteConnection connection);
    }

    private sealed class WriteRequest<T> : IWriteRequest
    {
        private readonly Func<SqliteConnection, CancellationToken, Task<T>> operation;
        private readonly CancellationToken cancellationToken;
        private readonly TaskCompletionSource<T> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WriteRequest(
            Func<SqliteConnection, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            this.operation = operation;
            this.cancellationToken = cancellationToken;
        }

        public Task<T> Completion => completion.Task;

        public async ValueTask ExecuteAsync(SqliteConnection connection)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                T result = await operation(connection, cancellationToken)
                    .ConfigureAwait(false);
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }
    }
}
