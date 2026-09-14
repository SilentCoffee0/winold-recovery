using WinOldRecovery.Core.IO;

namespace WinOldRecovery.Core.Tests.IO;

public sealed class QuantityFormatTests
{
    [Fact]
    public void Count_UsesThousandsSeparators()
    {
        Assert.Equal("1,234", QuantityFormat.Count(1234));
    }

    [Fact]
    public void Bytes_UsesUnits()
    {
        Assert.Equal("1.0 GB", QuantityFormat.Bytes(1024L * 1024L * 1024L));
        Assert.Equal("12 bytes", QuantityFormat.Bytes(12));
    }
}
