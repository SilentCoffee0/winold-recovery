using System.ComponentModel;
using System.Security.Principal;
using System.Text.Json;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Native;

namespace WinOldRecovery.FixtureGen;

public sealed class FixtureSelfCheck
{
    private static readonly string[] RequiredHazards =
    [
        "profile-alice",
        "profile-bob",
        "registry-hive",
        "legacy-junction",
        "junction-loop",
        "directory-symlink",
        "file-symlink",
        "long-path",
        "offline-placeholder",
        "invalid-name",
        "node-modules",
        "recipe-shells",
        "deny-acl",
        "orphan-sid",
        "efs",
    ];

    private readonly SafeFs safeFs = new(new SourceGuard());

    public FixtureCheckResult Check(string targetRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);

        string root = Path.GetFullPath(targetRoot);
        List<string> errors = [];
        FixtureManifest? manifest = ReadManifest(root, errors);
        if (manifest is null)
        {
            return new FixtureCheckResult(false, errors);
        }

        foreach (string key in RequiredHazards)
        {
            if (!manifest.Hazards.ContainsKey(key))
            {
                errors.Add($"Manifest is missing hazard '{key}'.");
            }
        }

        CheckProfiles(root, errors);
        CheckReparseHazards(root, manifest, errors);
        CheckLongPath(root, manifest, errors);
        CheckOfflinePlaceholder(root, manifest, errors);
        CheckInvalidName(root, manifest, errors);
        CheckNodeModules(root, manifest, errors);
        CheckRecipeShells(root, errors);

        if (manifest.FullHazardsRequested)
        {
            CheckFullHazards(root, manifest, errors);
        }
        else
        {
            foreach (string key in new[] { "deny-acl", "orphan-sid", "efs" })
            {
                if (manifest.Hazards.TryGetValue(key, out FixtureHazard? hazard) &&
                    !string.Equals(
                        hazard.Status,
                        "Unavailable",
                        StringComparison.Ordinal))
                {
                    errors.Add(
                        $"Portable fixture hazard '{key}' must be explicitly marked unavailable.");
                }
            }
        }

