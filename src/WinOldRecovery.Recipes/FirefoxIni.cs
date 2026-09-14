using WinOldRecovery.Core.IO;

namespace WinOldRecovery.Recipes;

public sealed record FirefoxProfileRecord(string Name, string Path, bool IsRelative, bool IsDefault);

public sealed record DiscoveredFirefoxProfile(string Directory, string Name, bool IsDefault);

public static class FirefoxIni
{
    public static IReadOnlyList<FirefoxProfileRecord> ParseProfiles(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<FirefoxProfileRecord> profiles = [];
        foreach ((string section, Dictionary<string, string> values) in ParseSections(text))
        {
            if (!IsProfileSection(section) ||
                !values.TryGetValue("Path", out string? path) ||
                string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string name = values.TryGetValue("Name", out string? named) && !string.IsNullOrWhiteSpace(named)
                ? named
                : Path.GetFileName(path.Replace('/', '\\'));
            bool isRelative = !values.TryGetValue("IsRelative", out string? relative) ||
                IsTruthy(relative);
            bool isDefault = values.TryGetValue("Default", out string? defaultValue) &&
                IsTruthy(defaultValue);
            profiles.Add(new FirefoxProfileRecord(name, path, isRelative, isDefault));
        }

        return profiles;
    }

    public static IReadOnlyList<string> ParseInstallDefaults(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<string> defaults = [];
        foreach ((string section, Dictionary<string, string> values) in ParseSections(text))
        {
            if (IsProfileSection(section) ||
                section.Equals("General", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (values.TryGetValue("Default", out string? path) && !string.IsNullOrWhiteSpace(path))
            {
                defaults.Add(path);
            }
        }

        return defaults;
    }

    public static IReadOnlyList<DiscoveredFirefoxProfile> Discover(
        SafeFs safeFs,
        string firefoxRoot,
        string containingRoot)
    {
        ArgumentNullException.ThrowIfNull(safeFs);
        ArgumentException.ThrowIfNullOrWhiteSpace(firefoxRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(containingRoot);

        Dictionary<string, DiscoveredFirefoxProfile> found = new(StringComparer.OrdinalIgnoreCase);

        void Consider(string directory, string name, bool isDefault)
        {
            if (!safeFs.DirectoryExists(directory) || DetectorWalk.IsReparse(directory) ||
                !IsUnder(containingRoot, directory))
            {
                return;
            }

            string key;
            try
            {
                key = PathCanonicalizer.NormalizeLexically(Path.GetFullPath(directory));
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or IOException)
            {
                return;
            }

            string display = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(directory) : name;
            if (found.TryGetValue(key, out DiscoveredFirefoxProfile? existing))
            {
                found[key] = existing with
                {
                    Name = string.IsNullOrWhiteSpace(existing.Name) ? display : existing.Name,
                    IsDefault = existing.IsDefault || isDefault,
                };
                return;
            }

            found[key] = new DiscoveredFirefoxProfile(directory, display, isDefault);
        }

        ReadIni(
            safeFs,
            Path.Combine(firefoxRoot, "profiles.ini"),
            (directory, name, isDefault) => Consider(directory, name, isDefault),
            firefoxRoot);
        ReadIni(
            safeFs,
            Path.Combine(firefoxRoot, "installs.ini"),
            (directory, name, isDefault) => Consider(directory, name, isDefault),
            firefoxRoot);

        string profilesDir = Path.Combine(firefoxRoot, "Profiles");
        if (safeFs.DirectoryExists(profilesDir) && !DetectorWalk.IsReparse(profilesDir))
        {
            foreach (string entry in safeFs.EnumerateFileSystemEntries(profilesDir))
            {
                if (safeFs.DirectoryExists(entry))
                {
                    Consider(entry, Path.GetFileName(entry), isDefault: false);
                }
            }
        }

        return [.. found.Values];
    }

    private static void ReadIni(
        SafeFs safeFs,
        string path,
        Action<string, string, bool> consider,
        string firefoxRoot)
    {
        if (!safeFs.FileExists(path))
        {
            return;
        }

        string text;
        try
        {
            text = safeFs.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (FirefoxProfileRecord record in ParseProfiles(text))
        {
            if (TryResolve(firefoxRoot, record.Path, record.IsRelative, out string directory))
            {
                consider(directory, record.Name, record.IsDefault);
            }
        }

        foreach (string defaultPath in ParseInstallDefaults(text))
        {
            if (TryResolve(firefoxRoot, defaultPath, isRelative: null, out string directory))
            {
                consider(directory, Path.GetFileName(directory.Replace('/', '\\')), true);
            }
        }
    }

    private static bool TryResolve(string firefoxRoot, string path, bool? isRelative, out string directory)
    {
        directory = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string normalized = path.Replace('/', '\\').Trim();
        try
        {
            directory = Path.IsPathRooted(normalized) && isRelative != true
                ? Path.GetFullPath(normalized)
                : Path.GetFullPath(Path.Combine(firefoxRoot, normalized));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    private static bool IsUnder(string root, string candidate)
    {
        try
        {
            string normalizedRoot = PathCanonicalizer.NormalizeLexically(Path.GetFullPath(root));
            string normalizedCandidate = PathCanonicalizer.NormalizeLexically(Path.GetFullPath(candidate));
            string prefix = Path.TrimEndingDirectorySeparator(normalizedRoot) + Path.DirectorySeparatorChar;
            return normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    private static List<(string Section, Dictionary<string, string> Values)> ParseSections(string text)
    {
        List<(string Section, Dictionary<string, string> Values)> sections = [];
        string current = string.Empty;
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in text.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']' && line.Length >= 2)
            {
                if (current.Length > 0)
                {
                    sections.Add((current, values));
                }

                current = line[1..^1].Trim();
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            if (current.Length == 0)
            {
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        if (current.Length > 0)
        {
            sections.Add((current, values));
        }

        return sections;
    }

    private static bool IsProfileSection(string section) =>
        section.StartsWith("Profile", StringComparison.OrdinalIgnoreCase);

    private static bool IsTruthy(string? value) =>
        value is "1" ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
}
