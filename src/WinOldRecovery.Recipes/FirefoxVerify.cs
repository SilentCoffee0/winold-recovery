using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Recipes;
using WinOldRecovery.Core.Safety;

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

        RecipeVerifyResult? registration = CheckRegistration(plan);
        if (registration is not null)
        {
            return registration;
        }

        RecipeVerifyResult? logins = CheckKey4LoginPair(plan);
        if (logins is not null)
        {
            return logins;
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

    public static RecipeVerifyResult? CheckRegistration(PlanResult plan)
    {
        string? iniPath = plan.Writes
            .Select(static write => write.DestinationPath)
            .FirstOrDefault(static path => path.EndsWith("profiles.ini", StringComparison.OrdinalIgnoreCase));
        string folder = plan.Card.Facts.GetValueOrDefault("folder") ?? string.Empty;
        if (string.IsNullOrEmpty(iniPath) || string.IsNullOrEmpty(folder))
        {
            return null;
        }

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

        return null;
    }

    public static RecipeVerifyResult? CheckKey4LoginPair(PlanResult plan)
    {
        string? destProfile = RecoveredProfileDirectory(plan);
        if (string.IsNullOrEmpty(destProfile))
        {
            return null;
        }

        bool key4 = File.Exists(Path.Combine(destProfile, "key4.db"));
        bool logins = File.Exists(Path.Combine(destProfile, "logins.json")) ||
            File.Exists(Path.Combine(destProfile, "logins.db"));
        if (key4 != logins)
        {
            return new RecipeVerifyResult(false, "key4.db and logins.json must be restored together");
        }

        return null;
    }

    private static string? RecoveredProfileDirectory(PlanResult plan)
    {
        string folder = plan.Card.Facts.GetValueOrDefault("folder") ?? string.Empty;
        if (string.IsNullOrEmpty(folder))
        {
            return null;
        }

        string recovered = folder + "-recovered";
        foreach (RecipeWrite write in plan.Writes)
        {
            if (write.ComponentKey != "transplant")
            {
                continue;
            }

            string? current = write.DestinationPath;
            while (!string.IsNullOrEmpty(current))
            {
                if (Path.GetFileName(current).Equals(recovered, StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }

                current = Path.GetDirectoryName(current);
            }
        }

        return null;
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
        SafeFs safeFs = plan.Destination?.SafeFs ?? new SafeFs(new SourceGuard());
        return ReadOnlySqlite.CopyToTemp(safeFs, destinationPlaces, tempDirectory, "places.sqlite");
    }
}
