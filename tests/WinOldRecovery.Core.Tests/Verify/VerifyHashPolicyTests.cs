using WinOldRecovery.Core.Hashing;
using WinOldRecovery.Core.Verify;

namespace WinOldRecovery.Core.Tests.Verify;

public sealed class VerifyHashPolicyTests
{
    [Fact]
    public void IsMandatory_CoversSmallFilesSensitiveAndStrongVerify()
    {
        Assert.True(VerifyHashPolicy.IsMandatory("a.txt", 12, sensitive: false, strongVerify: false));
        Assert.False(VerifyHashPolicy.IsMandatory("huge.bin", FileHashingPass.MaxFileBytes + 1, sensitive: false, strongVerify: false));
        Assert.True(VerifyHashPolicy.IsMandatory("huge.bin", FileHashingPass.MaxFileBytes + 1, sensitive: true, strongVerify: false));
        Assert.True(VerifyHashPolicy.IsMandatory("huge.bin", FileHashingPass.MaxFileBytes + 1, sensitive: false, strongVerify: true));
        Assert.True(VerifyHashPolicy.IsMandatory("disk.vhdx", FileHashingPass.MaxFileBytes + 1, sensitive: false, strongVerify: false));
        Assert.False(VerifyHashPolicy.IsMandatory("empty.txt", 0, sensitive: true, strongVerify: true));
    }

    [Fact]
    public void SampleSize_UsesTwoPercentWithAFloorOf200()
    {
        Assert.Equal(0, VerifyHashPolicy.SampleSize(0));
        Assert.Equal(10, VerifyHashPolicy.SampleSize(10));
        Assert.Equal(200, VerifyHashPolicy.SampleSize(300));
        Assert.Equal(200, VerifyHashPolicy.SampleSize(10_000));
        Assert.Equal(400, VerifyHashPolicy.SampleSize(20_000));
    }

    [Fact]
    public void SelectSample_IsDeterministicAndMeetsTheFloor()
    {
        string[] paths = Enumerable.Range(0, 300)
            .Select(index => @"C:\old\huge-" + index.ToString("D3") + ".bin")
            .ToArray();

        IReadOnlySet<string> first = VerifyHashPolicy.SelectSample(paths, "session-seed");
        IReadOnlySet<string> second = VerifyHashPolicy.SelectSample(paths, "session-seed");
        IReadOnlySet<string> other = VerifyHashPolicy.SelectSample(paths, "other-seed");

        Assert.Equal(200, first.Count);
        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.All(first, path => Assert.Contains(path, paths, StringComparer.OrdinalIgnoreCase));
    }
}
