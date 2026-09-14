using WinOldRecovery.Core.IO;

namespace WinOldRecovery.Core.Planning;

public sealed record PreviewInventory(
    int CloudOnly,
    int Encrypted,
    int AccessDenied,
    int UndecidedFiles,
    long UndecidedBytes);

public static class PreviewSummary
{
    public static string Format(
        RestorePlan plan,
        PreflightResult preflight,
        PreviewInventory inventory,
        IReadOnlyList<string> recipeLines,
        IReadOnlyList<string> runningApps)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(recipeLines);
        ArgumentNullException.ThrowIfNull(runningApps);

        List<string> lines =
        [
            $"Items to restore: {QuantityFormat.Count(plan.Items.Count)} copy operations, {QuantityFormat.Bytes(plan.TotalBytes)}.",
            $"Free space on destination: {QuantityFormat.Bytes(preflight.FreeBytes)} (need {QuantityFormat.Bytes(preflight.RequiredBytes)} with margin)" +
                (preflight.CanProceed ? "  ✔" : "  Will not fit."),
            "Conflicts: " + QuantityFormat.Count(preflight.Conflicts.Count) + " files already exist with different content.",
        ];

        if (recipeLines.Count > 0)
        {
            lines.Add("App recipes:");
            lines.AddRange(recipeLines.Select(static line => "  " + line));
        }

        lines.Add("Cannot be restored (shown for transparency):");
        lines.Add("  " + QuantityFormat.Count(inventory.CloudOnly) + " cloud-only placeholders (content lives in the cloud account)");
        lines.Add("  " + QuantityFormat.Count(inventory.Encrypted) + " EFS-encrypted files");
        lines.Add("  " + QuantityFormat.Count(inventory.AccessDenied) + " folders or files with access denied");
        lines.Add(
            "Undecided items remaining: " +
            QuantityFormat.Count(inventory.UndecidedFiles) +
            " (" +
            QuantityFormat.Bytes(inventory.UndecidedBytes) +
            ") — they will stay in Windows.old until you purge it.");

        if (runningApps.Count > 0)
        {
            lines.Add("Close these apps before Start restore:");
            lines.AddRange(runningApps.Select(static line => "  " + line));
        }

        lines.Add("Preview is a read-only dry run. Windows.old has not been changed.");
        return string.Join(Environment.NewLine, lines);
    }
}
