namespace WinOldRecovery.Core.Sessions;

public static class SessionLock
{
    public const string PurgedKvKey = "session.purged";
    public const string PurgedValue = "1";

    public static bool IsPurged(string? stored)
    {
        return string.Equals(stored, PurgedValue, StringComparison.Ordinal);
    }
}
