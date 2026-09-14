namespace WinOldRecovery.Core.Processes;

public interface IProcessPresence
{
    bool IsRunning(string processName);
}

public sealed class Win32ProcessPresence : IProcessPresence
{
    public bool IsRunning(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        string name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
        return System.Diagnostics.Process.GetProcessesByName(name).Length > 0;
    }
}

public sealed class NeverRunningProcessPresence : IProcessPresence
{
    public bool IsRunning(string processName) => false;
}
