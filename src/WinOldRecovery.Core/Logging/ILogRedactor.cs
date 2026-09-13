namespace WinOldRecovery.Core.Logging;

public interface ILogRedactor
{
    string Redact(string value);
}
