using WinOldRecovery.Core.Persistence;

namespace WinOldRecovery.Core.Planning;

public static class PlanProgress
{
    public static RestorePlan Pending(SessionDb sessionDb, RestorePlan plan)
    {
        ArgumentNullException.ThrowIfNull(sessionDb);
        ArgumentNullException.ThrowIfNull(plan);

        List<PlanItem> pending = [];
        long bytes = 0;
        foreach (PlanItem item in plan.Items)
        {
            if (item.Id is long id)
            {
                string? state = sessionDb.GetLatestJournalState(id);
                if (state is "Completed" or "Skipped")
                {
                    continue;
                }
            }

            pending.Add(item);
            bytes += item.Bytes;
        }

        return plan with { Items = pending, TotalBytes = bytes };
    }
}
