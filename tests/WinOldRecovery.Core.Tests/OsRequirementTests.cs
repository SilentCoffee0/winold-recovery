using WinOldRecovery.Core;

namespace WinOldRecovery.Core.Tests;

public sealed class OsRequirementTests
{
    [Fact]
    public void IsSupported_AllowsWindows10_1809AndWindows11On64Bit()
    {
        Assert.True(OsRequirement.IsSupported(new Version(10, 0, 17763), is64BitProcess: true));
        Assert.True(OsRequirement.IsSupported(new Version(10, 0, 17763, 1), is64BitProcess: true));
        Assert.True(OsRequirement.IsSupported(new Version(10, 0, 19045), is64BitProcess: true));
        Assert.True(OsRequirement.IsSupported(new Version(10, 0, 22621), is64BitProcess: true));
        Assert.True(OsRequirement.IsSupported(new Version(10, 0, 26200), is64BitProcess: true));
    }

    [Fact]
    public void IsSupported_RefusesWindows81EarlierThan1809And32Bit()
    {
        Assert.False(OsRequirement.IsSupported(new Version(6, 3, 9600), is64BitProcess: true));
        Assert.False(OsRequirement.IsSupported(new Version(6, 1, 7601), is64BitProcess: true));
        Assert.False(OsRequirement.IsSupported(new Version(10, 0, 17134), is64BitProcess: true));
        Assert.False(OsRequirement.IsSupported(new Version(10, 0, 17762), is64BitProcess: true));
        Assert.False(OsRequirement.IsSupported(new Version(10, 0, 19045), is64BitProcess: false));
        Assert.False(OsRequirement.IsSupported(new Version(10, 0, 22621), is64BitProcess: false));
    }

    [Fact]
    public void CurrentHost_MeetsThePublishedMinimum()
    {
        Assert.True(OsRequirement.IsCurrentSupported());
        Assert.Contains("1809", OsRequirement.RefusalMessage, StringComparison.Ordinal);
        Assert.Contains("64-bit", OsRequirement.RefusalMessage, StringComparison.Ordinal);
        Assert.Contains("17763", OsRequirement.RefusalMessage, StringComparison.Ordinal);
    }
}
