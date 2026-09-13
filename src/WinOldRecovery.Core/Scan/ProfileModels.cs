namespace WinOldRecovery.Core.Scan;

public enum ProfileKind
{
    Human,
    Service,
    PublicShared,
}

public sealed record DetectedProfile(
    string Name,
    string DisplayName,
    string SourcePath,
    string RelativePath,
    ProfileKind Kind,
    DateTimeOffset? LastUsedUtc,
    IReadOnlyList<StandardFolderMatch> StandardFolders,
    IReadOnlyList<string> CustomFolders,
    IReadOnlyList<LegacyJunctionMatch> LegacyJunctions);

public sealed record StandardFolderMatch(
    string KnownName,
    string? RelativePathInSource,
    string? RedirectedAbsolutePath,
    bool PresentInSource);

public sealed record LegacyJunctionMatch(string Name, string? Target);

public sealed record ProfileRecord(
    string SessionId,
    string Name,
    string SourcePath,
    ProfileKind Kind,
    DateTimeOffset? LastUsedUtc,
    long? Id = null);
