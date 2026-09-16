using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using WinOldRecovery.App.ViewModels;

namespace WinOldRecovery.App.Tests;

public sealed class FlaUiSmokeTests
{
    [SkippableFact(Timeout = 600_000)]
    public async Task ScanThroughPurge_OnPublishedExe_WhenInteractiveSessionProvided()
    {
        Skip.If(
            !string.Equals(Environment.GetEnvironmentVariable("RUN_FLAUI"), "1", StringComparison.Ordinal),
            "No interactive session. Set RUN_FLAUI=1 and WINOLD_RECOVERY_EXE on a desktop to run the FlaUI smoke.");

        string? exe = Environment.GetEnvironmentVariable("WINOLD_RECOVERY_EXE");
        Assert.False(string.IsNullOrWhiteSpace(exe), "Set WINOLD_RECOVERY_EXE to the published WinOldRecovery.exe.");
        Assert.True(File.Exists(exe!), exe);

        string work = Path.Combine(Path.GetTempPath(), "wor-flaui-e2e-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(work, "OldInstall");
        string dest = Path.Combine(work, "Recovered");
        string markerName = "hello-flaui.txt";
        string markerText = "flaui-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice", "Desktop"));
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(source, "Users", "Alice", "NTUSER.DAT"), "hive");
        File.WriteAllText(Path.Combine(source, "Users", "Alice", "Desktop", markerName), markerText);

        Assert.False(
            ShellViewModel.TouchesVolumeRootPreviousInstallation(source),
            "FlaUI e2e must not use a volume-root Windows.old.");
        Assert.False(ShellViewModel.TouchesVolumeRootPreviousInstallation(dest));

        Environment.SetEnvironmentVariable("WINOLD_RECOVERY_SMOKE_SOURCE", source);
        Environment.SetEnvironmentVariable("WINOLD_RECOVERY_SMOKE_DEST", dest);

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
            WaitForStatus(
                found,
                static text => text.Contains("Smoke fixture ready (OldInstall)", StringComparison.Ordinal),
                TimeSpan.FromMinutes(1),
                "smoke fixture");
            AutomationElement help = found.FindFirstDescendant(cf => cf.ByAutomationId("HelpButton"))
                ?? throw new InvalidOperationException("Help button was not found.");
            help.AsButton().Invoke();
            AutomationElement closeHelp = found.FindFirstDescendant(cf => cf.ByAutomationId("CloseHelpButton"))
                ?? throw new InvalidOperationException("Close help button was not found.");
            closeHelp.AsButton().Invoke();
            TryInvoke(found, "StepScan");
            InvokeEnabled(found, "ScanButton");
            WaitForStatus(found, static text => text.Contains("holds", StringComparison.OrdinalIgnoreCase), TimeSpan.FromMinutes(3), "scan");

            TryInvoke(found, "StepDecide");
            InvokeFirstEnabledByName(found, "Restore this card");
            InvokeEnabled(found, "PreviewPlanButton");
            WaitForNonEmptyPreview(found);

            SetNamedText(found, "Restore destination folder", dest);
            InvokeEnabled(found, "PreviewPlanButton");
            WaitForNonEmptyPreview(found);

            InvokeEnabled(found, "RunRestoreButton");
            WaitForStatus(
                found,
                static text => text.Contains("Restore finished", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromMinutes(3),
                "restore");

            string[] restored = Directory.GetFiles(dest, markerName, SearchOption.AllDirectories);
            Assert.True(restored.Length > 0, "Restore did not copy " + markerName + " under " + dest);
            Assert.Equal(markerText, File.ReadAllText(restored[0]));
            Assert.True(Directory.Exists(source), "Source must stay intact until purge.");

            InvokeEnabled(found, "RunVerifyButton");
            WaitForStatus(
                found,
                static text => text.Contains("Verify report:", StringComparison.Ordinal),
                TimeSpan.FromMinutes(2),
                "verify");

            TryInvoke(found, "StepPurge");
            SetToggle(found, "PreferManualDeleteRadio", true);
            AutomationElement? cleanup = found.FindFirstDescendant(cf => cf.ByAutomationId("PreferCleanupHandlerRadio"));
            Assert.False(
                cleanup?.AsRadioButton().IsChecked == true,
                "FlaUI e2e must never arm Previous Installations cleanup.");
            SetToggle(found, "FilesCheckedBox", true);
            SetToggle(found, "UndecidedAcknowledgedBox", true);
            SetToggle(found, "CustomRootConfirmedBox", true);
            SetNamedText(found, "Type the Windows.old folder name to confirm purge", Path.GetFileName(source));
            InvokeEnabled(found, "DeleteWindowsOldButton");
            WaitForStatus(
                found,
                static text => text.Contains("Purge finished", StringComparison.OrdinalIgnoreCase) &&
                    !text.Contains("cleanup-handler", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromMinutes(5),
                "purge");

            Assert.False(Directory.Exists(source), "Manual purge should remove the browsed fixture.");
            Assert.True(File.Exists(restored[0]), "Purge must not delete restored files.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("WINOLD_RECOVERY_SMOKE_SOURCE", null);
            Environment.SetEnvironmentVariable("WINOLD_RECOVERY_SMOKE_DEST", null);
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

            try
            {
                if (Directory.Exists(work) &&
                    !ShellViewModel.TouchesVolumeRootPreviousInstallation(work))
                {
                    Directory.Delete(work, recursive: true);
                }
            }
            catch (Exception)
            {
            }
        }

        await Task.CompletedTask;
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

    private static void InvokeEnabled(FlaUI.Core.AutomationElements.Window window, string automationId)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        while (DateTime.UtcNow < deadline)
        {
            AutomationElement? element = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            if (element is not null && element.IsEnabled)
            {
                element.AsButton().Invoke();
                return;
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException("Timed out waiting to invoke " + automationId + ". Status: " + ReadScanStatus(window));
    }

    private static void InvokeFirstEnabledByName(FlaUI.Core.AutomationElements.Window window, string name)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        while (DateTime.UtcNow < deadline)
        {
            AutomationElement[] matches = window.FindAllDescendants(cf => cf.ByName(name));
            foreach (AutomationElement match in matches)
            {
                if (!match.IsEnabled)
                {
                    continue;
                }

                match.AsButton().Invoke();
                return;
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException("Timed out waiting for an enabled '" + name + "'. Status: " + ReadScanStatus(window));
    }

    private static void WaitForStatus(
        FlaUI.Core.AutomationElements.Window window,
        Func<string, bool> match,
        TimeSpan timeout,
        string step)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        string last = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            last = ReadScanStatus(window);
            if (match(last))
            {
                return;
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException("Timed out waiting for " + step + ". Last status: " + last);
    }

    private static void WaitForNonEmptyPreview(FlaUI.Core.AutomationElements.Window window)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        string last = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            last = ReadScanStatus(window);
            if (last.Contains("Preview:", StringComparison.Ordinal) &&
                !last.Contains("Preview: 0 copy operations", StringComparison.Ordinal))
            {
                return;
            }

            if (last.Contains("Preview: 0 copy operations", StringComparison.Ordinal))
            {
                InvokeFirstEnabledByName(window, "Restore this card");
                InvokeEnabled(window, "PreviewPlanButton");
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException("Timed out waiting for a non-empty preview. Last status: " + last);
    }

    private static string ReadScanStatus(FlaUI.Core.AutomationElements.Window window)
    {
        AutomationElement? status = window.FindFirstDescendant(cf => cf.ByAutomationId("ScanStatusText"));
        if (status is null)
        {
            return string.Empty;
        }

        if (status.Patterns.Value.IsSupported)
        {
            string? value = status.Patterns.Value.Pattern.Value;
            if (!string.IsNullOrWhiteSpace(value) &&
                !value.Equals("Scan status", StringComparison.Ordinal))
            {
                return value;
            }
        }

        string name = status.Name ?? string.Empty;
        if (name.Equals("Scan status", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return name;
    }

    private static void SetNamedText(FlaUI.Core.AutomationElements.Window window, string name, string value)
    {
        AutomationElement element = window.FindFirstDescendant(cf => cf.ByName(name))
            ?? throw new InvalidOperationException("Could not find text box " + name);
        element.AsTextBox().Text = value;
    }

    private static void SetToggle(FlaUI.Core.AutomationElements.Window window, string automationId, bool on)
    {
        AutomationElement element = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId))
            ?? throw new InvalidOperationException("Could not find " + automationId);
        if (element.ControlType == ControlType.RadioButton)
        {
            FlaUI.Core.AutomationElements.RadioButton radio = element.AsRadioButton();
            if (radio.IsEnabled)
            {
                radio.IsChecked = on;
            }

            return;
        }

        FlaUI.Core.AutomationElements.CheckBox box = element.AsCheckBox();
        box.IsChecked = on;
    }

    private static Process StartElevated(string exe)
    {
        bool alreadyAdmin = IsAdministrator();
        ProcessStartInfo start = new(exe)
        {
            UseShellExecute = !alreadyAdmin,
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
