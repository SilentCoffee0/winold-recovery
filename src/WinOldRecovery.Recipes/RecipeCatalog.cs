using WinOldRecovery.Core.Recipes;

namespace WinOldRecovery.Recipes;

public static class RecipeCatalog
{
    public static IReadOnlyList<IRecipe> All { get; } =
    [
        new SshRecipe(),
        new ChromiumRecipe("chrome", "Google Chrome", Path.Combine("AppData", "Local", "Google", "Chrome", "User Data")),
        new ChromiumRecipe("edge", "Microsoft Edge", Path.Combine("AppData", "Local", "Microsoft", "Edge", "User Data")),
        new FirefoxRecipe(),
        new GitRecipe(),
        new SyncthingRecipe(),
        new AnkiRecipe(),
        new WslRecipe(),
        new GpgRecipe(),
        new KeePassRecipe(),
        new OutlookRecipe(),
        new ThunderbirdRecipe(),
        new VsCodeRecipe(),
        new TerminalRecipe(),
        new ObsidianRecipe(),
        new GameSavesRecipe(),
    ];

    public static IReadOnlyList<(string Id, string AbsentTitle)> AbsentLabels { get; } =
    [
        ("ssh", "SSH keys"),
        ("chrome", "Google Chrome"),
        ("edge", "Microsoft Edge"),
        ("firefox", "Firefox"),
        ("git", "Git"),
        ("syncthing", "Syncthing"),
        ("anki", "Anki"),
        ("wsl", "WSL"),
        ("gpg", "GPG"),
        ("keepass", "KeePass"),
        ("outlook", "Outlook"),
        ("thunderbird", "Thunderbird"),
        ("vscode", "VS Code"),
        ("windows-terminal", "Windows Terminal"),
        ("obsidian", "Obsidian"),
        ("game-saves", "Game saves"),
    ];
}
