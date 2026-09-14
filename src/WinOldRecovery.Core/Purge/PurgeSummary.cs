using System.Globalization;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Planning;

namespace WinOldRecovery.Core.Purge;

public static class PurgeSummary
{
    public static string Format(
        int restoreJobCount,
        bool verifySettled,
        DateTimeOffset? verifiedAtUtc,
        PreviewInventory inventory,
        long sourceBytes,
        long freeBytesOnSourceVolume,
        string sessionFolder)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionFolder);
        List<string> lines = [];
        if (restoreJobCount <= 0)
        {
            lines.Add(verifySettled
                ? "✔ No restore jobs to verify."
                : "Verify is not settled. Purge stays locked.");
        }
        else if (verifySettled)
        {
            string when = verifiedAtUtc is { } at
                ? " (" + at.UtcDateTime.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture) + ")"
                : string.Empty;
            lines.Add("✔ All " + QuantityFormat.Count(restoreJobCount) + " restore jobs verified" + when);
        }
        else
        {
            lines.Add("Verify is not settled. Purge stays locked.");
        }

        if (inventory.UndecidedFiles > 0)
        {
            lines.Add(
                "⚠ " +
                QuantityFormat.Count(inventory.UndecidedFiles) +
                " items are still Undecided (" +
                QuantityFormat.Bytes(inventory.UndecidedBytes) +
                "). They will be deleted with Windows.old.");
        }
        else
        {
            lines.Add("✔ No Undecided items remain.");
        }

        long after = freeBytesOnSourceVolume;
        if (sourceBytes > 0 && long.MaxValue - after >= sourceBytes)
        {
            after += sourceBytes;
        }

        lines.Add("✔ Free space after purge: " + QuantityFormat.Bytes(after));
        lines.Add(
            "A copy of your decisions, the restore log, and the verify report is kept at " +
            sessionFolder +
            ".");
        return string.Join(Environment.NewLine, lines);
    }
}
