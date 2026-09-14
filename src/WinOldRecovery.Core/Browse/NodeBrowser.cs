using System.Globalization;
using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Scan;

namespace WinOldRecovery.Core.Browse;

public sealed class NodeBrowser
{
    public const int ChildPageSize = 2000;
    public const int LargestListSize = 500;

    private readonly SessionDb sessionDb;
    private readonly string sessionId;

    public NodeBrowser(SessionDb sessionDb, string sessionId)
    {
        this.sessionDb = sessionDb ?? throw new ArgumentNullException(nameof(sessionDb));
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        this.sessionId = sessionId;
    }

    public NodePage GetChildren(long? parentId, int offset = 0, int limit = ChildPageSize)
    {
        return Query(
            parentFilter: parentId is null
                ? "parent_id IS NULL"
                : "parent_id = $parentId",
            extraFilter: null,
            orderBy: "name COLLATE NOCASE",
            offset,
            limit,
            parentId);
    }

    public NodePage GetLargest(long? underNodeId, bool folders)
    {
        string extra = folders
            ? "kind = 'Directory'"
            : "kind <> 'Directory'";
        string under = RelPathFilter(underNodeId, out string? prefix);
        return Query(
            parentFilter: "1 = 1",
            extraFilter: Combine(extra, under),
            orderBy: folders ? "agg_size DESC, name COLLATE NOCASE" : "size DESC, name COLLATE NOCASE",
            offset: 0,
            limit: LargestListSize,
            parentId: null,
            relPrefix: prefix);
    }

    public NodePage GetRecent(int days, long? underNodeId, int offset = 0)
    {
        DateTime cutoff = DateTime.UtcNow.AddDays(-days);
        string under = RelPathFilter(underNodeId, out string? prefix);
        return Query(
            parentFilter: "1 = 1",
            extraFilter: Combine("mtime_utc >= $cutoff AND kind <> 'Directory'", under),
            orderBy: "parent_id, mtime_utc DESC, name COLLATE NOCASE",
            offset,
            limit: ChildPageSize,
            parentId: null,
            relPrefix: prefix,
            cutoffUtc: new DateTimeOffset(cutoff, TimeSpan.Zero));
    }

    public NodePage Search(string query, long? underNodeId, int offset = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        string under = RelPathFilter(underNodeId, out string? prefix);
        bool glob = query.Contains('*', StringComparison.Ordinal) ||
            query.Contains('?', StringComparison.Ordinal);
        return Query(
            parentFilter: "1 = 1",
            extraFilter: Combine("name LIKE $query ESCAPE '\\'", under),
            orderBy: "rel_path COLLATE NOCASE",
            offset,
            limit: ChildPageSize,
            parentId: null,
            relPrefix: prefix,
            search: glob ? GlobToLike(query) : "%" + EscapeLike(query) + "%");
    }

    public NodePage GetUnknown(long? underNodeId, int offset = 0)
    {
        string under = RelPathFilter(underNodeId, out string? prefix);
        string names = string.Join(
            ",",
            ProfileDetector.StandardFolderNames.Select(static name => "'" + name.Replace("'", "''", StringComparison.Ordinal) + "'"));
        return Query(
            parentFilter: "1 = 1",
            extraFilter: Combine(
                $"""
                kind = 'Directory'
                AND name NOT IN ({names})
                AND name <> 'AppData'
                AND NOT EXISTS (SELECT 1 FROM badges WHERE badges.node_id = nodes.id)
                """,
                under),
            orderBy: "agg_size DESC, name COLLATE NOCASE",
            offset,
            limit: ChildPageSize,
            parentId: null,
            relPrefix: prefix);
    }

    public NodePage GetProblems(int offset = 0)
    {
        return Query(
            parentFilter: "1 = 1",
            extraFilter: "problem <> 'None'",
            orderBy: "problem, rel_path COLLATE NOCASE",
            offset,
            limit: ChildPageSize,
            parentId: null);
    }

    public TreeNodeRow? GetNode(long nodeId)
    {
        NodePage page = Query(
            parentFilter: "id = $parentId",
            extraFilter: null,
            orderBy: "id",
            offset: 0,
            limit: 1,
            parentId: nodeId);
        return page.Rows.Count == 0 ? null : page.Rows[0];
    }

