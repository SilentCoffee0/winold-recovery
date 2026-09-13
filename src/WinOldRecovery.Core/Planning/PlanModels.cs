namespace WinOldRecovery.Core.Planning;

public enum PlanOperation
{
    CopyFile,
    CopyTree,
}

public enum ConflictPolicy
{
    KeepBoth,
    Skip,
    OverwriteApproved,
}

public sealed record PlanItem(
    string SessionId,
    int JobId,
    PlanOperation Operation,
    string SourcePath,
    string DestinationPath,
    long Bytes,
    ConflictPolicy ConflictPolicy,
    bool OverwriteApproved,
    string? RecipeId,
    long? Id = null);

public sealed record RestorePlan(
    string SessionId,
    string SourceRoot,
    string DestinationRoot,
    IReadOnlyList<PlanItem> Items,
    long TotalBytes);

public sealed record PlanRequest(
    string SessionId,
    string SourceRoot,
    string DestinationRoot,
    int JobId = 1,
    ConflictPolicy ConflictPolicy = ConflictPolicy.KeepBoth);
