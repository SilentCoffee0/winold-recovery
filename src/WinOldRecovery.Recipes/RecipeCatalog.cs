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
    ];
}
