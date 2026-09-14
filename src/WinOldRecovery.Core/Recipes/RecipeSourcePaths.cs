namespace WinOldRecovery.Core.Recipes;

public static class RecipeSourcePaths
{
    public static string? OpenPath(RecipeCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (!card.Facts.TryGetValue("source", out string? source) ||
            string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        return source.Trim();
    }
}
