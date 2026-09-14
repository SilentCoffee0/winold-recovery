using WinOldRecovery.Core.IO;

namespace WinOldRecovery.Recipes;

public static class GitOffline
{
    public const string UnknownUntilGit = "unknown (install Git to analyze)";

    public static GitOfflineResult Analyze(SafeFs safeFs, string? gitDir)
    {
        ArgumentNullException.ThrowIfNull(safeFs);
        if (string.IsNullOrWhiteSpace(gitDir) ||
            (!safeFs.DirectoryExists(gitDir) && !safeFs.FileExists(gitDir)))
        {
            return GitOfflineResult.Unknown;
        }

        string head = ReadText(safeFs, Path.Combine(gitDir, "HEAD")).Trim();
        string current = CurrentBranch(head);
        GitConfigSnapshot config = ParseConfig(ReadText(safeFs, Path.Combine(gitDir, "config")));
        bool reftable = safeFs.DirectoryExists(Path.Combine(gitDir, "reftable"));
        DateTimeOffset? lastActivity = ReadReflogTime(safeFs, Path.Combine(gitDir, "logs", "HEAD"));
        DateTimeOffset? indexMtime = ReadMtimeUtc(Path.Combine(gitDir, "index"));

        if (reftable)
        {
            bool hasRemote = config.Remotes.Count > 0;
            return new GitOfflineResult(
                head,
                current,
                [],
                config.Remotes,
                hasRemote,
                LocalOnlyBranch: false,
                Stash: false,
                Reftable: true,
                lastActivity,
                indexMtime,
                Badge(hasRemote));
        }

        HashSet<string> heads = new(StringComparer.Ordinal);
        HashSet<string> remoteRefs = new(StringComparer.Ordinal);
        bool stash = safeFs.FileExists(Path.Combine(gitDir, "refs", "stash"));
        CollectLooseRefs(safeFs, Path.Combine(gitDir, "refs", "heads"), heads);
        CollectLooseRefs(safeFs, Path.Combine(gitDir, "refs", "remotes"), remoteRefs);
        ParsePackedRefs(
            ReadText(safeFs, Path.Combine(gitDir, "packed-refs")),
            heads,
            remoteRefs,
            ref stash);

        bool hasTrackingOrRemote = config.Remotes.Count > 0 || remoteRefs.Count > 0;
        bool localOnly = false;
        foreach (string branch in heads)
        {
            if (!config.BranchRemote.ContainsKey(branch) && !HasRemoteTracking(branch, remoteRefs))
            {
                localOnly = true;
                break;
            }
        }

        List<string> branches = [.. heads.OrderBy(static name => name, StringComparer.Ordinal)];
        return new GitOfflineResult(
            head,
            current,
            branches,
            config.Remotes,
            hasTrackingOrRemote,
            localOnly,
            stash,
            Reftable: false,
            lastActivity,
            indexMtime,
            Badge(hasTrackingOrRemote));
    }

    public static string StripRemoteUrl(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        int scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0)
        {
            return url;
        }

        int start = scheme + 3;
        int at = url.IndexOf('@', start);
        int slash = url.IndexOf('/', start);
        if (at > start && (slash < 0 || at < slash))
        {
            return url[..start] + url[(at + 1)..];
        }

