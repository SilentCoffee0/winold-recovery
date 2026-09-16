using WinOldRecovery.Core.Persistence;

namespace WinOldRecovery.Core.Recipes;

public sealed class RecipeIndex
{
    private readonly SessionDb sessionDb;
    private readonly string sessionId;
    private readonly string profileRelPrefix;
    private readonly string oldProfileRoot;
    private readonly string sourceRoot;

    public RecipeIndex(
        SessionDb sessionDb,
        string sessionId,
        string profileRelPrefix,
        string oldProfileRoot)
    {
        this.sessionDb = sessionDb ?? throw new ArgumentNullException(nameof(sessionDb));
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        this.sessionId = sessionId;
        this.profileRelPrefix = profileRelPrefix ?? string.Empty;
        ArgumentException.ThrowIfNullOrWhiteSpace(oldProfileRoot);
        this.oldProfileRoot = oldProfileRoot;
        sourceRoot = ResolveSourceRoot(oldProfileRoot, this.profileRelPrefix);
    }

    public IReadOnlyList<string> FilesWithExtensions(params string[] extensions)
    {
        return FilesWithExtensions(extensions, skipAppData: true);
    }

    public IReadOnlyList<string> FilesWithExtensions(IReadOnlyList<string> extensions, bool skipAppData)
    {
        return FilesWithExtensions(extensions, skipAppData, relativeUnderProfile: null);
    }

    public IReadOnlyList<string> FilesWithExtensions(
        IReadOnlyList<string> extensions,
        bool skipAppData,
        string? relativeUnderProfile)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        List<string> paths = [];
        foreach (string relPath in sessionDb.ListFileRelPathsUnderPrefixByExtension(
                     sessionId,
                     CombinePrefix(profileRelPrefix, relativeUnderProfile),
                     extensions,
                     skipAppData))
        {
            if (TryAbsolute(relPath, out string absolute))
            {
                paths.Add(absolute);
            }
        }

