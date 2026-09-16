using WinOldRecovery.Core.Verify;

namespace WinOldRecovery.Core.Tests.Verify;

public sealed class VerifyReportFormatTests
{
    [Fact]
    public void Counts_Success_UsesUxFileLines()
    {
        VerifyReport report = new("r1", true, [], 118420, 118420, 4120, 4120);
        string text = VerifyReportFormat.Counts(report);
        Assert.Contains("118,420 files verified by size and timestamp   ✔", text, StringComparison.Ordinal);
        Assert.Contains(
            "4,120 files verified by SHA-256 (sample + files 64 MB or smaller, VHDX, and sensitive items)   ✔",
            text,
            StringComparison.Ordinal);
        Assert.Contains("0 failures.", text, StringComparison.Ordinal);
        Assert.Contains("You can now open the restored files", text, StringComparison.Ordinal);
        Assert.Contains("Purge is available", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Counts_Failure_ShowsPassedOfChecked()
    {
        VerifyReport report = new("r1", false, [], 10, 9, 8, 7);
        Assert.Equal(
            "9 of 10 files matched size and timestamp; 7 of 8 SHA-256 checks passed.",
            VerifyReportFormat.Counts(report));
    }
}
