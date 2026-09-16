using WinOldRecovery.Core.Persistence;

namespace WinOldRecovery.Core.Recipes;

public sealed class RecipeIndex
{
    private readonly SessionDb sessionDb;
    private readonly string sessionId;
    private readonly string profileRelPrefix;
    private readonly string oldProfileRoot;

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
    }

    public IReadOnlyList<string> FilesWithExtensions(params string[] extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        List<string> paths = [];
        foreach (string relPath in sessionDb.ListFileRelPathsUnderPrefixByExtension(
                     sessionId,
                     profileRelPrefix,
                     extensions))
        {
            if (TryAbsolute(relPath, out string absolute))
            {
                paths.Add(absolute);
            }
        }

        return paths;
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

    public IReadOnlyList<string> GitWorkingTrees() => ParentsOfChildNamed(".git");

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
}
