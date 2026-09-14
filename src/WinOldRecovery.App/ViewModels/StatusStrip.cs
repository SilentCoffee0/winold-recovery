using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Planning;

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
            $"Selected: {selectedBytes} bytes of {freeBytes} free (need {requiredBytes} with margin)";
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
}
