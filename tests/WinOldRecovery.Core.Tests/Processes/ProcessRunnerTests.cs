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
    public async Task RunAsync_NetUserAddWithLongPasswordPromptsForYN()
    {
        // Historical FixtureGen orphan-SID command. Passwords longer than 14 characters
        // make net.exe prompt Y/N. ProcessRunner closes stdin so the child cannot wait
        // on an inherited Administrator console (the 15 Sep 2026 00:02:00 hang).
        // Expect a fast non-zero exit mentioning the 14-character prompt, not a timeout.
        ProcessRunner runner = new();
        ProcessRequest request = new(
            "net.exe",
            [
                "user",
                "WORFixaaaaaaaaaa",
                "Wor!aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaAa7",
                "/add",
                "/expires:never",
                "/passwordchg:no",
            ],
            Timeout: TimeSpan.FromSeconds(5));
        ProcessResult result = await runner.RunAsync(request);
        Assert.NotEqual(0, result.ExitCode);
        string text = result.StandardOutput + result.StandardError;
        Assert.Contains("14 characters", text, StringComparison.OrdinalIgnoreCase);
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
