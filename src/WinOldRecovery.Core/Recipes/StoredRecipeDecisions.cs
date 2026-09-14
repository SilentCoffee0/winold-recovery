using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.Persistence;

namespace WinOldRecovery.Core.Recipes;

public static class StoredRecipeDecisions
{
    public const string KvPrefix = "recipe.component.";

    public static string KvKey(string instanceKey, string componentKey)
    {
        return KvPrefix + instanceKey + "." + componentKey;
    }

    public static Decision Resolve(RecipeComponent component, string? stored)
    {
        if (component.Fixed)
        {
            return Decision.Undecided;
        }

        if (stored is not null &&
            Enum.TryParse(stored, ignoreCase: true, out Decision parsed) &&
            parsed is Decision.Restore or Decision.LeaveBehind or Decision.Undecided)
        {
            return parsed;
        }

        return component.SuggestedDefault;
    }

    public static Dictionary<string, Decision> Load(SessionDb sessionDb, string sessionId, RecipeCard card)
    {
        ArgumentNullException.ThrowIfNull(sessionDb);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(card);
        Dictionary<string, Decision> decisions = [];
        foreach (RecipeComponent component in card.Components)
        {
            string? stored = sessionDb.GetKv(sessionId, KvKey(card.InstanceKey, component.Key));
            decisions[component.Key] = Resolve(component, stored);
        }

        return decisions;
    }
}
