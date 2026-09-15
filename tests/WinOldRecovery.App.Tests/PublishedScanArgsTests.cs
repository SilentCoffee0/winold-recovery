using System.IO;

namespace WinOldRecovery.App.Tests;

public sealed class PublishedScanArgsTests
{
    [Fact]
    public void TryParse_ReadsScanAndReportPaths()
    {
        string source = Path.Combine(Path.GetTempPath(), "old");
        string report = Path.Combine(Path.GetTempPath(), "report.txt");
        Directory.CreateDirectory(source);
        Assert.True(
            PublishedScanArgs.TryParse(["--scan", source, "--report", report], out string parsedSource, out string parsedReport));
        Assert.Equal(Path.GetFullPath(source), parsedSource);
        Assert.Equal(Path.GetFullPath(report), parsedReport);
    }

    [Fact]
    public void TryParse_RejectsMissingReport()
    {
        Assert.False(PublishedScanArgs.TryParse(["--scan", "C:\\old"], out _, out _));
    }
}