        return url;
    }

    private static string Badge(bool hasRemote)
    {
        return hasRemote ? "Git: local-only work" : "Git: no remote";
    }

    private static string CurrentBranch(string head)
    {
        const string prefix = "ref: ";
        if (head.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            string target = head[prefix.Length..].Trim();
            const string heads = "refs/heads/";
            return target.StartsWith(heads, StringComparison.OrdinalIgnoreCase)
                ? target[heads.Length..]
                : target;
        }

        return string.IsNullOrEmpty(head) ? "unknown" : head;
    }

    private static void CollectLooseRefs(SafeFs safeFs, string root, HashSet<string> names)
    {
        if (!safeFs.DirectoryExists(root) || DetectorWalk.IsReparse(root))
        {
            return;
        }

        Stack<string> stack = new();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string directory = stack.Pop();
            IReadOnlyList<string> entries;
            try
            {
                entries = safeFs.EnumerateFileSystemEntries(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string entry in entries)
            {
                if (DetectorWalk.IsReparse(entry))
                {
                    continue;
                }

                if (safeFs.DirectoryExists(entry))
                {
                    stack.Push(entry);
                    continue;
                }

                if (!safeFs.FileExists(entry))
                {
                    continue;
                }

                string relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if (!string.IsNullOrWhiteSpace(relative) &&
                    !relative.StartsWith("..", StringComparison.Ordinal))
                {
                    names.Add(relative);
                }
            }
        }
    }

    private static void ParsePackedRefs(
        string text,
        HashSet<string> heads,
        HashSet<string> remoteRefs,
        ref bool stash)
    {
        foreach (string line in SplitLines(text))
        {
            if (line.StartsWith('#') || line.StartsWith('^'))
            {
                continue;
            }

            string[] parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            string name = parts[1].Trim();
            if (name.Equals("refs/stash", StringComparison.Ordinal))
            {
                stash = true;
            }
            else if (name.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                heads.Add(name["refs/heads/".Length..]);
            }
            else if (name.StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                remoteRefs.Add(name["refs/remotes/".Length..]);
            }
        }
    }

    private static bool HasRemoteTracking(string branch, HashSet<string> remoteRefs)
    {
        foreach (string remoteRef in remoteRefs)
        {
            int slash = remoteRef.IndexOf('/');
            if (slash < 0)
            {
                continue;
            }

            if (remoteRef[(slash + 1)..].Equals(branch, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static GitConfigSnapshot ParseConfig(string text)
    {
        List<GitOfflineRemote> remotes = [];
        Dictionary<string, string> branchRemote = new(StringComparer.Ordinal);
        string? section = null;
        string? subsection = null;
        foreach (string raw in SplitLines(text))
        {
            if (raw.StartsWith('[') && raw.EndsWith(']'))
            {
                string inner = raw[1..^1].Trim();
                int quote = inner.IndexOf('"');
                if (quote >= 0)
                {
                    section = inner[..quote].Trim();
                    int end = inner.LastIndexOf('"');
                    subsection = end > quote ? inner[(quote + 1)..end] : null;
                }
                else
                {
                    section = inner;
                    subsection = null;
                }

                continue;
            }

            int equals = raw.IndexOf('=');
            if (equals < 0 || section is null)
            {
                continue;
            }

            string key = raw[..equals].Trim();
            string value = raw[(equals + 1)..].Trim().Trim('"');
            if (section.Equals("remote", StringComparison.OrdinalIgnoreCase) &&
                subsection is not null &&
                key.Equals("url", StringComparison.OrdinalIgnoreCase))
            {
                remotes.Add(new GitOfflineRemote(subsection, StripRemoteUrl(value)));
            }
            else if (section.Equals("branch", StringComparison.OrdinalIgnoreCase) &&
                subsection is not null &&
                key.Equals("remote", StringComparison.OrdinalIgnoreCase))
            {
                branchRemote[subsection] = value;
            }
        }

        return new GitConfigSnapshot(remotes, branchRemote);
    }

    private static DateTimeOffset? ReadReflogTime(SafeFs safeFs, string path)
    {
        string text = ReadText(safeFs, path);
        string? last = null;
        foreach (string line in SplitLines(text))
        {
            last = line;
        }

        if (last is null)
        {
            return ReadMtimeUtc(path);
        }

        int tab = last.IndexOf('\t');
        string meta = tab < 0 ? last : last[..tab];
        string[] bits = meta.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (bits.Length >= 2 &&
            long.TryParse(bits[^2], out long unix) &&
            unix is > 1_000_000_000 and < 4_000_000_000)
        {
            return DateTimeOffset.FromUnixTimeSeconds(unix);
        }

        return ReadMtimeUtc(path);
    }

    private static DateTimeOffset? ReadMtimeUtc(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ReadText(SafeFs safeFs, string path)
    {
        try
        {
            return safeFs.FileExists(path) ? safeFs.ReadAllText(path) : string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }
        }
    }

    private sealed record GitConfigSnapshot(
        IReadOnlyList<GitOfflineRemote> Remotes,
        Dictionary<string, string> BranchRemote);
}

public sealed record GitOfflineRemote(string Name, string Url);

public sealed record GitOfflineResult(
    string Head,
    string CurrentBranch,
    IReadOnlyList<string> Branches,
    IReadOnlyList<GitOfflineRemote> Remotes,
    bool HasRemote,
    bool LocalOnlyBranch,
    bool Stash,
    bool Reftable,
    DateTimeOffset? LastActivity,
    DateTimeOffset? IndexMtime,
    string Badge)
{
    public static GitOfflineResult Unknown { get; } = new(
        "unknown",
        "unknown",
        [],
        [],
        HasRemote: false,
        LocalOnlyBranch: false,
        Stash: false,
        Reftable: false,
        LastActivity: null,
        IndexMtime: null,
        "Git: local-only work");
}