        return paths;
    }

    public int CountFilesUnder(string relativeUnderProfile, string? excludeFolderName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeUnderProfile);
        return sessionDb.CountFilesUnderRelPrefix(
            sessionId,
            CombinePrefix(profileRelPrefix, relativeUnderProfile),
            excludeFolderName);
    }

    public int CountChildDirectories(string relativeUnderProfile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeUnderProfile);
        return sessionDb.CountChildDirectories(
            sessionId,
            CombinePrefix(profileRelPrefix, relativeUnderProfile));
    }

    public IReadOnlyList<string> FilesNamed(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        List<string> paths = [];
        foreach (string relPath in sessionDb.ListFileRelPathsByExactNames(sessionId, profileRelPrefix, names))
        {
            if (TryAbsolute(relPath, out string absolute))
            {
                paths.Add(absolute);
            }
        }

        return paths;
    }

    public IReadOnlyList<string> FilesUnder(string relativeUnderProfile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeUnderProfile);
        List<string> paths = [];
        foreach (string relPath in sessionDb.ListFileRelPathsUnderRelPrefix(
                     sessionId,
                     CombinePrefix(profileRelPrefix, relativeUnderProfile)))
        {
            if (TryAbsolute(relPath, out string absolute))
            {
                paths.Add(absolute);
            }
        }

        return paths;
    }

    public IReadOnlyList<string> FilesUnderSource(string relativeUnderSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeUnderSource);
        string prefix = relativeUnderSource.Replace('/', '\\').Trim('\\');
        List<string> paths = [];
        foreach (string relPath in sessionDb.ListFileRelPathsUnderRelPrefix(sessionId, prefix))
        {
            if (TryAbsoluteFromSource(relPath, out string absolute))
            {
                paths.Add(absolute);
            }
        }

        return paths;
    }

    public IReadOnlyList<string> FilesNamedUnder(string relativeUnderProfile, params string[] names)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeUnderProfile);
        ArgumentNullException.ThrowIfNull(names);
        List<string> paths = [];
        foreach (string relPath in sessionDb.ListFileRelPathsByExactNames(
                     sessionId,
                     CombinePrefix(profileRelPrefix, relativeUnderProfile),
                     names))
        {
            if (TryAbsolute(relPath, out string absolute))
            {
                paths.Add(absolute);
            }
        }

        return paths;
    }

    public bool HasChildNamed(string relativeUnderProfile, string childName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeUnderProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(childName);
        return ParentsOfChildNamedUnderProfile(relativeUnderProfile, childName).Count > 0;
    }

    public IReadOnlyList<string> ParentsOfChildNamed(string childName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childName);
        List<string> paths = [];
        foreach (string relPath in sessionDb.ListRelPathsUnderPrefixByChildName(
                     sessionId,
                     profileRelPrefix,
                     childName))
        {
            if (TryAbsolute(relPath, out string absolute))
            {
                paths.Add(absolute);
            }
        }

        return paths;
    }

    public IReadOnlyList<string> ParentsOfChildNamedUnderProfile(string relativeUnderProfile, string childName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeUnderProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(childName);
        List<string> paths = [];
        foreach (string relPath in sessionDb.ListRelPathsUnderPrefixByChildName(
                     sessionId,
                     CombinePrefix(profileRelPrefix, relativeUnderProfile),
                     childName))
        {
            if (TryAbsolute(relPath, out string absolute))
            {
                paths.Add(absolute);
            }
        }

        return paths;
    }

    public IReadOnlyList<string> GitWorkingTrees() => ParentsOfChildNamed(".git");

    public bool AnyFileNamedStartingWith(string relativeUnderProfile, params string[] namePrefixes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeUnderProfile);
        ArgumentNullException.ThrowIfNull(namePrefixes);
        return sessionDb.AnyFileUnderPrefixWithNamePrefix(
            sessionId,
            CombinePrefix(profileRelPrefix, relativeUnderProfile),
            namePrefixes);
    }

    public IReadOnlyList<string> ParentsOfChildNamedUnder(string relativeUnderSource, string childName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeUnderSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(childName);
        string prefix = relativeUnderSource.Replace('/', '\\').Trim('\\');
        List<string> paths = [];
        foreach (string relPath in sessionDb.ListRelPathsUnderPrefixByChildName(sessionId, prefix, childName))
        {
            if (TryAbsoluteFromSource(relPath, out string absolute))
            {
                paths.Add(absolute);
            }
        }

        return paths;
    }

    private static string CombinePrefix(string profileRelPrefix, string? relativeUnderProfile)
    {
        string profile = (profileRelPrefix ?? string.Empty).Replace('/', '\\').Trim('\\');
        if (string.IsNullOrWhiteSpace(relativeUnderProfile))
        {
            return profile;
        }

        string extra = relativeUnderProfile.Replace('/', '\\').Trim('\\');
        return string.IsNullOrEmpty(profile) ? extra : profile + "\\" + extra;
    }

    private bool TryAbsolute(string nodeRelPath, out string absolute)
    {
        string prefix = profileRelPrefix.Replace('/', '\\').Trim('\\');
        string rel = nodeRelPath.Replace('/', '\\');
        string underProfile;
        if (string.IsNullOrEmpty(prefix))
        {
            underProfile = rel;
        }
        else if (rel.Equals(prefix, StringComparison.OrdinalIgnoreCase))
        {
            underProfile = string.Empty;
        }
        else if (rel.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
        {
            underProfile = rel[(prefix.Length + 1)..];
        }
        else
        {
            absolute = string.Empty;
            return false;
        }

        absolute = string.IsNullOrEmpty(underProfile)
            ? oldProfileRoot
            : Path.Combine(oldProfileRoot, underProfile);
        return true;
    }

    private bool TryAbsoluteFromSource(string nodeRelPath, out string absolute)
    {
        string rel = nodeRelPath.Replace('/', '\\').Trim('\\');
        absolute = string.IsNullOrEmpty(rel) ? sourceRoot : Path.Combine(sourceRoot, rel);
        return true;
    }

    private static string ResolveSourceRoot(string oldProfileRoot, string profileRelPrefix)
    {
        string profile = oldProfileRoot.Replace('/', '\\').TrimEnd('\\');
        string prefix = (profileRelPrefix ?? string.Empty).Replace('/', '\\').Trim('\\');
        if (!string.IsNullOrEmpty(prefix) &&
            profile.EndsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            profile.Length > prefix.Length)
        {
            return profile[..^prefix.Length].TrimEnd('\\');
        }

        return profile;
    }
}
