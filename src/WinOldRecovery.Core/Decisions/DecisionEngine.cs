using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.Persistence;

namespace WinOldRecovery.Core.Decisions;

public sealed class DecisionEngine
{
    public const int UndoLimit = 50;
    public const string UndoKvKey = "decision.undo";

    private readonly SessionDb sessionDb;
    private readonly string sessionId;

    public DecisionEngine(SessionDb sessionDb, string sessionId)
    {
        this.sessionDb = sessionDb ?? throw new ArgumentNullException(nameof(sessionDb));
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        this.sessionId = sessionId;
    }

    public Task SetUserDecisionAsync(
        long nodeId,
        Decision decision,
        CancellationToken cancellationToken = default)
    {
        return SetDecisionAsync(nodeId, decision, DecisionSource.User, cancellationToken);
    }

    public Task SetSuggestedDefaultAsync(
        long nodeId,
        Decision decision,
        CancellationToken cancellationToken = default)
    {
        return SetDecisionAsync(nodeId, decision, DecisionSource.SuggestedDefault, cancellationToken);
    }

    public async Task ClearUserDecisionAsync(
        long nodeId,
        CancellationToken cancellationToken = default)
    {
        await PushUndoAsync(nodeId, DecisionSource.User, cancellationToken).ConfigureAwait(false);
        await sessionDb.WriteAsync(
                async (connection, token) =>
                {
                    await using SqliteCommand command = connection.CreateCommand();
                    command.CommandText =
                        """
                        DELETE FROM decisions
                        WHERE node_id = $nodeId AND source = 'User';
                        """;
                    command.Parameters.AddWithValue("$nodeId", nodeId);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    await RefreshEffectiveAsync(connection, nodeId, token).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task UndoAsync(CancellationToken cancellationToken = default)
    {
        List<UndoEntry> stack = ReadUndoStack();
        if (stack.Count == 0)
        {
            return;
        }

        UndoEntry entry = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        await sessionDb.SetKvAsync(
                sessionId,
                UndoKvKey,
                JsonSerializer.Serialize(stack),
                cancellationToken)
            .ConfigureAwait(false);

        await sessionDb.WriteAsync(
                async (connection, token) =>
                {
                    await using SqliteCommand delete = connection.CreateCommand();
                    delete.CommandText =
                        """
                        DELETE FROM decisions
                        WHERE node_id = $nodeId AND source = $source;
                        """;
                    delete.Parameters.AddWithValue("$nodeId", entry.NodeId);
                    delete.Parameters.AddWithValue("$source", entry.Source);
                    await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                    if (entry.PreviousDecision is not null)
                    {
                        await using SqliteCommand insert = connection.CreateCommand();
                        insert.CommandText =
                            """
                            INSERT INTO decisions(node_id, decision, source, decided_at_utc)
                            VALUES ($nodeId, $decision, $source, $at);
                            """;
                        insert.Parameters.AddWithValue("$nodeId", entry.NodeId);
                        insert.Parameters.AddWithValue("$decision", entry.PreviousDecision);
                        insert.Parameters.AddWithValue("$source", entry.Source);
                        insert.Parameters.AddWithValue("$at", entry.PreviousDecidedAtUtc ?? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                        await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    await RefreshEffectiveAsync(connection, entry.NodeId, token).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Decision GetEffectiveDecision(long nodeId)
    {
        using SqliteConnection connection = sessionDb.OpenReadConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT eff_decision FROM nodes WHERE id = $id;";
        command.Parameters.AddWithValue("$id", nodeId);
        object? value = command.ExecuteScalar();
        if (value is null or DBNull)
        {
            throw new InvalidOperationException($"Node {nodeId} was not found.");
        }

        return Enum.Parse<Decision>((string)value);
    }

    public SubtreeDecisionSummary GetSubtreeSummary(long nodeId)
    {
        using SqliteConnection connection = sessionDb.OpenReadConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            WITH RECURSIVE subtree(id) AS (
                SELECT id FROM nodes WHERE id = $id
                UNION ALL
                SELECT child.id
                FROM nodes AS child
                INNER JOIN subtree ON child.parent_id = subtree.id
            )
            SELECT
                SUM(CASE WHEN eff_decision = 'Restore' THEN 1 ELSE 0 END),
                SUM(CASE WHEN eff_decision = 'LeaveBehind' THEN 1 ELSE 0 END),
                SUM(CASE WHEN eff_decision = 'Undecided' THEN 1 ELSE 0 END)
            FROM nodes
            WHERE id IN (SELECT id FROM subtree);
            """;
        command.Parameters.AddWithValue("$id", nodeId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new SubtreeDecisionSummary(Decision.Undecided, Mixed: false, 0, 0, 0);
        }

        int restore = Convert.ToInt32(reader.GetInt64(0));
        int leave = Convert.ToInt32(reader.GetInt64(1));
        int undecided = Convert.ToInt32(reader.GetInt64(2));
        int kinds = (restore > 0 ? 1 : 0) + (leave > 0 ? 1 : 0) + (undecided > 0 ? 1 : 0);
        Decision effective = GetEffectiveDecision(nodeId);
        return new SubtreeDecisionSummary(effective, kinds > 1, restore, leave, undecided);
    }

    private async Task SetDecisionAsync(
        long nodeId,
        Decision decision,
        DecisionSource source,
        CancellationToken cancellationToken)
    {
        if (source is not DecisionSource.User and not DecisionSource.SuggestedDefault)
        {
            throw new ArgumentOutOfRangeException(nameof(source), "Inherited decisions are computed, not stored.");
        }

        await PushUndoAsync(nodeId, source, cancellationToken).ConfigureAwait(false);
        await sessionDb.WriteAsync(
                async (connection, token) =>
                {
                    await using SqliteCommand command = connection.CreateCommand();
                    command.CommandText =
                        """
                        INSERT INTO decisions(node_id, decision, source, decided_at_utc)
                        VALUES ($nodeId, $decision, $source, $at)
                        ON CONFLICT(node_id, source) DO UPDATE SET
                            decision = excluded.decision,
                            decided_at_utc = excluded.decided_at_utc;
                        """;
                    command.Parameters.AddWithValue("$nodeId", nodeId);
                    command.Parameters.AddWithValue("$decision", decision.ToString());
                    command.Parameters.AddWithValue("$source", source.ToString());
                    command.Parameters.AddWithValue(
                        "$at",
                        DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    await RefreshEffectiveAsync(connection, nodeId, token).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task PushUndoAsync(
        long nodeId,
        DecisionSource source,
        CancellationToken cancellationToken)
    {
        DecisionSnapshot? snapshot = ReadSnapshot(nodeId, source);
        List<UndoEntry> stack = ReadUndoStack();
        stack.Add(
            new UndoEntry(
                nodeId,
                source.ToString(),
                snapshot?.Decision,
                snapshot?.DecidedAtUtc));
        if (stack.Count > UndoLimit)
        {
            stack.RemoveRange(0, stack.Count - UndoLimit);
        }

        await sessionDb.SetKvAsync(
                sessionId,
                UndoKvKey,
                JsonSerializer.Serialize(stack),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private DecisionSnapshot? ReadSnapshot(long nodeId, DecisionSource source)
    {
        using SqliteConnection connection = sessionDb.OpenReadConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT decision, decided_at_utc
            FROM decisions
            WHERE node_id = $nodeId AND source = $source;
            """;
        command.Parameters.AddWithValue("$nodeId", nodeId);
        command.Parameters.AddWithValue("$source", source.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new DecisionSnapshot(reader.GetString(0), reader.GetString(1));
    }

    private List<UndoEntry> ReadUndoStack()
    {
        string? json = sessionDb.GetKv(sessionId, UndoKvKey);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<UndoEntry>>(json) ?? [];
    }

    private static async Task RefreshEffectiveAsync(
        SqliteConnection connection,
        long rootNodeId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            WITH RECURSIVE subtree(id) AS (
                SELECT id FROM nodes WHERE id = $rootId
                UNION ALL
                SELECT child.id
                FROM nodes AS child
                INNER JOIN subtree ON child.parent_id = subtree.id
            ),
            ancestors(node_id, walk_id, parent_id, depth) AS (
                SELECT id, id, parent_id, 0
                FROM nodes
                WHERE id IN (SELECT id FROM subtree)
                UNION ALL
                SELECT ancestors.node_id, parent.id, parent.parent_id, ancestors.depth + 1
                FROM ancestors
                INNER JOIN nodes AS parent ON parent.id = ancestors.parent_id
            ),
            user_pick AS (
                SELECT node_id, decision
                FROM (
                    SELECT
                        a.node_id,
                        d.decision,
                        ROW_NUMBER() OVER (
                            PARTITION BY a.node_id
                            ORDER BY a.depth) AS row_number
                    FROM ancestors AS a
                    INNER JOIN decisions AS d
                        ON d.node_id = a.walk_id AND d.source = 'User'
                ) AS ranked
                WHERE row_number = 1
            ),
            suggested_pick AS (
                SELECT d.node_id, d.decision
                FROM decisions AS d
                WHERE d.source = 'SuggestedDefault'
                  AND d.node_id IN (SELECT id FROM subtree)
            )
            UPDATE nodes
            SET eff_decision = COALESCE(
                (SELECT decision FROM user_pick WHERE user_pick.node_id = nodes.id),
                (SELECT decision FROM suggested_pick WHERE suggested_pick.node_id = nodes.id),
                'Undecided')
            WHERE id IN (SELECT id FROM subtree);
            """;
        command.Parameters.AddWithValue("$rootId", rootNodeId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record UndoEntry(
        long NodeId,
        string Source,
        string? PreviousDecision,
        string? PreviousDecidedAtUtc);

    private sealed record DecisionSnapshot(string Decision, string DecidedAtUtc);
}
