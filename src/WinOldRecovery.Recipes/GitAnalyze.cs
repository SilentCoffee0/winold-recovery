using System.ComponentModel;
using WinOldRecovery.Core.Processes;

namespace WinOldRecovery.Recipes;

public static class GitAnalyze
{
    public static IReadOnlyList<ProcessRequest> CreateRequests(
        string repositoryPath,
        string destinationHome,
        string? gitExecutable = null)
    {
        Dictionary<string, string?> environment = new(StringComparer.OrdinalIgnoreCase)
        {
            ["GIT_OPTIONAL_LOCKS"] = "0",
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["HOME"] = destinationHome,
        };
        string[] prefix =
        [
            "--no-optional-locks",
            "-c", "safe.directory=*",
            "-c", "core.fsmonitor=false",
            "-c", "gc.auto=0",
            "-c", "maintenance.auto=false",
            "-C", repositoryPath,
        ];
        TimeSpan timeout = TimeSpan.FromSeconds(60);
        string fileName = string.IsNullOrWhiteSpace(gitExecutable) ? "git.exe" : gitExecutable;
        return
        [
            Request(fileName, [.. prefix, "status", "--porcelain=v2", "--branch", "--show-stash", "--untracked-files=all", "--ignored=no"], environment, timeout),
            Request(fileName, [.. prefix, "for-each-ref", "--format=%(refname:short)%09%(upstream:short)%09%(upstream:track)", "refs/heads"], environment, timeout),
            Request(fileName, [.. prefix, "log", "--branches", "--not", "--remotes", "--format=%H%x09%s"], environment, timeout),
            Request(fileName, [.. prefix, "stash", "list", "--format=%gd%x09%s"], environment, timeout),
            Request(fileName, [.. prefix, "worktree", "list", "--porcelain"], environment, timeout),
        ];
    }

    public static string ResolveGitExecutable()
    {
        foreach (string candidate in GitInstallCandidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "git.exe";
    }

    public static async Task<GitAnalyzeResult> AnalyzeAsync(
        IProcessRunner processRunner,
        string repositoryPath,
        string destinationHome,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ProcessRequest> requests = CreateRequests(
            repositoryPath,
            destinationHome,
            ResolveGitExecutable());
        string[] stdout = new string[5];
        bool anyOk = false;
        int index = 0;
        foreach (ProcessRequest request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessResult result;
            try
            {
                result = await processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Win32Exception)
            {
                result = new ProcessResult(1, string.Empty, string.Empty);
            }
            catch (InvalidOperationException)
            {
                result = new ProcessResult(1, string.Empty, string.Empty);
            }

            if (index < stdout.Length)
            {
                stdout[index] = result.StandardOutput ?? string.Empty;
            }

            if (result.ExitCode == 0)
            {
                anyOk = true;
            }

            index++;
        }

        if (!anyOk)
        {
            return GitAnalyzeResult.Unknown;
        }

        return Interpret(stdout[0], stdout[1], stdout[2], stdout[3]);
    }

    public static GitAnalyzeResult Interpret(
        string statusStdout,
        string forEachRefStdout,
        string unpushedLogStdout,
        string stashListStdout)
    {
        bool uncommitted = false;
        bool untracked = false;
        bool stashFromStatus = false;
        bool hasRemoteFromStatus = false;
        foreach (string raw in SplitLines(statusStdout))
        {
            if (raw.StartsWith("1 ", StringComparison.Ordinal) ||
                raw.StartsWith("2 ", StringComparison.Ordinal) ||
                raw.StartsWith("u ", StringComparison.Ordinal))
            {
                uncommitted = true;
            }
            else if (raw.StartsWith("? ", StringComparison.Ordinal))
            {
                untracked = true;
            }
            else if (raw.StartsWith("# stash ", StringComparison.Ordinal))
            {
                string rest = raw["# stash ".Length..].Trim();
                stashFromStatus = int.TryParse(rest, out int count) && count > 0;
            }
            else if (raw.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                hasRemoteFromStatus = raw.Length > "# branch.upstream ".Length;
            }
        }

        bool hasRemote = hasRemoteFromStatus;
        bool localOnlyBranch = false;
        foreach (string raw in SplitLines(forEachRefStdout))
        {
            string[] parts = raw.Split('\t');
            string upstream = parts.Length > 1 ? parts[1] : string.Empty;
            string track = parts.Length > 2 ? parts[2] : string.Empty;
            if (!string.IsNullOrWhiteSpace(upstream))
            {
                hasRemote = true;
            }

            if (string.IsNullOrWhiteSpace(upstream) ||
                track.Contains("[gone]", StringComparison.OrdinalIgnoreCase))
            {
                localOnlyBranch = true;
            }
        }

        bool unpushed = SplitLines(unpushedLogStdout).Any(static line => line.Length > 0);
        bool stash = stashFromStatus || SplitLines(stashListStdout).Any(static line => line.Length > 0);
        return GitAnalyzeResult.FromFlags(uncommitted, untracked, unpushed, stash, localOnlyBranch, hasRemote);
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string trimmed = line.TrimEnd();
            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }
        }
    }

    private static IEnumerable<string> GitInstallCandidates()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Git",
            "cmd",
            "git.exe");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Git",
            "cmd",
            "git.exe");
        string? install = null;
        try
        {
            using Microsoft.Win32.RegistryKey? key =
                Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\GitForWindows");
            install = key?.GetValue("InstallPath") as string;
        }
        catch (System.Security.SecurityException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        if (!string.IsNullOrWhiteSpace(install))
        {
            yield return Path.Combine(install, "cmd", "git.exe");
        }
    }

    private static ProcessRequest Request(
        string fileName,
        string[] arguments,
        Dictionary<string, string?> environment,
        TimeSpan timeout)
    {
        return new ProcessRequest(fileName, arguments, Environment: environment, Timeout: timeout);
    }
}

public sealed record GitAnalyzeResult(
    bool Uncommitted,
    bool Untracked,
    bool Unpushed,
    bool Stash,
    bool LocalOnlyBranch,
    bool HasRemote,
    string Badge)
{
    public static GitAnalyzeResult Unknown { get; } = FromFlags(
        uncommitted: false,
        untracked: false,
        unpushed: false,
        stash: false,
        localOnlyBranch: false,
        hasRemote: false,
        unknown: true);

    public static GitAnalyzeResult FromFlags(
        bool uncommitted,
        bool untracked,
        bool unpushed,
        bool stash,
        bool localOnlyBranch,
        bool hasRemote,
        bool unknown = false)
    {
        string badge;
        if (unknown)
        {
            badge = "Git: local-only work";
        }
        else if (!hasRemote)
        {
            badge = "Git: no remote";
        }
        else if (uncommitted || untracked || unpushed || stash || localOnlyBranch)
        {
            badge = "Git: local-only work";
        }
        else
        {
            badge = "Git: clean, pushed";
        }

        return new GitAnalyzeResult(
            uncommitted,
            untracked,
            unpushed,
            stash,
            localOnlyBranch,
            hasRemote,
            badge);
    }
}
