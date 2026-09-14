using Microsoft.Win32;

namespace WinOldRecovery.App;

public sealed class WpfFolderPicker : IFolderPicker
{
    public string? PickFolder()
    {
        OpenFolderDialog dialog = new()
        {
            Title = "Choose a Windows.old folder",
            Multiselect = false,
        };
        return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName)
            ? dialog.FolderName
            : null;
    }
}
