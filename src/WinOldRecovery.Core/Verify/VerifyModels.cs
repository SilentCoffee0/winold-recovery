namespace WinOldRecovery.Core.Verify;

public sealed record VerifyResultRow(
    long PlanItemId,
    string ReportId,
    int Level,
    bool Ok,
    string Detail,
    DateTimeOffset RecordedAtUtc);

public sealed record VerifyReport(
    string ReportId,
    bool AllOk,
    IReadOnlyList<VerifyResultRow> Rows);
