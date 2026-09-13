namespace WinOldRecovery.Core.Scan;

public interface IVolumeRootProvider
{
    IReadOnlyList<string> GetFixedVolumeRoots();
}