    public (DateTimeOffset? Oldest, DateTimeOffset? Newest) GetMtimeRange(long nodeId)
    {
        TreeNodeRow? node = GetNode(nodeId);
        if (node is null)
        {
            return (null, null);
        }

        using SqliteConnection connection = sessionDb.OpenReadConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.Parameters.AddWithValue("$sessionId", sessionId);
        if (string.IsNullOrEmpty(node.RelPath))
        {
            command.CommandText =
                """
                SELECT MIN(mtime_utc), MAX(mtime_utc)
                FROM nodes
                WHERE session_id = $sessionId
                  AND mtime_utc IS NOT NULL
                  AND kind NOT IN ('Directory', 'Junction', 'Symlink', 'MountPoint')
                """;
        }
        else
        {
            command.CommandText =
                """
                SELECT MIN(mtime_utc), MAX(mtime_utc)
                FROM nodes
                WHERE session_id = $sessionId
                  AND mtime_utc IS NOT NULL
                  AND kind NOT IN ('Directory', 'Junction', 'Symlink', 'MountPoint')
                  AND (id = $id OR rel_path = $relExact OR rel_path LIKE $relPrefix ESCAPE '\')
                """;
            command.Parameters.AddWithValue("$id", nodeId);
            command.Parameters.AddWithValue("$relExact", node.RelPath);
            command.Parameters.AddWithValue("$relPrefix", EscapeLike(node.RelPath) + @"\\%");
        }

        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1))
        {
            return (node.ModifiedUtc, node.ModifiedUtc);
        }

        return (
            DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture));
    }

    public static string FormatMtimeRange(DateTimeOffset? oldest, DateTimeOffset? newest)
    {
        if (oldest is null && newest is null)
        {
            return string.Empty;
        }

        string oldLabel = oldest?.ToString("d MMM yyyy", CultureInfo.InvariantCulture) ?? "—";
        string newLabel = newest?.ToString("d MMM yyyy", CultureInfo.InvariantCulture) ?? "—";
        if (string.Equals(oldLabel, newLabel, StringComparison.Ordinal))
        {
            return "Modified: " + oldLabel;
        }

        return "Oldest: " + oldLabel + Environment.NewLine + "Newest: " + newLabel;
    }

    public IReadOnlyList<TreeNodeRow> GetAncestors(long nodeId)
    {
        List<TreeNodeRow> chain = [];
        TreeNodeRow? current = GetNode(nodeId);
        while (current?.ParentId is long parentId)
        {
            current = GetNode(parentId);
            if (current is null)
            {
                break;
            }

            chain.Add(current);
        }

        chain.Reverse();
        return chain;
    }

    public TreeNodeRow? FindByRelPath(string relPath)
    {
        ArgumentNullException.ThrowIfNull(relPath);
        NodePage page = Query(
            parentFilter: "1 = 1",
            extraFilter: "rel_path = $query",
            orderBy: "id",
            offset: 0,
            limit: 1,
            parentId: null,
            search: relPath);
        return page.Rows.Count == 0 ? null : page.Rows[0];
    }

    private NodePage Query(
        string parentFilter,
        string? extraFilter,
        string orderBy,
        int offset,
        int limit,
        long? parentId,
        string? relPrefix = null,
        DateTimeOffset? cutoffUtc = null,
        string? search = null)
    {
        string where = "session_id = $sessionId AND " + parentFilter;
        if (!string.IsNullOrWhiteSpace(extraFilter))
        {
            where += " AND " + extraFilter;
        }

        using SqliteConnection connection = sessionDb.OpenReadConnection();
        int total;
        using (SqliteCommand count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM nodes WHERE " + where;
            Bind(count, parentId, relPrefix, cutoffUtc, search);
            total = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        List<TreeNodeRow> rows = [];
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                $"""
                SELECT
                    id,
                    parent_id,
                    name,
                    rel_path,
                    kind,
                    size,
                    agg_size,
                    agg_files,
                    mtime_utc,
                    problem,
                    eff_decision,
                    EXISTS(
                        SELECT 1 FROM decisions
                        WHERE decisions.node_id = nodes.id AND decisions.source = 'User')
                        AS has_user,
                    EXISTS(
                        SELECT 1 FROM decisions
                        WHERE decisions.node_id = nodes.id AND decisions.source = 'SuggestedDefault')
                        AS has_suggested,
                    (SELECT COUNT(*) FROM nodes AS children WHERE children.parent_id = nodes.id)
                        AS child_count,
                    EXISTS (
                        SELECT 1
                        FROM nodes AS descendant
                        WHERE descendant.eff_decision <> nodes.eff_decision
                          AND descendant.id IN (
                            WITH RECURSIVE subtree(id) AS (
                                SELECT child.id FROM nodes AS child WHERE child.parent_id = nodes.id
                                UNION ALL
                                SELECT next.id
                                FROM nodes AS next
                                INNER JOIN subtree ON next.parent_id = subtree.id
                            )
                            SELECT id FROM subtree
                          )
                    ) AS is_mixed
                FROM nodes
                WHERE {where}
                ORDER BY {orderBy}
                LIMIT $limit OFFSET $offset;
                """;
            Bind(command, parentId, relPrefix, cutoffUtc, search);
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                long id = reader.GetInt64(0);
                bool mixed = reader.GetInt64(14) != 0;
                long restoreBytes = 0;
                long leaveBytes = 0;
                long undecidedBytes = 0;
                if (mixed)
                {
                    (restoreBytes, leaveBytes, undecidedBytes) = LoadMixedBytes(connection, id);
                }

                rows.Add(
                    new TreeNodeRow(
                        id,
                        reader.IsDBNull(1) ? null : reader.GetInt64(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        Enum.Parse<NodeKind>(reader.GetString(4)),
                        reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7),
                        reader.IsDBNull(8)
                            ? null
                            : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
                        Enum.Parse<NodeProblem>(reader.GetString(9)),
                        Enum.Parse<Decision>(reader.GetString(10)),
                        reader.GetInt64(11) != 0,
                        reader.GetInt64(12) != 0,
                        Convert.ToInt32(reader.GetInt64(13), CultureInfo.InvariantCulture),
                        LoadBadges(connection, id),
                        mixed,
                        restoreBytes,
                        leaveBytes,
                        undecidedBytes));
            }
        }

        return new NodePage(rows, total, total > offset + rows.Count);
    }

    private void Bind(
        SqliteCommand command,
        long? parentId,
        string? relPrefix,
        DateTimeOffset? cutoffUtc,
        string? search)
    {
        command.Parameters.AddWithValue("$sessionId", sessionId);
        if (parentId is not null)
        {
            command.Parameters.AddWithValue("$parentId", parentId.Value);
        }

        if (relPrefix is not null)
        {
            command.Parameters.AddWithValue("$relPrefix", EscapeLike(relPrefix) + @"\\%");
            command.Parameters.AddWithValue("$relExact", relPrefix);
        }

        if (cutoffUtc is not null)
        {
            command.Parameters.AddWithValue(
                "$cutoff",
                cutoffUtc.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        }

        if (search is not null)
        {
            command.Parameters.AddWithValue("$query", search);
        }
    }

    private static IReadOnlyList<string> LoadBadges(SqliteConnection connection, long nodeId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT kind, detail FROM badges WHERE node_id = $id ORDER BY kind, detail;";
        command.Parameters.AddWithValue("$id", nodeId);
        List<string> badges = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string kind = reader.GetString(0);
            string detail = reader.GetString(1);
            badges.Add(string.IsNullOrEmpty(detail) ? kind : kind + ": " + detail);
        }

        return badges;
    }

    private static (long Restore, long Leave, long Undecided) LoadMixedBytes(
        SqliteConnection connection,
        long nodeId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            WITH RECURSIVE subtree(id) AS (
                SELECT $id
                UNION ALL
                SELECT child.id
                FROM nodes AS child
                INNER JOIN subtree ON child.parent_id = subtree.id
            )
            SELECT
                COALESCE(SUM(CASE WHEN eff_decision = 'Restore' AND kind NOT IN ('Directory', 'Junction', 'Symlink', 'MountPoint') THEN size ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN eff_decision = 'LeaveBehind' AND kind NOT IN ('Directory', 'Junction', 'Symlink', 'MountPoint') THEN size ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN eff_decision = 'Undecided' AND kind NOT IN ('Directory', 'Junction', 'Symlink', 'MountPoint') THEN size ELSE 0 END), 0)
            FROM nodes
            WHERE id IN (SELECT id FROM subtree);
            """;
        command.Parameters.AddWithValue("$id", nodeId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return (0, 0, 0);
        }

        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private string RelPathFilter(long? underNodeId, out string? prefix)
    {
        prefix = null;
        if (underNodeId is null)
        {
            return "1 = 1";
        }

        TreeNodeRow? node = GetNode(underNodeId.Value);
        if (node is null)
        {
            return "0 = 1";
        }

        prefix = string.IsNullOrEmpty(node.RelPath) ? string.Empty : node.RelPath;
        if (prefix.Length == 0)
        {
            return "1 = 1";
        }

        return "(rel_path = $relExact OR rel_path LIKE $relPrefix ESCAPE '\\')";
    }

    private static string Combine(string left, string right)
    {
        if (string.IsNullOrEmpty(right) || right == "1 = 1")
        {
            return left;
        }

        return left + " AND " + right;
    }

    private static string GlobToLike(string glob)
    {
        System.Text.StringBuilder builder = new(glob.Length);
        foreach (char character in glob)
        {
            switch (character)
            {
                case '*':
                    builder.Append('%');
                    break;
                case '?':
                    builder.Append('_');
                    break;
                case '%':
                case '_':
                case '\\':
                    builder.Append('\\').Append(character);
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }

    private static string EscapeLike(string value)
    {
        return value
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
    }
}
