using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Planning;

namespace WinOldRecovery.Core.Restore;

public sealed class RestoreRunner
{
    private readonly CopyEngine copyEngine;
    private readonly SessionDb? sessionDb;
    private readonly PreflightChecker preflightChecker;

    public RestoreRunner(
        CopyEngine copyEngine,
        SessionDb? sessionDb = null,
        PreflightChecker? preflightChecker = null)
    {
        this.copyEngine = copyEngine ?? throw new ArgumentNullException(nameof(copyEngine));
        this.sessionDb = sessionDb;
        this.preflightChecker = preflightChecker ?? new PreflightChecker();
    }

    public async Task<RestoreResult> RunAsync(
        RestorePlan plan,
        CancellationToken cancellationToken = default,
        IProgress<RestoreProgress>? progress = null,
        CancellationToken pauseToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        RestorePlan toCheck = sessionDb is null ? plan : PlanProgress.Pending(sessionDb, plan);
        PreflightResult preflight = preflightChecker.Check(toCheck);
        if (!preflight.CanProceed)
        {
            throw new InvalidOperationException(
                string.Join(Environment.NewLine, preflight.BlockingIssues));
        }

        List<RestoreItemResult> results = [];
        long completedBytes = 0;
        int index = 0;
        foreach (PlanItem item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(
                new RestoreProgress(
                    index,
                    plan.Items.Count,
                    completedBytes,
                    plan.TotalBytes,
                    Path.GetFileName(item.SourcePath)));
            try
            {
                RestoreItemResult result = await copyEngine.CopyAsync(item, cancellationToken, pauseToken)
                    .ConfigureAwait(false);
                results.Add(result);
                if (result.State is "Completed" or "Skipped")
                {
                    completedBytes += item.Bytes;
                }
            }
            catch (RestorePausedException paused)
            {
                return new RestoreResult(
                    false,
                    paused.IsDiskFull,
                    results,
                    paused.IsUserPause,
                    RestoreSkipCounts.FromResults(results));
            }

            index++;
            progress?.Report(
                new RestoreProgress(
                    index,
                    plan.Items.Count,
                    completedBytes,
                    plan.TotalBytes,
                    Path.GetFileName(item.SourcePath)));
        }

        return new RestoreResult(
            results.Count == plan.Items.Count &&
            results.All(static result => result.State is "Completed" or "Skipped"),
            false,
            results,
            Skips: RestoreSkipCounts.FromResults(results));
    }
}

public sealed record RestoreProgress(
    int CompletedItems,
    int TotalItems,
    long CompletedBytes,
    long TotalBytes,
    string CurrentName);
