using System.IO;

namespace WinOldRecovery.App.Help;

public sealed class LocalHelp
{
    public static readonly IReadOnlyList<HelpTopic> Catalog =
    [
        new("What can and cannot be recovered", "limitations.md"),
        new("Browser passwords after reinstall", "browser-passwords.md"),
        new("Syncthing identity", "syncthing-identity.md"),
        new("Git repositories", "git-repositories.md"),
        new("WSL", "wsl.md"),
        new("SSH keys", "ssh-keys.md"),
        new("Anki", "anki.md"),
        new("GPG keyring", "gpg.md"),
        new("Outlook PST", "outlook.md"),
    ];

    private readonly string helpRoot;

    public LocalHelp(string helpRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helpRoot);
        this.helpRoot = Path.GetFullPath(helpRoot);
    }

    public static LocalHelp FromAppDirectory()
    {
        return new LocalHelp(Path.Combine(AppContext.BaseDirectory, "help"));
    }

    public string ReadDisplayText(string fileName)
    {
        string fullPath = Resolve(fileName);
        string markdown = File.ReadAllText(fullPath);
        return LocalMarkdown.ToDisplayText(markdown);
    }

    public string Resolve(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            fileName.Contains("..", StringComparison.Ordinal) ||
            fileName.Contains('/', StringComparison.Ordinal) ||
            fileName.Contains('\\', StringComparison.Ordinal) ||
            !fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Help pages must be local Markdown files in the help folder.");
        }

        string combined = Path.GetFullPath(Path.Combine(helpRoot, fileName));
        string prefix = helpRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(combined, helpRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Help pages cannot leave the help folder.");
        }

        if (!File.Exists(combined))
        {
            throw new FileNotFoundException("The help page was not found next to the application.", combined);
        }

        return combined;
    }
}
