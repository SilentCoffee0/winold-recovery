using System.IO;

namespace WinOldRecovery.App.Tests;

public sealed class PublishedRestoreArgsTests
{
    [Fact]
    public void TryParse_ReadsRestoreDestinationAndReport()
    {
        string source = Path.Combine(Path.GetTempPath(), "old");
        string dest = Path.Combine(Path.GetTempPath(), "new");
        string report = Path.Combine(Path.GetTempPath(), "restore-report.txt");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(dest);
        Assert.True(
            PublishedRestoreArgs.TryParse(
                ["--restore", source, dest, "--report", report],
                out string parsedSource,
                out string parsedDest,
                out string parsedReport));
        Assert.Equal(Path.GetFullPath(source), parsedSource);
        Assert.Equal(Path.GetFullPath(dest), parsedDest);
        Assert.Equal(Path.GetFullPath(report), parsedReport);
    }

    [Fact]
    public void TryParse_RejectsMissingDestination()
    {
        Assert.False(
            PublishedRestoreArgs.TryParse(["--restore", "C:\\old", "--report", "C:\\r.txt"], out _, out _, out _));
    }
}
