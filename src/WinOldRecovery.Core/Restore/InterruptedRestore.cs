using System.Globalization;
using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Planning;
using WinOldRecovery.Core.Sessions;

namespace WinOldRecovery.Core.Restore;

public sealed record InterruptedRestoreReport(
    string SessionId,
    string WorkspaceRoot,
    DateTimeOffset InterruptedAt,
    int IncompleteItems,
    int CompletedItems,
    string? SourceRoot,
    string? DestinationRoot,
    RestorePlan? Plan);

public static class InterruptedRestore
{
    public const string SourceRootKey = "restore.sourceRoot";
    public const string DestinationRootKey = "restore.destinationRoot";

    public static InterruptedRestoreReport? Describe(SessionDb sessionDb, string sessionId, string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(sessionDb);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        IReadOnlyList<PlanItem> items = sessionDb.ListPlanItems(sessionId);
        if (items.Count == 0)
        {
            return null;
        }

        int incomplete = 0;
        int completed = 0;
        int pending = 0;
        bool anyInProgress = false;
        DateTimeOffset interruptedAt = DateTimeOffset.MinValue;
        foreach (PlanItem item in items)
        {
            if (item.Id is not long id)
            {
                continue;
            }

            string? state = sessionDb.GetLatestJournalState(id);
            if (state is "Completed" or "Skipped")
            {
                completed++;
                continue;
            }

            if (state is "Started" or "Paused" or "Failed")
            {
                anyInProgress = true;
                incomplete++;
                DateTimeOffset at = ReadLatestJournalTime(sessionDb, id) ?? DateTimeOffset.UtcNow;
                if (at > interruptedAt)
                {
                    interruptedAt = at;
                }

                continue;
            }

            pending++;
        }

        if (!anyInProgress)
        {
            return null;
        }

        incomplete += pending;

        string? sourceRoot = sessionDb.GetKv(sessionId, SourceRootKey);
        string? destinationRoot = sessionDb.GetKv(sessionId, DestinationRootKey);
        RestorePlan plan = new(
            sessionId,
            sourceRoot ?? InferDirectory(items[0].SourcePath),
            destinationRoot ?? InferDirectory(items[0].DestinationPath),
            items,
            items.Sum(static item => item.Bytes));
        return new InterruptedRestoreReport(
            sessionId,
            workspaceRoot,
            interruptedAt == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : interruptedAt,
            incomplete,
            completed,
            plan.SourceRoot,
            plan.DestinationRoot,
            plan);
    }

    public static InterruptedRestoreReport? FindLatest(SafeFs safeFs, string? localApplicationData = null)
    {
        ArgumentNullException.ThrowIfNull(safeFs);
        string basePath = localApplicationData ??
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string sessions = Path.Combine(basePath, "WinOldRecovery", "sessions");
        if (!Directory.Exists(sessions))
        {
            return null;
        }

        foreach (string directory in Directory.GetDirectories(sessions).OrderByDescending(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            string databasePath = Path.Combine(directory, "session.db");
            if (!File.Exists(databasePath))
            {
                continue;
            }

            if (IsDatabaseInUse(databasePath))
            {
                continue;
            }

            if (!TryPeekIncompleteJournal(databasePath, out bool hasIncomplete))
            {
                continue;
            }

            if (!hasIncomplete)
            {
                continue;
            }

            SessionDb? database = null;
            try
            {
                database = SessionDb.OpenAsync(databasePath, safeFs).GetAwaiter().GetResult();
                string sessionId = Path.GetFileName(directory);
                InterruptedRestoreReport? report = Describe(database, sessionId, directory);
                if (report is not null)
                {
                    return report;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
            {
                continue;
            }
            finally
            {
                database?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        return null;
    }

    private static DateTimeOffset? ReadLatestJournalTime(SessionDb sessionDb, long planItemId)
    {
        using SqliteConnection connection = sessionDb.OpenReadConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT recorded_at_utc
            FROM journal
            WHERE plan_item_id = $id
            ORDER BY recorded_at_utc DESC, id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", planItemId);
        if (command.ExecuteScalar() is not string value)
        {
            return null;
        }

        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    }

    private static bool TryPeekIncompleteJournal(string databasePath, out bool hasIncomplete)
    {
        hasIncomplete = false;
        SQLitePCL.Batteries_V2.Init();
        try
        {
            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 1,
            }.ToString();
            using SqliteConnection connection = new(connectionString);
            connection.Open();
            using (SqliteCommand pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA query_only = ON; PRAGMA busy_timeout = 1000;";
                pragma.ExecuteNonQuery();
            }

            using (SqliteCommand table = connection.CreateCommand())
            {
                table.CommandText =
                    """
                    SELECT 1
                    FROM sqlite_master
                    WHERE type = 'table' AND name = 'journal'
                    LIMIT 1;
                    """;
                if (table.ExecuteScalar() is null)
                {
                    return true;
                }
            }

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT 1
                FROM journal
                WHERE state IN ('Started', 'Paused', 'Failed')
                LIMIT 1;
                """;
            hasIncomplete = command.ExecuteScalar() is not null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            return false;
        }
    }

    private static bool IsDatabaseInUse(string databasePath)
    {
        try
        {
            using FileStream stream = new(
                databasePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string InferDirectory(string path)
    {
        string? parent = Path.GetDirectoryName(path);
        return string.IsNullOrWhiteSpace(parent) ? path : parent;
    }
}
