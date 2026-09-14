using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Planning;
using WinOldRecovery.Core.Recipes;
using WinOldRecovery.Core.Scan;
using WinOldRecovery.Core.Verify;

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

    public Task InsertProfilesAsync(
        IReadOnlyList<ProfileRecord> profiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (profiles.Count == 0)
        {
            return Task.CompletedTask;
        }

        return WriteAsync(
            async (connection, token) =>
            {
                using SqliteTransaction transaction = connection.BeginTransaction();
                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO profiles(session_id, name, source_path, kind, last_used_utc)
                    VALUES ($sessionId, $name, $sourcePath, $kind, $lastUsed);
                    """;
                SqliteParameter sessionId = command.Parameters.Add("$sessionId", SqliteType.Text);
                SqliteParameter name = command.Parameters.Add("$name", SqliteType.Text);
                SqliteParameter sourcePath = command.Parameters.Add("$sourcePath", SqliteType.Text);
                SqliteParameter kind = command.Parameters.Add("$kind", SqliteType.Text);
                SqliteParameter lastUsed = command.Parameters.Add("$lastUsed", SqliteType.Text);

                foreach (ProfileRecord profile in profiles)
                {
                    sessionId.Value = profile.SessionId;
                    name.Value = profile.Name;
                    sourcePath.Value = profile.SourcePath;
                    kind.Value = profile.Kind.ToString();
                    lastUsed.Value = profile.LastUsedUtc is { } used
                        ? used.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                        : DBNull.Value;
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                transaction.Commit();
            },
            cancellationToken);
    }

    public IReadOnlyList<ProfileRecord> ListProfiles(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        using SqliteConnection connection = OpenReadConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, name, source_path, kind, last_used_utc
            FROM profiles
            WHERE session_id = $sessionId
            ORDER BY name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        List<ProfileRecord> profiles = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            DateTimeOffset? lastUsed = reader.IsDBNull(4)
                ? null
                : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture);
            profiles.Add(
                new ProfileRecord(
                    sessionId,
                    reader.GetString(1),
                    reader.GetString(2),
                    Enum.Parse<ProfileKind>(reader.GetString(3)),
                    lastUsed,
                    reader.GetInt64(0)));
        }

        return profiles;
    }

    public Task InsertNodesAsync(
        IReadOnlyList<PersistedNode> nodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Count == 0)
        {
            return Task.CompletedTask;
        }

        return WriteAsync(
            async (connection, token) =>
            {
                using SqliteTransaction transaction = connection.BeginTransaction();
                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO nodes(
                        id,
                        session_id,
                        profile_id,
                        parent_id,
                        name,
                        rel_path,
                        kind,
                        size,
                        agg_size,
                        agg_files,
                        mtime_utc,
                        attributes,
                        problem)
                    VALUES (
                        $id,
                        $sessionId,
                        $profileId,
                        $parentId,
                        $name,
                        $relPath,
                        $kind,
                        $size,
                        $aggSize,
                        $aggFiles,
                        $mtime,
                        $attributes,
                        $problem);
                    """;
                SqliteParameter id = command.Parameters.Add("$id", SqliteType.Integer);
                SqliteParameter sessionId = command.Parameters.Add("$sessionId", SqliteType.Text);
                SqliteParameter profileId = command.Parameters.Add("$profileId", SqliteType.Integer);
                SqliteParameter parentId = command.Parameters.Add("$parentId", SqliteType.Integer);
                SqliteParameter name = command.Parameters.Add("$name", SqliteType.Text);
                SqliteParameter relPath = command.Parameters.Add("$relPath", SqliteType.Text);
                SqliteParameter kind = command.Parameters.Add("$kind", SqliteType.Text);
                SqliteParameter size = command.Parameters.Add("$size", SqliteType.Integer);
                SqliteParameter aggSize = command.Parameters.Add("$aggSize", SqliteType.Integer);
                SqliteParameter aggFiles = command.Parameters.Add("$aggFiles", SqliteType.Integer);
                SqliteParameter mtime = command.Parameters.Add("$mtime", SqliteType.Text);
                SqliteParameter attributes = command.Parameters.Add("$attributes", SqliteType.Integer);
                SqliteParameter problem = command.Parameters.Add("$problem", SqliteType.Text);

                foreach (PersistedNode node in nodes)
                {
                    id.Value = node.Id;
                    sessionId.Value = node.SessionId;
                    profileId.Value = (object?)node.ProfileId ?? DBNull.Value;
                    parentId.Value = (object?)node.ParentId ?? DBNull.Value;
                    name.Value = node.Name;
                    relPath.Value = node.RelPath;
                    kind.Value = node.Kind.ToString();
                    size.Value = node.Size;
                    aggSize.Value = node.AggSize;
                    aggFiles.Value = node.AggFiles;
                    mtime.Value = node.LastWriteTimeUtc is { } lastWrite
                        ? DateTime.SpecifyKind(lastWrite, DateTimeKind.Utc)
                            .ToString("O", CultureInfo.InvariantCulture)
                        : DBNull.Value;
                    attributes.Value = node.Attributes;
                    problem.Value = node.Problem.ToString();
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                transaction.Commit();
            },
            cancellationToken);
    }

    public Task UpdateNodeAggregatesAsync(
        IReadOnlyList<NodeAggregateUpdate> updates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count == 0)
        {
            return Task.CompletedTask;
        }

        return WriteAsync(
            async (connection, token) =>
            {
                using SqliteTransaction transaction = connection.BeginTransaction();
                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE nodes
                    SET agg_size = $aggSize,
                        agg_files = $aggFiles,
                        problem = $problem
                    WHERE id = $id;
                    """;
                SqliteParameter id = command.Parameters.Add("$id", SqliteType.Integer);
                SqliteParameter aggSize = command.Parameters.Add("$aggSize", SqliteType.Integer);
                SqliteParameter aggFiles = command.Parameters.Add("$aggFiles", SqliteType.Integer);
                SqliteParameter problem = command.Parameters.Add("$problem", SqliteType.Text);

                foreach (NodeAggregateUpdate update in updates)
                {
                    id.Value = update.Id;
                    aggSize.Value = update.AggSize;
                    aggFiles.Value = update.AggFiles;
                    problem.Value = update.Problem.ToString();
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                transaction.Commit();
            },
            cancellationToken);
    }

    public Task InsertBadgesAsync(
        IReadOnlyList<NodeBadgeRow> badges,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(badges);
        if (badges.Count == 0)
        {
            return Task.CompletedTask;
        }

        return WriteAsync(
            async (connection, token) =>
            {
                using SqliteTransaction transaction = connection.BeginTransaction();
                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT OR IGNORE INTO badges(node_id, kind, detail)
                    VALUES ($nodeId, $kind, $detail);
                    """;
                SqliteParameter nodeId = command.Parameters.Add("$nodeId", SqliteType.Integer);
                SqliteParameter kind = command.Parameters.Add("$kind", SqliteType.Text);
                SqliteParameter detail = command.Parameters.Add("$detail", SqliteType.Text);

                foreach (NodeBadgeRow badge in badges)
                {
                    nodeId.Value = badge.NodeId;
                    kind.Value = badge.Kind;
                    detail.Value = badge.Detail;
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                transaction.Commit();
            },
            cancellationToken);
    }

    public IReadOnlyList<ClassificationNodeRow> ListClassificationNodes(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        using SqliteConnection connection = OpenReadConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, parent_id, name, rel_path, kind, size, agg_size, problem, sensitive
            FROM nodes
            WHERE session_id = $sessionId
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        List<ClassificationNodeRow> rows = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(
                new ClassificationNodeRow(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    Enum.Parse<NodeKind>(reader.GetString(4)),
                    reader.GetInt64(5),
                    reader.GetInt64(6),
                    Enum.Parse<NodeProblem>(reader.GetString(7)),
                    reader.GetInt64(8) != 0));
        }

        return rows;
    }

    public Task MarkNodesSensitiveAsync(
        IReadOnlyList<long> nodeIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        if (nodeIds.Count == 0)
        {
            return Task.CompletedTask;
        }

        return WriteAsync(
            async (connection, token) =>
            {
                using SqliteTransaction transaction = connection.BeginTransaction();
                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE nodes SET sensitive = 1 WHERE id = $id;";
                SqliteParameter id = command.Parameters.Add("$id", SqliteType.Integer);
                foreach (long nodeId in nodeIds)
                {
                    id.Value = nodeId;
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                transaction.Commit();
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<PlanItem>> ReplacePlanItemsAsync(
        string sessionId,
        IReadOnlyList<PlanItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(items);

        return WriteAsync(
            async (connection, token) =>
            {
                using SqliteTransaction transaction = connection.BeginTransaction();
                await using SqliteCommand clear = connection.CreateCommand();
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM plan_items WHERE session_id = $sessionId;";
                clear.Parameters.AddWithValue("$sessionId", sessionId);
                await clear.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO plan_items(
                        session_id, job_id, operation, source_path, destination_path,
                        bytes, conflict_policy, overwrite_approved, recipe_id)
                    VALUES (
                        $sessionId, $jobId, $operation, $sourcePath, $destinationPath,
                        $bytes, $conflictPolicy, $overwriteApproved, $recipeId);
                    """;
                SqliteParameter sessionParam = command.Parameters.Add("$sessionId", SqliteType.Text);
                SqliteParameter jobId = command.Parameters.Add("$jobId", SqliteType.Integer);
                SqliteParameter operation = command.Parameters.Add("$operation", SqliteType.Text);
                SqliteParameter sourcePath = command.Parameters.Add("$sourcePath", SqliteType.Text);
                SqliteParameter destinationPath = command.Parameters.Add("$destinationPath", SqliteType.Text);
                SqliteParameter bytes = command.Parameters.Add("$bytes", SqliteType.Integer);
                SqliteParameter conflictPolicy = command.Parameters.Add("$conflictPolicy", SqliteType.Text);
                SqliteParameter overwriteApproved = command.Parameters.Add("$overwriteApproved", SqliteType.Integer);
                SqliteParameter recipeId = command.Parameters.Add("$recipeId", SqliteType.Text);

                List<PlanItem> stored = [];
                foreach (PlanItem item in items)
                {
                    sessionParam.Value = item.SessionId;
                    jobId.Value = item.JobId;
                    operation.Value = item.Operation.ToString();
                    sourcePath.Value = item.SourcePath;
                    destinationPath.Value = item.DestinationPath;
                    bytes.Value = item.Bytes;
                    conflictPolicy.Value = item.ConflictPolicy.ToString();
                    overwriteApproved.Value = item.OverwriteApproved ? 1 : 0;
                    recipeId.Value = (object?)item.RecipeId ?? DBNull.Value;
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    await using SqliteCommand idCommand = connection.CreateCommand();
                    idCommand.Transaction = transaction;
                    idCommand.CommandText = "SELECT last_insert_rowid();";
                    long id = (long)(await idCommand.ExecuteScalarAsync(token).ConfigureAwait(false) ?? 0L);
                    stored.Add(item with { Id = id });
                }

                transaction.Commit();
                return (IReadOnlyList<PlanItem>)stored;
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<PlanItem>> InsertPlanItemsAsync(
        IReadOnlyList<PlanItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<PlanItem>>([]);
        }

        return WriteAsync(
            async (connection, token) =>
            {
                using SqliteTransaction transaction = connection.BeginTransaction();
                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO plan_items(
                        session_id, job_id, operation, source_path, destination_path,
                        bytes, conflict_policy, overwrite_approved, recipe_id)
                    VALUES (
                        $sessionId, $jobId, $operation, $sourcePath, $destinationPath,
                        $bytes, $conflictPolicy, $overwriteApproved, $recipeId);
                    """;
                SqliteParameter sessionParam = command.Parameters.Add("$sessionId", SqliteType.Text);
                SqliteParameter jobId = command.Parameters.Add("$jobId", SqliteType.Integer);
                SqliteParameter operation = command.Parameters.Add("$operation", SqliteType.Text);
                SqliteParameter sourcePath = command.Parameters.Add("$sourcePath", SqliteType.Text);
                SqliteParameter destinationPath = command.Parameters.Add("$destinationPath", SqliteType.Text);
                SqliteParameter bytes = command.Parameters.Add("$bytes", SqliteType.Integer);
                SqliteParameter conflictPolicy = command.Parameters.Add("$conflictPolicy", SqliteType.Text);
                SqliteParameter overwriteApproved = command.Parameters.Add("$overwriteApproved", SqliteType.Integer);
                SqliteParameter recipeId = command.Parameters.Add("$recipeId", SqliteType.Text);

                List<PlanItem> stored = [];
                foreach (PlanItem item in items)
                {
                    sessionParam.Value = item.SessionId;
                    jobId.Value = item.JobId;
                    operation.Value = item.Operation.ToString();
                    sourcePath.Value = item.SourcePath;
                    destinationPath.Value = item.DestinationPath;
                    bytes.Value = item.Bytes;
                    conflictPolicy.Value = item.ConflictPolicy.ToString();
                    overwriteApproved.Value = item.OverwriteApproved ? 1 : 0;
                    recipeId.Value = (object?)item.RecipeId ?? DBNull.Value;
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    await using SqliteCommand idCommand = connection.CreateCommand();
                    idCommand.Transaction = transaction;
                    idCommand.CommandText = "SELECT last_insert_rowid();";
                    long id = (long)(await idCommand.ExecuteScalarAsync(token).ConfigureAwait(false) ?? 0L);
                    stored.Add(item with { Id = id });
                }

                transaction.Commit();
                return (IReadOnlyList<PlanItem>)stored;
            },
            cancellationToken);
    }

    public IReadOnlyList<PlanItem> ListPlanItems(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        using SqliteConnection connection = OpenReadConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, session_id, job_id, operation, source_path, destination_path,
                   bytes, conflict_policy, overwrite_approved, recipe_id
            FROM plan_items
            WHERE session_id = $sessionId
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        List<PlanItem> items = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(
                new PlanItem(
                    reader.GetString(1),
                    reader.GetInt32(2),
                    Enum.Parse<PlanOperation>(reader.GetString(3)),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt64(6),
                    Enum.Parse<ConflictPolicy>(reader.GetString(7)),
                    reader.GetInt64(8) != 0,
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.GetInt64(0)));
        }

        return items;
    }

    public Task AppendJournalAsync(
        long planItemId,
        string state,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        return WriteAsync(
            async (connection, token) =>
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO journal(plan_item_id, state, recorded_at_utc, error)
                    VALUES ($planItemId, $state, $at, $error);
                    """;
                command.Parameters.AddWithValue("$planItemId", planItemId);
                command.Parameters.AddWithValue("$state", state);
                command.Parameters.AddWithValue(
                    "$at",
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public string? GetLatestJournalState(long planItemId)
    {
        using SqliteConnection connection = OpenReadConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT state
            FROM journal
            WHERE plan_item_id = $id
            ORDER BY recorded_at_utc DESC, id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", planItemId);
        return command.ExecuteScalar() as string;
    }

    public Task InsertVerifyResultsAsync(
        IReadOnlyList<VerifyResultRow> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return Task.CompletedTask;
        }

        return WriteAsync(
            async (connection, token) =>
            {
                using SqliteTransaction transaction = connection.BeginTransaction();
                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO verify_results(
                        plan_item_id, report_id, level, ok, detail, recorded_at_utc)
                    VALUES ($planItemId, $reportId, $level, $ok, $detail, $at);
                    """;
                SqliteParameter planItemId = command.Parameters.Add("$planItemId", SqliteType.Integer);
                SqliteParameter reportId = command.Parameters.Add("$reportId", SqliteType.Text);
                SqliteParameter level = command.Parameters.Add("$level", SqliteType.Integer);
                SqliteParameter ok = command.Parameters.Add("$ok", SqliteType.Integer);
                SqliteParameter detail = command.Parameters.Add("$detail", SqliteType.Text);
                SqliteParameter at = command.Parameters.Add("$at", SqliteType.Text);
                foreach (VerifyResultRow row in rows)
                {
                    planItemId.Value = row.PlanItemId;
                    reportId.Value = row.ReportId;
                    level.Value = row.Level;
                    ok.Value = row.Ok ? 1 : 0;
                    detail.Value = row.Detail;
                    at.Value = row.RecordedAtUtc.ToUniversalTime()
                        .ToString("O", CultureInfo.InvariantCulture);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                transaction.Commit();
            },
            cancellationToken);
    }

    public Task ReplaceRecipeCardsAsync(
        string sessionId,
        IReadOnlyList<PersistedRecipeCard> cards,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(cards);
        return WriteAsync(
            async (connection, token) =>
            {
                using SqliteTransaction transaction = connection.BeginTransaction();
                await using SqliteCommand clear = connection.CreateCommand();
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM cards WHERE session_id = $sessionId;";
                clear.Parameters.AddWithValue("$sessionId", sessionId);
                await clear.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO cards(session_id, recipe_id, profile_id, title, json)
                    VALUES ($sessionId, $recipeId, $profileId, $title, $json);
                    """;
                SqliteParameter sessionParam = command.Parameters.Add("$sessionId", SqliteType.Text);
                SqliteParameter recipeId = command.Parameters.Add("$recipeId", SqliteType.Text);
                SqliteParameter profileId = command.Parameters.Add("$profileId", SqliteType.Integer);
                SqliteParameter title = command.Parameters.Add("$title", SqliteType.Text);
                SqliteParameter json = command.Parameters.Add("$json", SqliteType.Text);
                foreach (PersistedRecipeCard card in cards)
                {
                    sessionParam.Value = card.SessionId;
                    recipeId.Value = card.RecipeId;
                    profileId.Value = (object?)card.ProfileId ?? DBNull.Value;
                    title.Value = card.Title;
                    json.Value = card.Json;
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                    if (card.Components is { Count: > 0 })
                    {
                        await using SqliteCommand idCommand = connection.CreateCommand();
                        idCommand.Transaction = transaction;
                        idCommand.CommandText = "SELECT last_insert_rowid();";
                        long cardId = (long)(await idCommand.ExecuteScalarAsync(token).ConfigureAwait(false) ?? 0L);
                        await using SqliteCommand componentCommand = connection.CreateCommand();
                        componentCommand.Transaction = transaction;
                        componentCommand.CommandText =
                            """
                            INSERT INTO components(card_id, key, decision, fixed, fixed_reason)
                            VALUES ($cardId, $key, $decision, $fixed, $reason);
                            """;
                        SqliteParameter cardIdParam = componentCommand.Parameters.Add("$cardId", SqliteType.Integer);
                        SqliteParameter keyParam = componentCommand.Parameters.Add("$key", SqliteType.Text);
                        SqliteParameter decisionParam = componentCommand.Parameters.Add("$decision", SqliteType.Text);
                        SqliteParameter fixedParam = componentCommand.Parameters.Add("$fixed", SqliteType.Integer);
                        SqliteParameter reasonParam = componentCommand.Parameters.Add("$reason", SqliteType.Text);
                        foreach (RecipeComponent component in card.Components)
                        {
                            cardIdParam.Value = cardId;
                            keyParam.Value = component.Key;
                            decisionParam.Value = component.SuggestedDefault.ToString();
                            fixedParam.Value = component.Fixed ? 1 : 0;
                            reasonParam.Value = (object?)component.FixedReason ?? DBNull.Value;
                            await componentCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        }
                    }
                }

                transaction.Commit();
            },
            cancellationToken);
    }

    public IReadOnlyList<PersistedRecipeCard> ListRecipeCards(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        using SqliteConnection connection = OpenReadConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, recipe_id, profile_id, title, json
            FROM cards
            WHERE session_id = $sessionId
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        List<PersistedRecipeCard> cards = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            cards.Add(
                new PersistedRecipeCard(
                    sessionId,
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt64(0)));
        }

        return cards;
    }

    public Task SetKvAsync(
        string sessionId,
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        return WriteAsync(
            async (connection, token) =>
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO kv(session_id, key, value)
                    VALUES ($sessionId, $key, $value)
                    ON CONFLICT(session_id, key) DO UPDATE SET value = excluded.value;
                    """;
                command.Parameters.AddWithValue("$sessionId", sessionId);
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$value", value);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public string? GetKv(string sessionId, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        using SqliteConnection connection = OpenReadConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT value
            FROM kv
            WHERE session_id = $sessionId AND key = $key;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public long GetMaxNodeId(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        using SqliteConnection connection = OpenReadConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COALESCE(MAX(id), 0)
            FROM nodes
            WHERE session_id = $sessionId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public long? FindNodeId(string sessionId, string relPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(relPath);

        using SqliteConnection connection = OpenReadConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id
            FROM nodes
            WHERE session_id = $sessionId AND rel_path = $relPath
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$relPath", relPath);
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public ChildAggregate GetChildAggregates(string sessionId, long parentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        using SqliteConnection connection = OpenReadConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COALESCE(SUM(agg_size), 0), COALESCE(SUM(agg_files), 0)
            FROM nodes
            WHERE session_id = $sessionId AND parent_id = $parentId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$parentId", parentId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new ChildAggregate(0, 0);
        }

        return new ChildAggregate(reader.GetInt64(0), reader.GetInt64(1));
    }

    public Task DeleteNodeSubtreeAsync(
        string sessionId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(relativePath);

        return WriteAsync(
            async (connection, token) =>
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    """
                    DELETE FROM nodes
                    WHERE session_id = $sessionId
                      AND (
                            rel_path = $relPath
                         OR rel_path LIKE $prefix ESCAPE '\'
                      );
                    """;
                command.Parameters.AddWithValue("$sessionId", sessionId);
                command.Parameters.AddWithValue("$relPath", relativePath);
                command.Parameters.AddWithValue("$prefix", EscapeLikePrefix(relativePath) + @"\%");
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task ClearScanDataAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return WriteAsync(
            async (connection, token) =>
            {
                using SqliteTransaction transaction = connection.BeginTransaction();
                await ExecClearAsync(
                        connection,
                        transaction,
                        "DELETE FROM plan_items WHERE session_id = $sessionId;",
                        sessionId,
                        token)
                    .ConfigureAwait(false);
                await ExecClearAsync(
                        connection,
                        transaction,
                        "DELETE FROM cards WHERE session_id = $sessionId;",
                        sessionId,
                        token)
                    .ConfigureAwait(false);
                await ExecClearAsync(
                        connection,
                        transaction,
                        "UPDATE nodes SET parent_id = NULL WHERE session_id = $sessionId;",
                        sessionId,
                        token)
                    .ConfigureAwait(false);
                await ExecClearAsync(
                        connection,
                        transaction,
                        "DELETE FROM nodes WHERE session_id = $sessionId;",
                        sessionId,
                        token)
                    .ConfigureAwait(false);
                await ExecClearAsync(
                        connection,
                        transaction,
                        "DELETE FROM profiles WHERE session_id = $sessionId;",
                        sessionId,
                        token)
                    .ConfigureAwait(false);
                await ExecClearAsync(
                        connection,
                        transaction,
                        "DELETE FROM kv WHERE session_id = $sessionId;",
                        sessionId,
                        token)
                    .ConfigureAwait(false);
                transaction.Commit();
            },
            cancellationToken);
    }

    private static async Task ExecClearAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string sessionId,
        CancellationToken token)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    public IReadOnlyList<string> GetChildNames(string sessionId, long parentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        using SqliteConnection connection = OpenReadConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name
            FROM nodes
            WHERE session_id = $sessionId AND parent_id = $parentId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$parentId", parentId);
        List<string> names = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static string EscapeLikePrefix(string value)
    {
        return value
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
    }

    public static string SerializeWalkerCheckpoint(WalkerCheckpoint checkpoint)
    {
        return JsonSerializer.Serialize(checkpoint);
    }

    public static WalkerCheckpoint? DeserializeWalkerCheckpoint(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<WalkerCheckpoint>(json);
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
