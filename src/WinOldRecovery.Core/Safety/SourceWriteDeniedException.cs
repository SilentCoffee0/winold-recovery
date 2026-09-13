namespace WinOldRecovery.Core.Safety;

public sealed class SourceWriteDeniedException : InvalidOperationException
{
    public SourceWriteDeniedException(string path)
        : base($"A write beneath a registered source root was refused: '{path}'.")
    {
        Path = path;
    }

    public string Path { get; }
}
