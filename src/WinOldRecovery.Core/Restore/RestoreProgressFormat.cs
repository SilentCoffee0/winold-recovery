using System.Globalization;
using WinOldRecovery.Core.IO;

namespace WinOldRecovery.Core.Restore;

public static class RestoreProgressFormat
{
    public static string Line(RestoreProgress progress, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(progress);
        string eta = FormatEta(progress.CompletedBytes, progress.TotalBytes, elapsed);
        string line =
            "Restoring…  " +
            Percent(progress).ToString(CultureInfo.InvariantCulture) +
            " %   " +
            QuantityFormat.Bytes(progress.CompletedBytes) +
            " of " +
            QuantityFormat.Bytes(progress.TotalBytes);
        if (eta.Length > 0)
        {
            line += "   " + eta;
        }

        if (!string.IsNullOrEmpty(progress.CurrentName))
        {
            line += "  " + progress.CurrentName;
        }

        return line;
    }

    public static int Percent(RestoreProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (progress.TotalBytes > 0)
        {
            return (int)Math.Clamp(progress.CompletedBytes * 100L / progress.TotalBytes, 0, 100);
        }

        if (progress.TotalItems <= 0)
        {
            return 0;
        }

        return Math.Clamp(progress.CompletedItems * 100 / progress.TotalItems, 0, 100);
    }

    public static string FormatEta(long completedBytes, long totalBytes, TimeSpan elapsed)
    {
        if (completedBytes <= 0 || totalBytes <= completedBytes || elapsed.TotalSeconds < 1)
        {
            return string.Empty;
        }

        double remainingSeconds = (totalBytes - completedBytes) * elapsed.TotalSeconds / completedBytes;
        if (!double.IsFinite(remainingSeconds) || remainingSeconds <= 0)
        {
            return string.Empty;
        }

        if (remainingSeconds < 60)
        {
            int seconds = Math.Max(1, (int)Math.Round(remainingSeconds));
            return "~" + seconds.ToString(CultureInfo.InvariantCulture) + " sec left";
        }

        if (remainingSeconds < 3600)
        {
            int minutes = Math.Max(1, (int)Math.Round(remainingSeconds / 60d));
            return "~" + minutes.ToString(CultureInfo.InvariantCulture) + " min left";
        }

        int hours = Math.Max(1, (int)Math.Round(remainingSeconds / 3600d));
        return "~" + hours.ToString(CultureInfo.InvariantCulture) + " h left";
    }
}
