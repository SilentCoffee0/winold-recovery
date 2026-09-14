using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Planning;

namespace WinOldRecovery.Core.Restore;

public static class DiskFullPause
{
    public static string Format(string destinationRoot, long remainingBytes, long freeBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        string volume = Path.GetPathRoot(destinationRoot) ?? destinationRoot;
        volume = volume.TrimEnd('\\');
        long required = remainingBytes + (remainingBytes / 20) + PreflightChecker.AbsoluteMarginBytes;
        long need = Math.Max(0, required - Math.Max(0, freeBytes));
        return volume + " is full. Free " + QuantityFormat.Bytes(need) + " and click Resume, or Cancel.";
    }
}
