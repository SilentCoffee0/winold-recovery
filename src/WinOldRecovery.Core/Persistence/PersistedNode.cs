using WinOldRecovery.Core.Scan;

namespace WinOldRecovery.Core.Persistence;

public sealed record PersistedNode(
    long Id,
    string SessionId,
    long? ProfileId,
    long? ParentId,
    string Name,
    string RelPath,
    NodeKind Kind,
    long Size,
    long AggSize,
    long AggFiles,
    DateTime? LastWriteTimeUtc,
    int Attributes,
    NodeProblem Problem);

public sealed record NodeAggregateUpdate(
    long Id,
    long AggSize,
    long AggFiles,
    NodeProblem Problem);

public sealed record NodeBadgeRow(long NodeId, string Kind, string Detail);

public sealed record BadgeKindTotal(string Kind, string Detail, int Count, long Bytes);

public sealed record WalkerCheckpoint(
    long RootNodeId,
    long NextNodeId,
    List<string> CompletedTopLevelDirectories);

public readonly record struct ChildAggregate(long AggSize, long AggFiles);

public sealed record ClassificationNodeRow(
    long Id,
    long? ParentId,
    string Name,
    string RelPath,
    NodeKind Kind,
    long Size,
    long AggSize,
    NodeProblem Problem,
    bool Sensitive);
