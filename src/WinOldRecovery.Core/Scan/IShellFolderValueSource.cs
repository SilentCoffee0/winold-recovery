namespace WinOldRecovery.Core.Scan;

public interface IShellFolderValueSource
{
    IReadOnlyDictionary<string, string> GetValues(string profileRoot);
}
