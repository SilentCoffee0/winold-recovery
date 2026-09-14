using CommunityToolkit.Mvvm.ComponentModel;

namespace WinOldRecovery.App.ViewModels;

public sealed class SyncthingMappingRow : ObservableObject
{
    private string plannedPath;
    private readonly Action? plannedPathChanged;

    public SyncthingMappingRow(
        string instanceKey,
        string folderId,
        string label,
        string sourcePath,
        string plannedPath,
        bool existsOnDestination,
        Action? plannedPathChanged = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderId);
        InstanceKey = instanceKey;
        FolderId = folderId;
        Label = string.IsNullOrWhiteSpace(label) ? folderId : label;
        SourcePath = sourcePath;
        this.plannedPath = plannedPath;
        Presence = existsOnDestination ? "exists" : "missing";
        this.plannedPathChanged = plannedPathChanged;
    }

    public string InstanceKey { get; }

    public string FolderId { get; }

    public string Label { get; }

    public string SourcePath { get; }

    public string Presence { get; }

    public string PlannedPath
    {
        get => plannedPath;
        set
        {
            if (SetProperty(ref plannedPath, value ?? string.Empty))
            {
                plannedPathChanged?.Invoke();
            }
        }
    }

    public string Display =>
        Label + ": " + SourcePath + " → " + PlannedPath + " (" + Presence + ")";
}
