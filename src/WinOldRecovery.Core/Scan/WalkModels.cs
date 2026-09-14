namespace WinOldRecovery.Core.Scan;

public sealed record WalkRequest(
    string SessionId,
    string SourceRoot,
    IReadOnlyList<WalkScope>? Scopes = null,
    bool Resume = false);

public sealed record WalkScope(string RelativeRoot, long? ProfileId = null);

public sealed record WalkProgress(
    int NodesVisited,
    long BytesSeen,
    string CurrentRelativePath,
    IReadOnlyList<string> CompletedTopLevelDirectories,
    int JunctionsSkipped = 0,
    int CloudSkipped = 0,
    int EncryptedSkipped = 0,
    int AccessDenied = 0);

public sealed record WalkResult(
    int NodesVisited,
    long BytesSeen,
    IReadOnlyList<string> CompletedTopLevelDirectories,
    bool Completed,
    int JunctionsSkipped = 0,
    int CloudSkipped = 0,
    int EncryptedSkipped = 0,
    int AccessDenied = 0);
