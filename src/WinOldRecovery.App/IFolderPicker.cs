namespace WinOldRecovery.App;

public interface IFolderPicker
{
    string? PickFolder();
}

public sealed class NullFolderPicker : IFolderPicker
{
    public string? PickFolder() => null;
}
