using System.IO;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace WinOldRecovery.App.Tests;

public sealed class FlaUiSmokeTests
{
    [SkippableFact]
    public void ScanThroughPurge_OnPublishedExe_WhenInteractiveSessionProvided()
    {
        Skip.If(
            !string.Equals(Environment.GetEnvironmentVariable("RUN_FLAUI"), "1", StringComparison.Ordinal),
            "No interactive session. Set RUN_FLAUI=1 and WINOLD_RECOVERY_EXE on a desktop to run the FlaUI smoke.");

        string? exe = Environment.GetEnvironmentVariable("WINOLD_RECOVERY_EXE");
        Assert.False(string.IsNullOrWhiteSpace(exe), "Set WINOLD_RECOVERY_EXE to the published WinOldRecovery.exe.");
        Assert.True(File.Exists(exe!), exe);

        using FlaUI.Core.Application application = FlaUI.Core.Application.Launch(exe);
        using UIA3Automation automation = new();
        FlaUI.Core.AutomationElements.Window? window = application.GetMainWindow(automation, TimeSpan.FromSeconds(30));
        Assert.NotNull(window);
        Assert.Contains("WinOld Recovery", window.Title, StringComparison.OrdinalIgnoreCase);
        AutomationElement scan = window.FindFirstDescendant(cf => cf.ByAutomationId("StepScan"))
            ?? throw new InvalidOperationException("Scan step button was not found.");
        scan.AsButton().Invoke();
        AutomationElement purge = window.FindFirstDescendant(cf => cf.ByAutomationId("StepPurge"))
            ?? throw new InvalidOperationException("Purge step button was not found.");
        Assert.NotNull(purge);
        application.Close();
    }
}
