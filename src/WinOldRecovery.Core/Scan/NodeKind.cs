namespace WinOldRecovery.Core.Scan;

public enum NodeKind
{
    File,
    Directory,
    Junction,
    Symlink,
    MountPoint,
    CloudPlaceholder,
    Unknown,
}
