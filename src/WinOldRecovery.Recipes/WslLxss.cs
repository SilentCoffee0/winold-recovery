namespace WinOldRecovery.Recipes;

internal static class WslLxss
{
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    public sealed record Distro(
        string Guid,
        string Name,
        string BasePath,
        string MappedBasePath,
        string Version,
        string DefaultUid,
        string PackageFamilyName,
        bool IsDefault);

    public static IReadOnlyList<Distro> Read(
        IReadOnlyDictionary<string, string> rootValues,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> children,
        string oldProfileRoot,
        string profileName)
    {
        string defaultGuid = rootValues.GetValueOrDefault("DefaultDistribution") ?? string.Empty;
        List<Distro> distros = [];
        foreach ((string guid, IReadOnlyDictionary<string, string> values) in children)
        {
            string name = values.GetValueOrDefault("DistributionName") ?? string.Empty;
            string basePath = values.GetValueOrDefault("BasePath") ?? string.Empty;
            distros.Add(
                new Distro(
                    guid,
                    name,
                    basePath,
                    MapBasePath(basePath, oldProfileRoot, profileName),
                    values.GetValueOrDefault("Version") ?? string.Empty,
                    values.GetValueOrDefault("DefaultUid") ?? string.Empty,
                    values.GetValueOrDefault("PackageFamilyName") ?? string.Empty,
                    GuidEquals(guid, defaultGuid)));
        }

        return distros;
    }

    public static Distro? Match(string disk, IReadOnlyList<Distro> distros)
    {
        string full = Path.GetFullPath(disk);
        foreach (Distro distro in distros)
        {
            if (ContainsIgnoreCase(full, distro.Guid) ||
                (distro.PackageFamilyName.Length > 0 && ContainsIgnoreCase(full, distro.PackageFamilyName)))
            {
                return distro;
            }

            if (distro.MappedBasePath.Length == 0)
            {
                continue;
            }

            string mapped = Path.GetFullPath(distro.MappedBasePath);
            if (full.StartsWith(mapped.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetDirectoryName(full), mapped, StringComparison.OrdinalIgnoreCase))
            {
                return distro;
            }
        }

        return null;
    }

    internal static string MapBasePath(string basePath, string oldProfileRoot, string profileName)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return string.Empty;
        }

        string path = basePath;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            path = path[4..];
        }

        string marker = @"\Users\" + profileName + @"\";
        int index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return path;
        }

        return Path.GetFullPath(Path.Combine(oldProfileRoot, path[(index + marker.Length)..]));
    }

    private static bool GuidEquals(string left, string right)
    {
        return left.Trim('{', '}').Equals(right.Trim('{', '}'), StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsIgnoreCase(string haystack, string needle)
    {
        return needle.Length > 0 && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }
}
