using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.Recipes;

namespace WinOldRecovery.Recipes;

internal static class RecipeDecisions
{
    public static bool ShouldRestore(CardDecisions decisions, string key)
    {
        return decisions.ComponentDecisions.TryGetValue(key, out Decision decision) &&
            decision == Decision.Restore;
    }

    public static string ConflictName(string destinationPath)
    {
        return destinationPath + ".from-windows-old";
    }
}
