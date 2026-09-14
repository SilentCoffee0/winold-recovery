using WinOldRecovery.App.ViewModels;
using WinOldRecovery.Core.Planning;

namespace WinOldRecovery.App.Tests;

public sealed class StatusStripTests
{
    [Fact]
    public void IntegrityLevel_MapsTheFourSourceStates()
    {
        Assert.Equal(SourceIntegrityLevel.Untouched, StatusStrip.IntegrityLevel("Windows.old untouched"));
        Assert.Equal(SourceIntegrityLevel.Deleting, StatusStrip.IntegrityLevel("Deleting…"));
        Assert.Equal(SourceIntegrityLevel.Removed, StatusStrip.IntegrityLevel("Windows.old removed"));
        Assert.Equal(SourceIntegrityLevel.Partial, StatusStrip.IntegrityLevel("Windows.old partly deleted"));
    }

    [Fact]
    public void FormatSpaceBudget_TurnsAmberThenRed()
    {
        const long free = 10L * 1024 * 1024 * 1024;
        (string okText, SpaceBudgetLevel ok) = StatusStrip.FormatSpaceBudget(100, 100, free);
        Assert.Equal(SpaceBudgetLevel.Ok, ok);
        Assert.DoesNotContain("Will not fit", okText, StringComparison.Ordinal);
        Assert.DoesNotContain("Approaching", okText, StringComparison.Ordinal);

        long amberSelected = (long)(0.95 * (free - PreflightChecker.AbsoluteMarginBytes));
        (string amberText, SpaceBudgetLevel amber) = StatusStrip.FormatSpaceBudget(
            amberSelected,
            amberSelected,
            free);
        Assert.Equal(SpaceBudgetLevel.Approaching, amber);
        Assert.Contains("Approaching the free-space limit", amberText, StringComparison.Ordinal);

        (string redText, SpaceBudgetLevel red) = StatusStrip.FormatSpaceBudget(
            free * 2,
            free * 2,
            free);
        Assert.Equal(SpaceBudgetLevel.WillNotFit, red);
        Assert.Contains("Will not fit", redText, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatScanSummary_AlwaysNamesKnownAppsAndHighValue()
    {
        string text = StatusStrip.FormatScanSummary(12, 4096, 2, 3, 5, hashedFiles: 4);
        Assert.Contains("across 2 profiles", text, StringComparison.Ordinal);
        Assert.Contains("Hashed 4 files", text, StringComparison.Ordinal);
        Assert.Contains("We found 3 known apps and 5 high-value items", text, StringComparison.Ordinal);
        Assert.Contains("Nothing has been changed", text, StringComparison.Ordinal);
    }
}
