namespace WinOldRecovery.Core.Logging;

public static class ExceptionReport
{
    public static string FormatUserMessage(
        Exception exception,
        string? sessionLogPath,
        ILogRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(redactor);

        string detail = redactor.Redact(exception.GetType().Name + ": " + exception.Message);
        string logLine = string.IsNullOrWhiteSpace(sessionLogPath)
            ? "The session log could not be located."
            : "A redacted log is at " + sessionLogPath + ".";
        return
            "WinOld Recovery hit an unexpected error. Windows.old was not modified by this crash." +
            Environment.NewLine + Environment.NewLine +
            detail + Environment.NewLine + Environment.NewLine +
            logLine +
            " If you open a bug, attach the support bundle from the Purge step (or the session folder), never raw secret files.";
    }
}
