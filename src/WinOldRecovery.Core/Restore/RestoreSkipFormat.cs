using WinOldRecovery.Core.IO;

namespace WinOldRecovery.Core.Restore;

public static class RestoreSkipFormat
{
    public static string Summary(RestoreSkipCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        if (counts.Total <= 0)
        {
            return string.Empty;
        }

        return "Warnings (" + QuantityFormat.Count(counts.Total) + ")";
    }

    public static string Detail(RestoreSkipCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        List<string> lines = [];
        if (counts.Reparse > 0)
        {
            lines.Add(QuantityFormat.Count(counts.Reparse) + " junctions or symlinks skipped (not followed)");
        }

        if (counts.Offline > 0)
        {
            lines.Add(QuantityFormat.Count(counts.Offline) + " cloud placeholders skipped");
        }

        if (counts.Encrypted > 0)
        {
            lines.Add(QuantityFormat.Count(counts.Encrypted) + " EFS-encrypted files skipped");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
