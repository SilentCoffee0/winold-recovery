namespace WinOldRecovery.Core.Restore;

public sealed class RestoreSkipCounts
{
    public int Reparse { get; private set; }

    public int Offline { get; private set; }

    public int Encrypted { get; private set; }

    public int Total => Reparse + Offline + Encrypted;

    public void Add(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            Reparse++;
            return;
        }

        if ((attributes & FileAttributes.Encrypted) != 0)
        {
            Encrypted++;
            return;
        }

        if ((attributes & FileAttributes.Offline) != 0)
        {
            Offline++;
        }
    }

    public void Add(RestoreSkipCounts? other)
    {
        if (other is null)
        {
            return;
        }

        Reparse += other.Reparse;
        Offline += other.Offline;
        Encrypted += other.Encrypted;
    }

    public static RestoreSkipCounts FromResults(IEnumerable<RestoreItemResult> results)
    {
        RestoreSkipCounts total = new();
        foreach (RestoreItemResult result in results)
        {
            total.Add(result.Skips);
        }

        return total;
    }
}
