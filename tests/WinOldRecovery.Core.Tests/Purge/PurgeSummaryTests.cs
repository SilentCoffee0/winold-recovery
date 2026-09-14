using WinOldRecovery.Core.Planning;
using WinOldRecovery.Core.Purge;

namespace WinOldRecovery.Core.Tests.Purge;

public sealed class PurgeSummaryTests
{
    [Fact]
    public void Format_MatchesUxChecklist()
    {
        PreviewInventory inventory = new(0, 0, 0, 14, 96L * 1024 * 1024 * 1024);
        string text = PurgeSummary.Format(
            6,
            true,
            new DateTimeOffset(2026, 9, 12, 20, 41, 0, TimeSpan.Zero),
            inventory,
            40L * 1024 * 1024 * 1024,
            623L * 1024 * 1024 * 1024,
            @"C:\Users\VJ\AppData\Local\WinOldRecovery\sessions\2026-09-12");

        Assert.Contains("✔ All 6 restore jobs verified (12 Sep 2026 20:41)", text, StringComparison.Ordinal);
        Assert.Contains("⚠ 14 items are still Undecided (96.0 GB)", text, StringComparison.Ordinal);
        Assert.Contains("They will be deleted with Windows.old.", text, StringComparison.Ordinal);
        Assert.Contains("✔ Free space after purge: 663.0 GB", text, StringComparison.Ordinal);
        Assert.Contains("sessions\\2026-09-12", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_EmptyPlan_SaysNoJobs()
    {
        string text = PurgeSummary.Format(
            0,
            true,
            null,
            new PreviewInventory(0, 0, 0, 0, 0),
            0,
            10,
            "session");
        Assert.Contains("No restore jobs to verify", text, StringComparison.Ordinal);
        Assert.Contains("No Undecided items remain", text, StringComparison.Ordinal);
    }
}
