namespace WinOldRecovery.Core.Persistence;

public sealed record SessionRecord(
    string Id,
    DateTimeOffset StartedAt,
    string Status,
    string AppVersion,
    string? SourceRoot = null,
    string OptionsJson = "{}");
