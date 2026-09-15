using WinOldRecovery.Core.IO;

namespace WinOldRecovery.Recipes;

internal static class FirefoxProfileGroups
{
    public static bool Detected(SafeFs safeFs, string firefoxRoot)
    {
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
