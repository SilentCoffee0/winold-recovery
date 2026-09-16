using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Recipes;

namespace WinOldRecovery.Recipes;

internal static class FirefoxProfileGroups
{
    public static bool Detected(
        SafeFs safeFs,
        string firefoxRoot,
        RecipeIndex? index = null,
        string? relativeUnderProfile = null)
    {
        if (index is not null && !string.IsNullOrWhiteSpace(relativeUnderProfile))
        {
            foreach (string name in new[] { "profiles.ini", "installs.ini" })
            {
                string? indexed = DetectorWalk.IndexedImmediatePath(
                    index,
                    firefoxRoot,
                    relativeUnderProfile,
                    name);
                if (indexed is not null && safeFs.FileExists(indexed) && HasStoreId(safeFs.ReadAllText(indexed)))
                {
                    return true;
                }
            }

            return false;
        }

        if (!safeFs.DirectoryExists(firefoxRoot))
        {
            return false;
        }

        foreach (string name in new[] { "profiles.ini", "installs.ini" })
        {
            string path = Path.Combine(firefoxRoot, name);
            if (safeFs.FileExists(path) && HasStoreId(safeFs.ReadAllText(path)))
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasStoreId(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("StoreID", StringComparison.OrdinalIgnoreCase) &&
                trimmed.Contains('='))
            {
                return true;
            }
        }

        return false;
    }
}
