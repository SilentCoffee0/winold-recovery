using WinOldRecovery.Core.Processes;

namespace WinOldRecovery.Core.Tests.Processes;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_KillsAChildThatExceedsTimeout()
    {
        ProcessRunner runner = new();
        TimeoutException thrown = await Assert.ThrowsAsync<TimeoutException>(
            () => runner.RunAsync(
                new ProcessRequest(
                    "ping.exe",
                    ["127.0.0.1", "-n", "30"],
                    Timeout: TimeSpan.FromSeconds(1))));
        Assert.Contains("ping.exe", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ReturnsStdoutFromAShortCommand()
    {
        ProcessResult result = await new ProcessRunner().RunAsync(
            new ProcessRequest("cmd.exe", ["/c", "echo", "fixture-ok"], Timeout: TimeSpan.FromSeconds(15)));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("fixture-ok", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }
}
