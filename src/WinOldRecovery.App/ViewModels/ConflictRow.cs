using CommunityToolkit.Mvvm.ComponentModel;

namespace WinOldRecovery.App.ViewModels;

public sealed class ConflictRow : ObservableObject
{
    private bool approved;

    public ConflictRow(string destinationPath, long existingSize, DateTimeOffset existingWriteUtc)
    {
        DestinationPath = destinationPath;
        ExistingSize = existingSize;
        ExistingWriteUtc = existingWriteUtc;
        DisplayName = System.IO.Path.GetFileName(destinationPath);
    }

    public string DestinationPath { get; }

    public string DisplayName { get; }

    public long ExistingSize { get; }

    public DateTimeOffset ExistingWriteUtc { get; }

    public bool Approved
    {
        get => approved;
        set => SetProperty(ref approved, value);
    }
}
