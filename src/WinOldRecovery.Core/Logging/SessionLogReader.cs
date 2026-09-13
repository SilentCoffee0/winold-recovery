using WinOldRecovery.Core.IO;

namespace WinOldRecovery.Core.Logging;

public static class SessionLogReader
{
    public static string Read(SafeFs safeFs, string logPath, ILogRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(safeFs);
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        ArgumentNullException.ThrowIfNull(redactor);

        if (!safeFs.FileExists(logPath))
        {
            return "The session log is empty. Windows.old has not been modified by logging.";
        }

        using FileStream stream = safeFs.OpenShareRead(logPath);
        using StreamReader reader = new(stream);
        return redactor.Redact(reader.ReadToEnd());
    }
}
