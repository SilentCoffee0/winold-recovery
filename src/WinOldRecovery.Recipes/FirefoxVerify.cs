using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.Recipes;

namespace WinOldRecovery.Recipes;

internal static class FirefoxVerify
{
    public static RecipeVerifyResult Check(PlanResult plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Writes.Any(static write => !File.Exists(write.DestinationPath)))
        {
            return new RecipeVerifyResult(false, "Firefox transplant missing");
        }

        string? iniPath = plan.Writes
            .Select(static write => write.DestinationPath)
            .FirstOrDefault(static path => path.EndsWith("profiles.ini", StringComparison.OrdinalIgnoreCase));
        string folder = plan.Card.Facts.GetValueOrDefault("folder") ?? string.Empty;
        if (!string.IsNullOrEmpty(iniPath) && !string.IsNullOrEmpty(folder))
        {
            string recovered = folder + "-recovered";
            string text = File.ReadAllText(iniPath);
            bool listed = FirefoxIni.ParseProfiles(text).Any(record =>
            {
                string path = record.Path.Replace('/', '\\');
                return path.EndsWith(recovered, StringComparison.OrdinalIgnoreCase) ||
                    record.Name.Equals(recovered, StringComparison.OrdinalIgnoreCase);
            });
            if (!listed)
            {
                return new RecipeVerifyResult(false, "profiles.ini does not list the recovered profile");
            }
        }

        string? destProfile = plan.Writes
            .Where(static write => write.ComponentKey == "transplant")
            .Select(static write => write.DestinationPath)
            .Where(static path => !path.EndsWith("profiles.ini", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetDirectoryName)
            .FirstOrDefault(static path => !string.IsNullOrEmpty(path));
        if (!string.IsNullOrEmpty(destProfile))
        {
            bool key4 = File.Exists(Path.Combine(destProfile, "key4.db"));
            bool logins = File.Exists(Path.Combine(destProfile, "logins.json")) ||
                File.Exists(Path.Combine(destProfile, "logins.db"));
            if (key4 != logins)
            {
                return new RecipeVerifyResult(false, "key4.db and logins.json must be restored together");
            }
        }

        RecipeWrite? places = plan.Writes.FirstOrDefault(static write =>
            Path.GetFileName(write.DestinationPath).Equals("places.sqlite", StringComparison.OrdinalIgnoreCase));
        if (places is null)
        {
            return new RecipeVerifyResult(true, "Firefox files present");
        }

        try
        {
            string copy = CopyPlaces(plan, places.DestinationPath);
            string integrity;
            using (SqliteConnection connection = ReadOnlySqlite.OpenReadOnly(copy))
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "PRAGMA integrity_check;";
                integrity = command.ExecuteScalar()?.ToString() ?? "unknown";
            }

            if (!integrity.Equals("ok", StringComparison.OrdinalIgnoreCase))
            {
                return new RecipeVerifyResult(false, "places.sqlite integrity_check failed");
            }

            SqliteConnection.ClearAllPools();
            (int count, _) = SqliteExports.FirefoxBookmarks(copy);
            if (!int.TryParse(
                    plan.Card.Facts.GetValueOrDefault("bookmarkCount"),
                    out int expected) ||
                count != expected)
            {
                return new RecipeVerifyResult(false, "Bookmark count does not match the source");
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            return new RecipeVerifyResult(false, "places.sqlite could not be verified");
        }

        return new RecipeVerifyResult(true, "Firefox files present");
    }

    private static string CopyPlaces(PlanResult plan, string destinationPlaces)
    {
        string tempDirectory = plan.Destination is not null
            ? Path.Combine(
                plan.Destination.SessionExportsDirectory,
                "verify",
                "firefox",
                Guid.NewGuid().ToString("N"))
            : Path.Combine(Path.GetTempPath(), "WinOldRecovery-FxVerify-" + Guid.NewGuid().ToString("N"));
        if (plan.Destination is not null)
        {
            return ReadOnlySqlite.CopyToTemp(
                plan.Destination.SafeFs,
                destinationPlaces,
                tempDirectory,
                "places.sqlite");
        }

        Directory.CreateDirectory(tempDirectory);
        string copy = Path.Combine(tempDirectory, "places.sqlite");
        File.Copy(destinationPlaces, copy, overwrite: true);
        return copy;
    }
}
