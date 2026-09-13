using WinOldRecovery.Core.Logging;

namespace WinOldRecovery.Core.Tests.Logging;

public sealed class ExceptionReportTests
{
    [Fact]
    public void UserMessage_RedactsCanariesAndPointsAtTheSessionLog()
    {
        SensitiveDataRedactor redactor = new();
        const string canary = "WINOLD_RECOVERY_CANARY_DO_NOT_LOG_7F3A91";
        redactor.RegisterSecretLiteral(canary);
        string message = ExceptionReport.FormatUserMessage(
            new InvalidOperationException("boom " + canary),
            @"C:\Users\Public\WinOldRecovery\sessions\demo\log.txt",
            redactor);

        Assert.DoesNotContain(canary, message, StringComparison.Ordinal);
        Assert.Contains("<redacted>", message, StringComparison.Ordinal);
        Assert.Contains("log.txt", message, StringComparison.Ordinal);
        Assert.Contains("Windows.old was not modified", message, StringComparison.Ordinal);
        Assert.Contains("support bundle", message, StringComparison.OrdinalIgnoreCase);
    }
}
