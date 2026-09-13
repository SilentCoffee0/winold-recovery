using WinOldRecovery.Core.Processes;

namespace WinOldRecovery.Recipes;

public static class GitAnalyze
{
    public static IReadOnlyList<ProcessRequest> CreateRequests(string repositoryPath, string destinationHome)
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
        return
        [
            Request([.. prefix, "status", "--porcelain=v2", "--branch", "--show-stash", "--untracked-files=all", "--ignored=no"], environment, timeout),
            Request([.. prefix, "for-each-ref", "--format=%(refname:short)%09%(upstream:short)%09%(upstream:track)", "refs/heads"], environment, timeout),
            Request([.. prefix, "log", "--branches", "--not", "--remotes", "--format=%H%x09%s"], environment, timeout),
            Request([.. prefix, "stash", "list", "--format=%gd%x09%s"], environment, timeout),
            Request([.. prefix, "worktree", "list", "--porcelain"], environment, timeout),
        ];
    }

    public static async Task AnalyzeAsync(
        IProcessRunner processRunner,
        string repositoryPath,
        string destinationHome,
        CancellationToken cancellationToken = default)
    {
        foreach (ProcessRequest request in CreateRequests(repositoryPath, destinationHome))
        {
            await processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ProcessRequest Request(
        string[] arguments,
        Dictionary<string, string?> environment,
        TimeSpan timeout)
    {
        return new ProcessRequest("git.exe", arguments, Environment: environment, Timeout: timeout);
    }
}
