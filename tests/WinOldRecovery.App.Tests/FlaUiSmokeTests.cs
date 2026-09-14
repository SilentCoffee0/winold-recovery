using System.Diagnostics;
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

        using Process process = StartElevated(exe);
        using FlaUI.Core.Application application = FlaUI.Core.Application.Attach(process.Id);
        using UIA3Automation automation = new();
        FlaUI.Core.AutomationElements.Window? window = application.GetMainWindow(automation, TimeSpan.FromSeconds(45));
        Assert.NotNull(window);
        Assert.Contains("WinOld Recovery", window.Title, StringComparison.OrdinalIgnoreCase);
        AutomationElement? dismiss = window.FindFirstDescendant(cf => cf.ByAutomationId("DismissFirstRunButton"));
        dismiss?.AsButton().Invoke();
        AutomationElement help = window.FindFirstDescendant(cf => cf.ByAutomationId("HelpButton"))
            ?? throw new InvalidOperationException("Help button was not found.");
        help.AsButton().Invoke();
        AutomationElement closeHelp = window.FindFirstDescendant(cf => cf.ByAutomationId("CloseHelpButton"))
            ?? throw new InvalidOperationException("Close help button was not found.");
        closeHelp.AsButton().Invoke();
        AutomationElement preview = window.FindFirstDescendant(cf => cf.ByAutomationId("PreviewPlanButton"))
            ?? throw new InvalidOperationException("Preview plan button was not found.");
        Assert.NotNull(preview);
        AutomationElement scan = window.FindFirstDescendant(cf => cf.ByAutomationId("StepScan"))
            ?? throw new InvalidOperationException("Scan step button was not found.");
        scan.AsButton().Invoke();
        AutomationElement purge = window.FindFirstDescendant(cf => cf.ByAutomationId("StepPurge"))
            ?? throw new InvalidOperationException("Purge step button was not found.");
        Assert.NotNull(purge);
        application.Close();
    }

    private static Process StartElevated(string exe)
    {
        ProcessStartInfo start = new(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        Process? process = Process.Start(start);
        Skip.If(process is null, "UAC elevation was declined or no interactive desktop is attached.");
        return process;
    }
}
