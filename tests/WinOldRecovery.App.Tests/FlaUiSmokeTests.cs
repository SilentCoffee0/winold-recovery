using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
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

        using Process started = StartElevated(exe);
        Process process = WaitForAppProcess(started);
        FlaUI.Core.Application? application = null;
        try
        {
            try
            {
                application = FlaUI.Core.Application.Attach(process.Id);
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 5)
            {
                Skip.If(
                    true,
                    "UIPI blocked attach: run the testhost elevated so FlaUI can automate the requireAdministrator EXE.");
            }

            using UIA3Automation automation = new();
            FlaUI.Core.AutomationElements.Window found = WaitUntilHelpReady(process, application!, automation);
            Assert.Contains("WinOld Recovery", found.Title, StringComparison.OrdinalIgnoreCase);
            TryInvoke(found, "DismissFirstRunButton");
            TryInvoke(found, "DismissInterruptedButton");
            AutomationElement help = found.FindFirstDescendant(cf => cf.ByAutomationId("HelpButton"))
                ?? throw new InvalidOperationException("Help button was not found.");
            help.AsButton().Invoke();
            AutomationElement closeHelp = found.FindFirstDescendant(cf => cf.ByAutomationId("CloseHelpButton"))
                ?? throw new InvalidOperationException("Close help button was not found.");
            closeHelp.AsButton().Invoke();
            AutomationElement preview = found.FindFirstDescendant(cf => cf.ByAutomationId("PreviewPlanButton"))
                ?? throw new InvalidOperationException("Preview plan button was not found.");
            Assert.NotNull(preview);
            AutomationElement scan = found.FindFirstDescendant(cf => cf.ByAutomationId("StepScan"))
                ?? throw new InvalidOperationException("Scan step button was not found.");
            scan.AsButton().Invoke();
            AutomationElement purge = found.FindFirstDescendant(cf => cf.ByAutomationId("StepPurge"))
                ?? throw new InvalidOperationException("Purge step button was not found.");
            Assert.NotNull(purge);
        }
        finally
        {
            try
            {
                application?.Close();
            }
            catch (Exception)
            {
            }

            application?.Dispose();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private static Process WaitForAppProcess(Process started)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            Process? newest = Process.GetProcessesByName("WinOldRecovery")
                .OrderByDescending(static candidate =>
                {
                    try
                    {
                        return candidate.StartTime;
                    }
                    catch (Exception)
                    {
                        return DateTime.MinValue;
                    }
                })
                .FirstOrDefault();
            if (newest is not null && !newest.HasExited)
            {
                return newest;
            }

            if (started.HasExited)
            {
                break;
            }

            Thread.Sleep(200);
        }

        return started;
    }

    private static FlaUI.Core.AutomationElements.Window WaitUntilHelpReady(
        Process process,
        FlaUI.Core.Application application,
        UIA3Automation automation)
    {
        DateTime deadline = DateTime.UtcNow + ReadWindowTimeout();
        FlaUI.Core.AutomationElements.Window? window = null;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"WinOldRecovery.exe exited before showing a window (exit {process.ExitCode}).");
            }

            process.Refresh();
            window = application.GetMainWindow(automation, TimeSpan.FromSeconds(2));
            if (window is not null &&
                window.FindFirstDescendant(cf => cf.ByAutomationId("HelpButton")) is not null)
            {
                return window;
            }

            Thread.Sleep(250);
        }

        throw new InvalidOperationException(
            $"No ready main window. pid={process.Id} name={process.ProcessName} exited={process.HasExited} handle=0x{process.MainWindowHandle.ToInt64():X} title='{process.MainWindowTitle}'.");
    }

    private static TimeSpan ReadWindowTimeout()
    {
        // The first launch of a freshly published single-file EXE extracts the
        // bundle and is scanned by antimalware before any window appears; a warm
        // launch shows the window in about a second.
        if (int.TryParse(
                Environment.GetEnvironmentVariable("FLAUI_WINDOW_TIMEOUT_SECONDS"),
                out int seconds) &&
            seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return TimeSpan.FromMinutes(5);
    }

    private static void TryInvoke(FlaUI.Core.AutomationElements.Window window, string automationId)
    {
        AutomationElement? element = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        element?.AsButton().Invoke();
    }

    private static Process StartElevated(string exe)
    {
        bool alreadyAdmin = IsAdministrator();
        ProcessStartInfo start = new(exe)
        {
            UseShellExecute = true,
            Verb = alreadyAdmin ? string.Empty : "runas",
            WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
            WindowStyle = ProcessWindowStyle.Normal,
        };
        Process? process = Process.Start(start);
        Skip.If(process is null, "UAC elevation was declined or no interactive desktop is attached.");
        return process;
    }

    private static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
