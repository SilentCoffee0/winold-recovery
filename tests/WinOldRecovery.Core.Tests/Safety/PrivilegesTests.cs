using System.ComponentModel;
using WinOldRecovery.Native;

namespace WinOldRecovery.Core.Tests.Safety;

public sealed class PrivilegesTests
{
    [Fact]
    public void EnableBackupAndRestore_SucceedsOrReportsMissingTokenPrivileges()
    {
        try
        {
            Privileges.EnableBackupAndRestore();
        }
        catch (Win32Exception exception)
        {
            Assert.Equal(1300, exception.NativeErrorCode);
        }
    }
}