        return new FixtureCheckResult(errors.Count == 0, errors);
    }

    private FixtureManifest? ReadManifest(string root, ICollection<string> errors)
    {
        string path = Path.Combine(root, FixtureOptions.ManifestFileName);
        if (!File.Exists(path))
        {
            errors.Add($"Fixture manifest does not exist at '{path}'.");
            return null;
        }

        try
        {
            using StreamReader reader = new(safeFs.OpenRead(path));
            return JsonSerializer.Deserialize<FixtureManifest>(reader.ReadToEnd())
                ?? throw new JsonException("Manifest was empty.");
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or UnauthorizedAccessException)
        {
            errors.Add($"Fixture manifest could not be read: {exception.Message}");
            return null;
        }
    }

    private static void CheckProfiles(string root, ICollection<string> errors)
    {
        foreach (string profile in new[] { "Alice", "Bob" })
        {
            string hive = Path.Combine(root, "Users", profile, "NTUSER.DAT");
            if (!File.Exists(hive))
            {
                errors.Add($"Profile '{profile}' is missing NTUSER.DAT.");
            }
        }
    }

    private static void CheckReparseHazards(
        string root,
        FixtureManifest manifest,
        ICollection<string> errors)
    {
        foreach (string key in new[]
                 {
                     "legacy-junction",
                     "junction-loop",
                     "directory-symlink",
                     "file-symlink",
                 })
        {
            if (!manifest.Hazards.TryGetValue(key, out FixtureHazard? hazard) ||
                !string.Equals(hazard.Status, "Created", StringComparison.Ordinal))
            {
                if (manifest.FullHazardsRequested)
                {
                    errors.Add($"Full fixture did not create required reparse hazard '{key}'.");
                }

                continue;
            }

            string path = Path.Combine(root, hazard.RelativePath);
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                {
                    errors.Add($"Hazard '{key}' is not a reparse point.");
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                errors.Add($"Could not inspect reparse hazard '{key}': {exception.Message}");
            }
        }
    }

    private static void CheckLongPath(
        string root,
        FixtureManifest manifest,
        ICollection<string> errors)
    {
        if (!TryGetCreatedPath(root, manifest, "long-path", errors, out string? path))
        {
            return;
        }

        if (path.Length < 300)
        {
            errors.Add($"Long-path fixture is only {path.Length} characters.");
        }

        if (!File.Exists(path))
        {
            errors.Add("Long-path fixture file does not exist.");
        }
    }

    private static void CheckOfflinePlaceholder(
        string root,
        FixtureManifest manifest,
        ICollection<string> errors)
    {
        if (!TryGetCreatedPath(
                root,
                manifest,
                "offline-placeholder",
                errors,
                out string? path))
        {
            return;
        }

        if ((File.GetAttributes(path) & FileAttributes.Offline) == 0)
        {
            errors.Add("Offline placeholder stand-in lacks the OFFLINE attribute.");
        }
    }

    private static void CheckInvalidName(
        string root,
        FixtureManifest manifest,
        ICollection<string> errors)
    {
        if (!TryGetCreatedPath(root, manifest, "invalid-name", errors, out string? path))
        {
            return;
        }

        string expectedName = Path.GetFileName(path);
        string? parent = Path.GetDirectoryName(path);
        bool exactEntryExists =
            parent is not null &&
            Directory.EnumerateFiles(parent)
                .Any(
                    candidate => string.Equals(
                        Path.GetFileName(candidate),
                        expectedName,
                        StringComparison.Ordinal));
        if (!expectedName.EndsWith(' ') || !exactEntryExists)
        {
            string actualNames = parent is null
                ? "<no parent>"
                : string.Join(
                    ", ",
                    Directory.EnumerateFileSystemEntries(parent)
                        .Select(entry => $"'{Path.GetFileName(entry)}'"));
            errors.Add(
                $"Invalid-name fixture '{expectedName}' was not preserved with a trailing space. " +
                $"Entries: {actualNames}.");
        }
    }

    private static void CheckNodeModules(
        string root,
        FixtureManifest manifest,
        ICollection<string> errors)
    {
        if (!TryGetCreatedPath(root, manifest, "node-modules", errors, out string? path))
        {
            return;
        }

        int count = Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly).Count();
        if (count != manifest.NodeModulesFileCount)
        {
            errors.Add(
                $"node_modules contains {count} files; expected {manifest.NodeModulesFileCount}.");
        }
    }

    private static void CheckRecipeShells(string root, ICollection<string> errors)
    {
        string alice = Path.Combine(root, "Users", "Alice");
        string[] requiredFiles =
        [
            Path.Combine(
                alice,
                "AppData",
                "Roaming",
                "Mozilla",
                "Firefox",
                "Profiles",
                "fixture.default",
                "logins.json"),
            Path.Combine(
                alice,
                "AppData",
                "Local",
                "Google",
                "Chrome",
                "User Data",
                "Default",
                "Bookmarks"),
            Path.Combine(alice, ".ssh", "id_ed25519"),
            Path.Combine(alice, "Projects", "local-repository", ".git", "HEAD"),
            Path.Combine(
                alice,
                "AppData",
                "Roaming",
                "Anki2",
                "User 1",
                "collection.anki2"),
        ];

        foreach (string path in requiredFiles)
        {
            if (!File.Exists(path))
            {
                errors.Add($"Recipe shell is missing '{Path.GetRelativePath(root, path)}'.");
            }
        }
    }

    private static void CheckFullHazards(
        string root,
        FixtureManifest manifest,
        ICollection<string> errors)
    {
        foreach (string key in new[] { "deny-acl", "orphan-sid", "efs" })
        {
            if (!manifest.Hazards.TryGetValue(key, out FixtureHazard? hazard) ||
                !string.Equals(hazard.Status, "Created", StringComparison.Ordinal))
            {
                errors.Add($"Full fixture did not create privileged hazard '{key}'.");
            }
        }

        if (TryGetCreatedPath(root, manifest, "efs", errors, out string? efsPath) &&
            (File.GetAttributes(efsPath) & FileAttributes.Encrypted) == 0)
        {
            errors.Add("EFS fixture lacks the ENCRYPTED attribute.");
        }

        if (TryGetCreatedPath(root, manifest, "deny-acl", errors, out string? denyPath))
        {
            string protectedFile = Path.Combine(denyPath, "protected.txt");
            try
            {
                using FileStream unexpectedRead = File.OpenRead(protectedFile);
                errors.Add("Deny-ACL fixture remains readable through a normal file open.");
            }
            catch (UnauthorizedAccessException)
            {
                try
                {
                    using FileStream backupRead = BackupFile.OpenRead(
                        PathCanonicalizer.NormalizeLexically(protectedFile));
                    if (backupRead.ReadByte() < 0)
                    {
                        errors.Add("Backup-mode read opened the deny-ACL file but read no data.");
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or Win32Exception)
                {
                    errors.Add(
                        $"Backup-mode read could not read the deny-ACL fixture: {exception.Message}");
                }
            }
        }

        if (TryGetCreatedPath(root, manifest, "orphan-sid", errors, out string? orphanPath))
        {
            string ownerSid = FileSecurityInfo.GetOwnerSid(orphanPath);
            FixtureHazard orphanHazard = manifest.Hazards["orphan-sid"];
            if (!string.Equals(ownerSid, orphanHazard.Detail, StringComparison.Ordinal))
            {
                errors.Add("Orphan fixture owner SID differs from the recorded SID.");
            }

            try
            {
                _ = new SecurityIdentifier(ownerSid).Translate(typeof(NTAccount));
                errors.Add("Orphan fixture owner still resolves to a local account.");
            }
            catch (IdentityNotMappedException)
            {
                // Expected after the temporary local account has been removed.
            }
        }
    }

    private static bool TryGetCreatedPath(
        string root,
        FixtureManifest manifest,
        string key,
        ICollection<string> errors,
        out string path)
    {
        if (!manifest.Hazards.TryGetValue(key, out FixtureHazard? hazard) ||
            !string.Equals(hazard.Status, "Created", StringComparison.Ordinal))
        {
            errors.Add($"Required hazard '{key}' was not created.");
            path = string.Empty;
            return false;
        }

        path = Path.Combine(root, hazard.RelativePath);
        return true;
    }
}

public sealed record FixtureCheckResult(bool Success, IReadOnlyList<string> Errors)
{
    public void ThrowIfFailed()
    {
        if (!Success)
        {
            throw new InvalidDataException(
                "Fixture self-check failed:" +
                Environment.NewLine +
                string.Join(Environment.NewLine, Errors.Select(static error => $"- {error}")));
        }
    }
}
