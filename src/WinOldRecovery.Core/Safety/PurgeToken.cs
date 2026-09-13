namespace WinOldRecovery.Core.Safety;

public sealed class PurgeToken
{
    internal PurgeToken(string canonicalSourceRoot)
    {
        CanonicalSourceRoot = canonicalSourceRoot;
    }

    internal string CanonicalSourceRoot { get; }
}
