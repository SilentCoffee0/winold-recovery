using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Restore;
using WinOldRecovery.Core.Safety;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: RestoreHarness <session.db> <session-id>");
    return 2;
}

SafeFs safeFs = new(new SourceGuard());
await using SessionDb database = await SessionDb.OpenAsync(args[0], safeFs);
CopyEngine engine = new(database, safeFs);
foreach (WinOldRecovery.Core.Planning.PlanItem item in database.ListPlanItems(args[1]))
{
    await engine.CopyAsync(item);
}

return 0;
