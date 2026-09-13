namespace WinOldRecovery.Core.Scan;

public sealed class DriveInfoVolumeRootProvider : IVolumeRootProvider
{
    public IReadOnlyList<string> GetFixedVolumeRoots()
    {
        return DriveInfo.GetDrives()
            .Where(static drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
            .Select(static drive => drive.RootDirectory.FullName)
            .ToArray();
    }
}
