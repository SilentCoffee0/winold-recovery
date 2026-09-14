using CommunityToolkit.Mvvm.ComponentModel;

namespace WinOldRecovery.App.ViewModels;

public sealed class ConflictRow : ObservableObject
{
    private readonly Action? approvedChanged;
    private bool approved;

    public ConflictRow(
        string destinationPath,
        long existingSize,
        DateTimeOffset existingWriteUtc,
        Action? approvedChanged = null)
    {
        DestinationPath = destinationPath;
        ExistingSize = existingSize;
        ExistingWriteUtc = existingWriteUtc;
        DisplayName = System.IO.Path.GetFileName(destinationPath);
        this.approvedChanged = approvedChanged;
    }

    public string DestinationPath { get; }

    public string DisplayName { get; }

    public long ExistingSize { get; }

    public DateTimeOffset ExistingWriteUtc { get; }

    public bool Approved
    {
        get => approved;
        set
        {
            if (SetProperty(ref approved, value))
            {
                approvedChanged?.Invoke();
            }
        }
    }
}
