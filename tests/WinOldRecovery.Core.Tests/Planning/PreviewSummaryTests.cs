using WinOldRecovery.Core.Planning;

namespace WinOldRecovery.Core.Tests.Planning;

public sealed class PreviewSummaryTests
{
    [Fact]
    public void Format_IncludesTransparencyCountsAndRunningApps()
    {
        RestorePlan plan = new(
            "session-1",
            @"C:\old",
            Path.GetTempPath(),
            [],
            0);
        PreflightResult preflight = new(
            true,
            1,
            2,
            [],
            [],
            []);
        PreviewInventory inventory = new(3, 0, 2, 14, 96L * 1024 * 1024 * 1024);
        string text = PreviewSummary.Format(
            plan,
            preflight,
            inventory,
            ["Firefox: transplant as recovered profile"],
            ["Firefox must be closed before the profile is transplanted."]);

        Assert.Contains("App recipes:", text, StringComparison.Ordinal);
        Assert.Contains("3 cloud-only", text, StringComparison.Ordinal);
        Assert.Contains("2 folders or files with access denied", text, StringComparison.Ordinal);
        Assert.Contains("Undecided items remaining: 14", text, StringComparison.Ordinal);
        Assert.Contains("Close these apps before Start restore:", text, StringComparison.Ordinal);
        Assert.Contains("read-only dry run", text, StringComparison.Ordinal);
    }
}
