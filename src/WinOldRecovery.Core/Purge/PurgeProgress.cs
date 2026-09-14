using WinOldRecovery.Core.IO;

namespace WinOldRecovery.Core.Purge;

public sealed record PurgeProgress(int DeletedLeaves, string CurrentPath);

public static class PurgeProgressFormat
{
    public const string CancelledDetail =
        "Purge cancelled. Windows.old is only partly deleted. Remaining items stay on disk.";

    public static string Line(PurgeProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        string line = "Deleting…  " + QuantityFormat.Count(progress.DeletedLeaves) + " items";
        if (!string.IsNullOrEmpty(progress.CurrentPath))
        {
            line += "  " + PathDisplay.MiddleEllipsis(progress.CurrentPath);
        }

        return line;
    }
}
