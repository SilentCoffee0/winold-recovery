namespace WinOldRecovery.Core.Purge;

public interface ICleanupSage
{
    bool TryArmPreviousInstallations(int sageId);

    void Disarm(int sageId);
}

public sealed class RegistryCleanupSage : ICleanupSage
{
    public const string PreviousInstallationsKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\Previous Installations";

    public bool TryArmPreviousInstallations(int sageId)
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                PreviousInstallationsKey,
                writable: true);
            if (key is null)
            {
                return false;
            }

            key.SetValue(StateFlagsName(sageId), 2, Microsoft.Win32.RegistryValueKind.DWord);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public void Disarm(int sageId)
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                PreviousInstallationsKey,
                writable: true);
            key?.DeleteValue(StateFlagsName(sageId), throwOnMissingValue: false);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
        }
    }

    internal static string StateFlagsName(int sageId)
    {
        return "StateFlags" + sageId.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
    }
}
