using WinOldRecovery.Core.IO;
using WinOldRecovery.Native;

namespace WinOldRecovery.Core.Tests.IO;

public sealed class OwnerSidDisplayTests
{
    [Fact]
    public void LineFromSid_MarksUnmappedSidAsOldAccount()
    {
        string line = OwnerSidDisplay.LineFromSid("S-1-5-21-1-2-3-1001");
        Assert.Equal("Owner: S-1-5-21-1-2-3-1001 (old account, no longer exists)", line);
    }

    [Fact]
    public void LineFromSid_ShowsTranslatedWellKnownAccount()
    {
        string line = OwnerSidDisplay.LineFromSid("S-1-5-18");
        Assert.StartsWith("Owner: ", line, StringComparison.Ordinal);
        Assert.Contains("S-1-5-18", line, StringComparison.Ordinal);
        Assert.DoesNotContain("no longer exists", line, StringComparison.Ordinal);
        Assert.Contains("SYSTEM", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Line_ReadsOwnerOfALocalFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "winold-owner-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "x");
        try
        {
            string line = OwnerSidDisplay.Line(path);
            Assert.StartsWith("Owner: ", line, StringComparison.Ordinal);
            string ownerSid = FileSecurityInfo.GetOwnerSid(path);
            Assert.Contains(ownerSid, line, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Line_MissingPath_IsUnavailable()
    {
        Assert.Equal("Owner: unavailable", OwnerSidDisplay.Line(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void AttributesLine_IncludesArchiveOrDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "winold-attr-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "x");
        try
        {
            string line = OwnerSidDisplay.AttributesLine(path);
            Assert.StartsWith("Attributes: ", line, StringComparison.Ordinal);
            Assert.Contains("Archive", line, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
