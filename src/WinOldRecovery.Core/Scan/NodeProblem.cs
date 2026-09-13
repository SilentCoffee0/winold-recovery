namespace WinOldRecovery.Core.Scan;

public enum NodeProblem
{
    None,
    AccessDenied,
    EfsEncrypted,
    CloudOnly,
    LongPath,
    InvalidDestName,
    ZeroByteStub,
}
