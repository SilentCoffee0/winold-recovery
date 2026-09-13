using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Logging;
using WinOldRecovery.Core.Safety;

namespace WinOldRecovery.Core.Tests.Logging;

public sealed class SessionLogReaderTests
{
    [Fact]
    public void Read_RedactsCanariesFromAnExistingLog()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinOldRecovery-Log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SafeFs safeFs = new(new SourceGuard());
            string logPath = Path.Combine(root, "log.txt");
            const string canary = "WINOLD_RECOVERY_CANARY_DO_NOT_LOG_7F3A91";
            File.WriteAllText(logPath, "line " + canary + " end");
            SensitiveDataRedactor redactor = new();
            redactor.RegisterSecretLiteral(canary);

            string text = SessionLogReader.Read(safeFs, logPath, redactor);
            Assert.DoesNotContain(canary, text, StringComparison.Ordinal);
            Assert.Contains("<redacted>", text, StringComparison.Ordinal);
            Assert.Contains("line", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_EmptyWhenLogIsMissing()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinOldRecovery-Log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SafeFs safeFs = new(new SourceGuard());
            string text = SessionLogReader.Read(
                safeFs,
                Path.Combine(root, "missing.txt"),
                new SensitiveDataRedactor());
            Assert.Contains("empty", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
