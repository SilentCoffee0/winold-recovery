using WinOldRecovery.App.ViewModels;
using WinOldRecovery.Core.Planning;
using WinOldRecovery.Core.Scan;

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
        Assert.Contains("Selected: 100 bytes of 10.0 GB free", okText, StringComparison.Ordinal);
        Assert.Contains("need 100 bytes with margin", okText, StringComparison.Ordinal);
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

    [Fact]
    public void FormatScanProgress_ListsProfilesFilesFoldersAndSkips()
    {
        WalkProgress report = new(
            10,
            4096,
            @"Users\Alice\Desktop",
            [],
            JunctionsSkipped: 2,
            CloudSkipped: 1,
            EncryptedSkipped: 0,
            AccessDenied: 3,
            FilesSeen: 7,
            FoldersSeen: 4,
            ProfileNames: ["Alice", "Public (shared)"]);
        string text = StatusStrip.FormatScanProgress(report, []);
        Assert.Contains("Profiles found: Alice, Public (shared)", text, StringComparison.Ordinal);
        Assert.Contains("Files: 7", text, StringComparison.Ordinal);
        Assert.Contains("Folders: 4", text, StringComparison.Ordinal);
        Assert.Contains("Skipped: 2 junctions, 1 cloud placeholders, 0 encrypted, 3 access denied", text, StringComparison.Ordinal);
    }
}
