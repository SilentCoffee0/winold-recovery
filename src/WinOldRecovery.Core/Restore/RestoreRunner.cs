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
        CancellationToken cancellationToken = default)
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
        foreach (PlanItem item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                results.Add(await copyEngine.CopyAsync(item, cancellationToken).ConfigureAwait(false));
            }
            catch (RestorePausedException)
            {
                return new RestoreResult(false, true, results);
            }
        }

        return new RestoreResult(true, false, results);
    }
}
