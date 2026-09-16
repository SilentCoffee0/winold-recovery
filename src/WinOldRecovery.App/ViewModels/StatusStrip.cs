using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Planning;
using WinOldRecovery.Core.Scan;

namespace WinOldRecovery.App.ViewModels;

public enum SpaceBudgetLevel
{
    Idle,
    Ok,
    Approaching,
    WillNotFit,
}

public enum SourceIntegrityLevel
{
    Untouched,
    Deleting,
    Removed,
    Partial,
}

public static class StatusStrip
{
    public static SourceIntegrityLevel IntegrityLevel(string text)
    {
        if (string.Equals(text, "Deleting…", StringComparison.Ordinal))
        {
            return SourceIntegrityLevel.Deleting;
        }

        if (string.Equals(text, "Windows.old removed", StringComparison.Ordinal))
        {
            return SourceIntegrityLevel.Removed;
        }

        if (string.Equals(text, "Windows.old partly deleted", StringComparison.Ordinal))
        {
            return SourceIntegrityLevel.Partial;
        }

        return SourceIntegrityLevel.Untouched;
    }

    public static (string Text, SpaceBudgetLevel Level) FormatSpaceBudget(
        long selectedBytes,
        long requiredBytes,
        long freeBytes)
    {
        string core =
            $"Selected: {QuantityFormat.Bytes(selectedBytes)} of {QuantityFormat.Bytes(freeBytes)} free (need {QuantityFormat.Bytes(requiredBytes)} with margin)";
        if (requiredBytes > freeBytes)
        {
            return (core + " Will not fit.", SpaceBudgetLevel.WillNotFit);
        }

        long amberAt = (long)(0.9 * Math.Max(0, freeBytes - PreflightChecker.AbsoluteMarginBytes));
        if (selectedBytes >= amberAt)
        {
            return (core + " Approaching the free-space limit.", SpaceBudgetLevel.Approaching);
        }

        return (core, SpaceBudgetLevel.Ok);
    }

    public static string FormatScanSummary(
        int nodesVisited,
        long bytesSeen,
        int profileCount,
        int knownApps,
        int highValueItems,
        int? hashedFiles = null)
    {
        string text =
            $"Windows.old holds {QuantityFormat.Count(nodesVisited)} entries ({QuantityFormat.Bytes(bytesSeen)}) across {profileCount} profiles.";
        if (hashedFiles is int hashed)
        {
            text += " Hashed " + hashed + " files under 64 MB.";
        }

        return text +
            $" We found {knownApps} known apps and {highValueItems} high-value items. Nothing has been changed.";
    }

    public static string FormatScanProgress(WalkProgress report, IReadOnlyList<string> profileNames)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(profileNames);
        IReadOnlyList<string> names = report.ProfileNames is { Count: > 0 }
            ? report.ProfileNames
            : profileNames;
        string profiles = names.Count == 0
            ? "Profiles found: …"
            : "Profiles found: " + string.Join(", ", names);
        return profiles +
            $"  Files: {QuantityFormat.Count(report.FilesSeen)}   Folders: {QuantityFormat.Count(report.FoldersSeen)}   Size so far: {QuantityFormat.Bytes(report.BytesSeen)}" +
            Environment.NewLine +
            $"Skipped: {QuantityFormat.Count(report.JunctionsSkipped)} junctions, {QuantityFormat.Count(report.CloudSkipped)} cloud placeholders, {QuantityFormat.Count(report.EncryptedSkipped)} encrypted, {QuantityFormat.Count(report.AccessDenied)} access denied.";
    }
}
