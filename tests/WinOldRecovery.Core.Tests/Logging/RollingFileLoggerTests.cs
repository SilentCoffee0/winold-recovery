using Microsoft.Extensions.Logging;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Logging;
using WinOldRecovery.Core.Safety;

namespace WinOldRecovery.Core.Tests.Logging;

public sealed class RollingFileLoggerTests
{
    [Fact]
    public void I8_CanarySecretsAreRedactedFromMessagesAndExceptions()
    {
        string root = CreateTestRoot();
        string logPath = Path.Combine(root, "log.txt");
        const string canary = "SECRET_CANARY_7F3A91";

        try
        {
            SensitiveDataRedactor redactor = new();
            redactor.RegisterSecretLiteral(canary);
            using RollingFileLoggerProvider provider = new(
                logPath,
                new SafeFs(new SourceGuard()),
                redactor);
            ILogger logger = provider.CreateLogger("CanaryTest");

            logger.LogError(
                new InvalidOperationException($"Exception contained {canary}"),
                "Message contained {Canary}",
                canary);
            provider.Dispose();

            string contents = File.ReadAllText(logPath);
            Assert.DoesNotContain(canary, contents);
            Assert.Contains("<redacted>", contents);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SensitivePathsAreReplacedWithNodeReferences()
    {
        string root = CreateTestRoot();
        string logPath = Path.Combine(root, "log.txt");
        string sensitivePath = Path.Combine(
            root,
            "Windows.old",
            "Users",
            "Alice",
            ".ssh",
            "id_ed25519");

        try
        {
            SensitiveDataRedactor redactor = new();
            redactor.RegisterSensitivePath(42, sensitivePath);
            using RollingFileLoggerProvider provider = new(
                logPath,
                new SafeFs(new SourceGuard()),
                redactor);

            provider.CreateLogger("PathTest")
                .LogWarning("Could not inspect {Path}", sensitivePath);
            provider.Dispose();

            string contents = File.ReadAllText(logPath);
            Assert.DoesNotContain(sensitivePath, contents, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("<sensitive:42>", contents);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Provider_RotatesWithoutLosingRedaction()
    {
        string root = CreateTestRoot();
        string logPath = Path.Combine(root, "log.txt");
        const string canary = "ROTATION_SECRET_91A7";

        try
        {
            SensitiveDataRedactor redactor = new();
            redactor.RegisterSecretLiteral(canary);
            using RollingFileLoggerProvider provider = new(
                logPath,
                new SafeFs(new SourceGuard()),
                redactor,
                maximumBytes: 256,
                retainedFileCount: 2,
                clock: () => new DateTimeOffset(
                    2026,
                    9,
                    13,
                    4,
                    0,
                    0,
                    TimeSpan.Zero));
            ILogger logger = provider.CreateLogger("RotationTest");

            for (int index = 0; index < 20; index++)
            {
                logger.LogInformation(
                    "Entry {Index}: {Canary} with enough padding to rotate the file.",
                    index,
                    canary);
            }

            provider.Dispose();

            string[] logs = Directory.GetFiles(root, "log.txt*");
            Assert.True(logs.Length is >= 2 and <= 3);
            Assert.All(
                logs,
                path => Assert.DoesNotContain(
                    canary,
                    File.ReadAllText(path),
                    StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void I1_LogCannotBeCreatedInsideRegisteredSource()
    {
        string root = CreateTestRoot();
        string source = Path.Combine(root, "Windows.old");
        Directory.CreateDirectory(source);
        SourceGuard guard = new();
        guard.RegisterSourceRoot(source);

        try
        {
            Assert.Throws<SourceWriteDeniedException>(
                () => new RollingFileLoggerProvider(
                    Path.Combine(source, "log.txt"),
                    new SafeFs(guard),
                    new SensitiveDataRedactor()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTestRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"WinOldRecovery-Logging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
