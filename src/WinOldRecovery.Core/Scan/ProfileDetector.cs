using System.IO.Enumeration;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Native;

namespace WinOldRecovery.Core.Scan;

public sealed class ProfileDetector
{
    public static readonly IReadOnlyList<string> StandardFolderNames =
    [
        "Desktop",
        "Documents",
        "Downloads",
        "Pictures",
        "Videos",
        "Music",
        "Saved Games",
        "Favorites",
        "Contacts",
        "Links",
        "Searches",
    ];

    public static readonly IReadOnlyList<string> LegacyJunctionNames =
    [
        "Application Data",
        "Local Settings",
        "My Documents",
        "Cookies",
        "NetHood",
        "PrintHood",
        "Recent",
        "SendTo",
        "Start Menu",
        "Templates",
    ];

    private static readonly HashSet<string> SkippedProfileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Default",
        "Default User",
        "All Users",
    };

    private static readonly Dictionary<string, string> ShellFolderValueNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Desktop"] = "Desktop",
            ["{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}"] = "Desktop",
            ["Personal"] = "Documents",
            ["{F42EE2D3-909F-4907-8871-4C22FC0BF756}"] = "Documents",
            ["{FDD39AD0-238F-46AF-ADB4-6C85480369C7}"] = "Documents",
            ["My Pictures"] = "Pictures",
            ["{0DDD015D-B06C-45D5-8C4C-F59713854639}"] = "Pictures",
            ["My Video"] = "Videos",
            ["{35286A68-3C57-41A1-BBB1-0EAE73D76C95}"] = "Videos",
            ["My Music"] = "Music",
            ["{A0C69A99-21C8-4671-8703-7934162FCF1D}"] = "Music",
            ["{374DE290-123F-4565-9164-39C4925E467B}"] = "Downloads",
            ["{4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4}"] = "Saved Games",
            ["Favorites"] = "Favorites",
            ["{56784854-C6CB-462B-8169-88E350ACB882}"] = "Contacts",
            ["{BFB9D5E0-C6A9-404C-B2B2-AE6DB6AF4968}"] = "Links",
            ["{7D1D3A04-DEBB-4115-95CF-2F29DA2920DA}"] = "Searches",
        };

    private readonly IShellFolderValueSource? shellFolderValueSource;

    public ProfileDetector(IShellFolderValueSource? shellFolderValueSource = null)
    {
        this.shellFolderValueSource = shellFolderValueSource;
    }

    public IReadOnlyList<DetectedProfile> Detect(string sourceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        string root = PathCanonicalizer.NormalizeLexically(sourceRoot);
        string users = Path.Combine(root, "Users");
        if (!Directory.Exists(users))
        {
            return [];
        }

        List<DetectedProfile> profiles = [];
        foreach (string entryName in EnumerateTopLevelNames(users))
        {
            if (SkippedProfileNames.Contains(entryName))
            {
                continue;
            }

            string profilePath = Path.Combine(users, entryName);
            FileAttributes attributes = TryGetAttributes(profilePath);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            if (!LooksLikeProfile(profilePath, entryName))
            {
                continue;
            }

            profiles.Add(BuildProfile(root, entryName, profilePath));
        }

        return profiles
            .OrderBy(static profile => KindOrder(profile.Kind))
            .ThenBy(static profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private DetectedProfile BuildProfile(string sourceRoot, string name, string profilePath)
    {
        ProfileKind kind = ClassifyKind(name, profilePath);
        string displayName = kind == ProfileKind.PublicShared ? "Public (shared)" : name;
        string relativePath = CombineRelative("Users", name);
        DateTimeOffset? lastUsed = TryGetLastWriteTimeUtc(Path.Combine(profilePath, "NTUSER.DAT"));

        Dictionary<string, StandardFolderMatch> folders = new(StringComparer.OrdinalIgnoreCase);
        foreach (string folderName in StandardFolderNames)
        {
            string candidate = Path.Combine(profilePath, folderName);
            bool present = Directory.Exists(candidate) &&
                (TryGetAttributes(candidate) & FileAttributes.ReparsePoint) == 0;
            folders[folderName] = new StandardFolderMatch(
                folderName,
                present ? CombineRelative(relativePath, folderName) : null,
                RedirectedAbsolutePath: null,
                present);
        }

        ApplyShellFolderRedirects(sourceRoot, name, profilePath, relativePath, folders);

        List<string> customFolders = [];
        List<LegacyJunctionMatch> legacyJunctions = [];
        foreach (string entryName in EnumerateTopLevelNames(profilePath))
        {
            string childPath = Path.Combine(profilePath, entryName);
            FileAttributes attributes = TryGetAttributes(childPath);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                continue;
            }

            if (LegacyJunctionNames.Contains(entryName, StringComparer.OrdinalIgnoreCase))
            {
                string? target = null;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    try
                    {
                        target = ReparsePoint.Read(PathCanonicalizer.ToExtendedPath(childPath)).Target;
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException)
                    {
                    }
                }

                legacyJunctions.Add(new LegacyJunctionMatch(entryName, target));
                continue;
            }

            if (folders.ContainsKey(entryName) ||
                entryName.Equals("AppData", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            customFolders.Add(entryName);
        }

        return new DetectedProfile(
            name,
            displayName,
            profilePath,
            relativePath,
            kind,
            lastUsed,
            folders.Values.OrderBy(static folder => folder.KnownName, StringComparer.OrdinalIgnoreCase).ToArray(),
            customFolders.OrderBy(static folder => folder, StringComparer.OrdinalIgnoreCase).ToArray(),
            legacyJunctions);
    }

    private void ApplyShellFolderRedirects(
        string sourceRoot,
        string profileName,
        string profilePath,
        string relativePath,
        Dictionary<string, StandardFolderMatch> folders)
    {
        if (shellFolderValueSource is null)
        {
            return;
        }

        IReadOnlyDictionary<string, string> values = shellFolderValueSource.GetValues(profilePath);
        foreach ((string valueName, string rawValue) in values)
        {
            if (!ShellFolderValueNames.TryGetValue(valueName, out string? knownName))
            {
                continue;
            }

            string expanded = ExpandProfileVariables(rawValue, profilePath, profileName);
            bool insideSource = IsInsideSource(sourceRoot, expanded);
            string? relative = insideSource
                ? Path.GetRelativePath(sourceRoot, PathCanonicalizer.NormalizeLexically(expanded))
                : null;
            bool present = insideSource &&
                Directory.Exists(PathCanonicalizer.ToExtendedPath(expanded));
            if (!insideSource)
            {
                folders[knownName] = new StandardFolderMatch(
                    knownName,
                    RelativePathInSource: null,
                    expanded,
                    PresentInSource: false);
                continue;
            }

            if (present)
            {
                folders[knownName] = new StandardFolderMatch(
                    knownName,
                    relative?.Replace('/', '\\'),
                    RedirectedAbsolutePath: null,
                    PresentInSource: true);
            }
        }
    }

    private static bool LooksLikeProfile(string profilePath, string name)
    {
        if (name.Equals("Public", StringComparison.OrdinalIgnoreCase) ||
            IsServiceAccountName(name))
        {
            return true;
        }

        if (File.Exists(Path.Combine(profilePath, "NTUSER.DAT")))
        {
            return true;
        }

        return StandardFolderNames.Any(folder => Directory.Exists(Path.Combine(profilePath, folder)));
    }

    private static ProfileKind ClassifyKind(string name, string profilePath)
    {
        if (name.Equals("Public", StringComparison.OrdinalIgnoreCase))
        {
            return ProfileKind.PublicShared;
        }

        if (IsServiceAccountName(name) || !File.Exists(Path.Combine(profilePath, "NTUSER.DAT")))
        {
            return ProfileKind.Service;
        }

        return ProfileKind.Human;
    }

    internal static bool IsServiceAccountName(string name)
    {
        if (name.Contains("ServiceAcct", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("MSSQL", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("defaultuser0", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("DefaultAccount", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static int KindOrder(ProfileKind kind)
    {
        return kind switch
        {
            ProfileKind.Human => 0,
            ProfileKind.PublicShared => 1,
            _ => 2,
        };
    }

    private static IEnumerable<string> EnumerateTopLevelNames(string directory)
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
        };

        foreach (string path in Directory.EnumerateFileSystemEntries(directory, "*", options))
        {
            yield return Path.GetFileName(path);
        }
    }

    private static FileAttributes TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(PathCanonicalizer.ToExtendedPath(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static DateTimeOffset? TryGetLastWriteTimeUtc(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ExpandProfileVariables(string rawValue, string profilePath, string profileName)
    {
        return rawValue
            .Replace("%USERPROFILE%", profilePath, StringComparison.OrdinalIgnoreCase)
            .Replace("%USERNAME%", profileName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInsideSource(string sourceRoot, string candidate)
    {
        try
        {
            string normalizedRoot = PathCanonicalizer.NormalizeLexically(sourceRoot);
            string normalizedCandidate = PathCanonicalizer.NormalizeLexically(candidate);
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

    private static string CombineRelative(string parent, string name)
    {
        return string.IsNullOrEmpty(parent) ? name : parent + "\\" + name;
    }
}
