namespace WinOldRecovery.Core.Verify;

public static class VerifyAcknowledgement
{
    public const int MinimumReasonLength = 8;
    public const string KeyPrefix = "verify.ack.";

    public static string KvKey(string reportId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportId);
        return KeyPrefix + reportId;
    }

    public static bool IsValidReason(string? reason)
    {
        return !string.IsNullOrWhiteSpace(reason) &&
            reason.Trim().Length >= MinimumReasonLength;
    }
}
