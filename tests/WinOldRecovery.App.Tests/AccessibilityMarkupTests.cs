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
        Assert.Contains("DisplayName", xaml, StringComparison.Ordinal);
        Assert.Contains("RowTooltip", xaml, StringComparison.Ordinal);
        Assert.Contains("DecisionTooltip", xaml, StringComparison.Ordinal);
        Assert.Contains("IsInheritedDecision", xaml, StringComparison.Ordinal);
        Assert.Contains("IsGroupHeader", xaml, StringComparison.Ordinal);
        Assert.Contains("ProblemLabel", xaml, StringComparison.Ordinal);
        Assert.Contains("GrayTextBrushKey", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Foreground=\"Gray\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SpaceBudgetLevel", xaml, StringComparison.Ordinal);
        Assert.Contains("SourceIntegrityLevel", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"FilesViewTree\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"FilesViewLargest\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"FilesViewRecent\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"FilesViewUnknown\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"FilesViewProblems\"", xaml, StringComparison.Ordinal);
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
        Assert.Contains("AutomationProperties.AutomationId=\"RecipeInspectButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"AnalyzeGitButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Analyze Git repositories", xaml, StringComparison.Ordinal);
        Assert.Contains("InspectOverviewCardCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("OpenOverviewCardCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("RestoreOverviewCardCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("LeaveOverviewCardCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Inspect this card", xaml, StringComparison.Ordinal);
        Assert.Contains("Open this card folder", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"PauseRestoreButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"GoToPurgeButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"RecheckPreviewButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"EditSyncthingMappingButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Edit Syncthing folder mapping", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"DeleteWindowsOldButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"CancelPurgeButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"ScanStatusText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"RunRestoreButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"RunVerifyButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"PreferManualDeleteRadio\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"FilesCheckedBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"PurgeTypedNameBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("How to delete", xaml, StringComparison.Ordinal);
        Assert.Contains("PreferManualDelete", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"LogButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SessionLogView", xaml, StringComparison.Ordinal);
        Assert.Contains("CompactLayout", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"DismissFirstRunButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("FirstRunBody", xaml, StringComparison.Ordinal);
        Assert.Contains("Merge into existing folders", xaml, StringComparison.Ordinal);
        Assert.Contains("Restore into Recovered folder", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"MergeIntoProfileRadio\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"RestoreIntoRecoveredRadio\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"ConflictKeepBothRadio\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"ConflictSkipRadio\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"ConflictOverwriteRadio\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SystemFonts.MessageFontFamilyKey", xaml, StringComparison.Ordinal);
        Assert.Contains("SystemFonts.MessageFontSizeKey", xaml, StringComparison.Ordinal);
        Assert.Contains("UseLayoutRounding", xaml, StringComparison.Ordinal);
        string appXaml = File.ReadAllText(FindAppXaml());
        Assert.Contains("SystemFonts.MessageFontFamilyKey", appXaml, StringComparison.Ordinal);
        Assert.Contains("SystemFonts.MessageFontSizeKey", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"CardListBox\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"CommandBar\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"DecisionChip\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"6\"", appXaml, StringComparison.Ordinal);
        string manifest = File.ReadAllText(FindAppManifest());
        Assert.Contains("PerMonitorV2", manifest, StringComparison.Ordinal);
        Assert.Contains("dpiAware", manifest, StringComparison.Ordinal);
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

    private static string FindAppManifest()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "build", "app.manifest");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("app.manifest was not found from the test output directory.");
    }
}
