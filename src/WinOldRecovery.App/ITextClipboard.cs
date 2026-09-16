namespace WinOldRecovery.App;

public interface ITextClipboard
{
    void SetText(string text);
}

public sealed class NullTextClipboard : ITextClipboard
{
    public void SetText(string text)
    {
    }
}

public sealed class WpfTextClipboard : ITextClipboard
{
    public void SetText(string text)
    {
        System.Windows.Clipboard.SetText(text);
    }
}
