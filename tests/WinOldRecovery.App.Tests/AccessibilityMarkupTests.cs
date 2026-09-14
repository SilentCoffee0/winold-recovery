using System.IO;

namespace WinOldRecovery.App.Tests;

public sealed class AccessibilityMarkupTests
{
    [Fact]
    public void MainWindow_DeclaresNarratorNamesForTheSixSteps()
    {
        string xaml = File.ReadAllText(FindMainWindowXaml());
        Assert.Contains("AutomationProperties.Name=\"Scan step\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Decide step\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Preview step\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Restore step\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Verify step\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Purge step\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"ScanButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinWidth=\"1024\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"640\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextSearch.TextPath=\"Name\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"BrowseSourceButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Pause scan", xaml, StringComparison.Ordinal);
        Assert.Contains("Compute folder sizes and counts", xaml, StringComparison.Ordinal);
        Assert.Contains("Hash files smaller than 64 MB during scan", xaml, StringComparison.Ordinal);
        Assert.Contains("ScanButtonLabel", xaml, StringComparison.Ordinal);
        Assert.Contains("DisplayLabel", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"UndoDecisionButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"RestoreJobsList\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"RerunVerifyButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"AcknowledgeVerifyButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"RecipeOpenFolderButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"PauseRestoreButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"GoToPurgeButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"RecheckPreviewButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"DeleteWindowsOldButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"CancelPurgeButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("How to delete", xaml, StringComparison.Ordinal);
        Assert.Contains("PreferManualDelete", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"LogButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SessionLogView", xaml, StringComparison.Ordinal);
        Assert.Contains("CompactLayout", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"DismissFirstRunButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("FirstRunBody", xaml, StringComparison.Ordinal);
        Assert.Contains("ThemeMode=\"System\"", File.ReadAllText(FindAppXaml()), StringComparison.Ordinal);
    }

    private static string FindMainWindowXaml()
    {
        string copied = Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml");
        if (File.Exists(copied))
        {
            return copied;
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "WinOldRecovery.App", "MainWindow.xaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("MainWindow.xaml was not found from the test output directory.");
    }

    private static string FindAppXaml()
    {
        string copied = Path.Combine(AppContext.BaseDirectory, "App.xaml");
        if (File.Exists(copied))
        {
            return copied;
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "WinOldRecovery.App", "App.xaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("App.xaml was not found from the test output directory.");
    }
}
