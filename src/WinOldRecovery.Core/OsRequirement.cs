namespace WinOldRecovery.Core;

public static class OsRequirement
{
    public const int MinimumBuild = 17763;

    public static readonly Version MinimumWindowsVersion = new(10, 0, MinimumBuild);

    public const string RefusalMessage =
        "WinOld Recovery requires 64-bit Windows 10 version 1809 (build 17763) or later, or Windows 11.";

    public static bool IsCurrentSupported()
    {
        return OperatingSystem.IsWindows() &&
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinimumBuild) &&
            Environment.Is64BitProcess;
    }

    public static bool IsSupported(Version windowsVersion, bool is64BitProcess)
    {
        ArgumentNullException.ThrowIfNull(windowsVersion);
        if (!is64BitProcess)
        {
            return false;
        }

        if (windowsVersion.Major > 10)
        {
            return true;
        }

        return windowsVersion.Major == 10 && windowsVersion.Build >= MinimumBuild;
    }
}
