using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Scan;

namespace WinOldRecovery.Core.Planning;

public sealed class PlanBuilder
{
    private static readonly HashSet<string> WholeAppDataNames =
        new(StringComparer.OrdinalIgnoreCase) { "Local", "Roaming", "LocalLow" };

    private readonly SessionDb sessionDb;

    public PlanBuilder(SessionDb sessionDb)
    {
        this.sessionDb = sessionDb ?? throw new ArgumentNullException(nameof(sessionDb));
    }

    public async Task<RestorePlan> BuildAsync(
        PlanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationRoot);

        string sourceRoot = PathCanonicalizer.Canonicalize(request.SourceRoot);
        string destinationRoot = PathCanonicalizer.Canonicalize(request.DestinationRoot);
        RejectContainment(sourceRoot, destinationRoot);

        IReadOnlyList<PlanNode> nodes = LoadNodes(request.SessionId);
        Dictionary<long, List<PlanNode>> children = nodes
            .Where(static node => node.ParentId is not null)
            .GroupBy(static node => node.ParentId!.Value)
            .ToDictionary(static group => group.Key, static group => group.ToList());
        HashSet<long> covered = [];
        List<PlanItem> items = [];

        foreach (PlanNode node in nodes.Where(static item => item.ParentId is null))
        {
            Walk(node);
        }

        RestorePlan plan = new(
            request.SessionId,
            sourceRoot,
            destinationRoot,
            items,
            items.Sum(static item => item.Bytes));
        IReadOnlyList<PlanItem> stored = await sessionDb
            .ReplacePlanItemsAsync(request.SessionId, plan.Items, cancellationToken)
            .ConfigureAwait(false);
        return plan with { Items = stored };

        void Walk(PlanNode node)
        {
            if (covered.Contains(node.Id) ||
                string.IsNullOrEmpty(node.RelPath) ||
                node.EffectiveDecision != Decision.Restore)
            {
                ExpandChildren(node);
                return;
            }

            if (!CanRestore(node))
            {
                ExpandChildren(node);
                return;
            }

            if (node.Kind == NodeKind.Directory)
            {
                if (IsForbiddenAppDataFolder(node.RelPath))
                {
                    ExpandChildren(node);
                    return;
                }

                if (SubtreeUniformRestore(node))
                {
                    Emit(node, PlanOperation.CopyTree, node.AggSize);
                    MarkCovered(node);
                    return;
                }

                ExpandChildren(node);
                return;
            }

            Emit(node, PlanOperation.CopyFile, node.Size);
        }

        void ExpandChildren(PlanNode node)
        {
            if (!children.TryGetValue(node.Id, out List<PlanNode>? kids))
            {
                return;
            }

            foreach (PlanNode child in kids)
            {
                Walk(child);
            }
        }

        bool SubtreeUniformRestore(PlanNode node)
        {
            if (!children.TryGetValue(node.Id, out List<PlanNode>? kids))
            {
                return true;
            }

            foreach (PlanNode child in kids)
            {
                if (!CanRestore(child))
                {
                    continue;
                }

                if (child.EffectiveDecision != Decision.Restore)
                {
                    return false;
                }

                if (child.Kind == NodeKind.Directory && IsForbiddenAppDataFolder(child.RelPath))
                {
                    return false;
                }

                if (child.Kind == NodeKind.Directory && !SubtreeUniformRestore(child))
                {
                    return false;
                }
            }

            return true;
        }

        void MarkCovered(PlanNode node)
        {
            covered.Add(node.Id);
            if (!children.TryGetValue(node.Id, out List<PlanNode>? kids))
            {
                return;
            }

            foreach (PlanNode child in kids)
            {
                MarkCovered(child);
            }
        }

        void Emit(PlanNode node, PlanOperation operation, long bytes)
        {
            string sourcePath = string.IsNullOrEmpty(node.RelPath)
                ? sourceRoot
                : Path.Combine(sourceRoot, node.RelPath);
            string destinationPath = string.IsNullOrEmpty(node.RelPath)
                ? destinationRoot
                : Path.Combine(destinationRoot, node.RelPath);
            items.Add(
                new PlanItem(
                    request.SessionId,
                    request.JobId,
                    operation,
                    sourcePath,
                    destinationPath,
                    bytes,
                    request.ConflictPolicy == ConflictPolicy.OverwriteApproved
                        ? ConflictPolicy.KeepBoth
                        : request.ConflictPolicy,
                    OverwriteApproved: false,
                    RecipeId: null));
        }
    }

    public static bool IsForbiddenAppDataFolder(string relPath)
    {
        string[] parts = relPath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (3 or 4) ||
            !parts[0].Equals("Users", StringComparison.OrdinalIgnoreCase) ||
            !parts[2].Equals("AppData", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return parts.Length == 3 || WholeAppDataNames.Contains(parts[3]);
    }

    private static void RejectContainment(string sourceRoot, string destinationRoot)
    {
        if (IsInside(destinationRoot, sourceRoot) || IsInside(sourceRoot, destinationRoot))
        {
            throw new InvalidOperationException(
                "The destination cannot sit inside Windows.old, and Windows.old cannot sit inside the destination.");
        }
    }

    private static bool IsInside(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = root.EndsWith('\\') ? root : root + "\\";
        string normalized = path.EndsWith('\\') ? path : path + "\\";
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanRestore(PlanNode node)
    {
        if (node.Kind is NodeKind.Junction or NodeKind.Symlink or NodeKind.MountPoint
            or NodeKind.CloudPlaceholder)
        {
            return false;
        }

        return node.Problem is NodeProblem.None or NodeProblem.LongPath;
    }

    private IReadOnlyList<PlanNode> LoadNodes(string sessionId)
    {
        using SqliteConnection connection = sessionDb.OpenReadConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, parent_id, rel_path, kind, size, agg_size, problem, eff_decision
            FROM nodes
            WHERE session_id = $sessionId
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        List<PlanNode> nodes = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            nodes.Add(
                new PlanNode(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    reader.GetString(2),
                    Enum.Parse<NodeKind>(reader.GetString(3)),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    Enum.Parse<NodeProblem>(reader.GetString(6)),
                    Enum.Parse<Decision>(reader.GetString(7))));
        }

        return nodes;
    }

    private sealed record PlanNode(
        long Id,
        long? ParentId,
        string RelPath,
        NodeKind Kind,
        long Size,
        long AggSize,
        NodeProblem Problem,
        Decision EffectiveDecision);
}
