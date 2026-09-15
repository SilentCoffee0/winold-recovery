using System.Globalization;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Planning;
using WinOldRecovery.Core.Safety;

namespace WinOldRecovery.Core.Restore;

public static class PublishedRestoreProbe
{
    public static async Task<string> RunAsync(
        SessionDb sessionDb,
        SafeFs safeFs,
        SourceGuard sourceGuard,
        string sessionId,
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionDb);
        ArgumentNullException.ThrowIfNull(safeFs);
        ArgumentNullException.ThrowIfNull(sourceGuard);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        string source = sourceGuard.RegisterSourceRoot(Path.GetFullPath(sourceRoot));
        string destination = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(destination);

        IReadOnlyList<PlanItem> existing = sessionDb.ListPlanItems(sessionId);
        IReadOnlyList<PlanItem> items;
        if (existing.Count > 0)
        {
            items = existing;
        }
        else
        {
            long bytes = Directory.Exists(source)
                ? Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                    .Sum(path => new FileInfo(path).Length)
                : 0;
            items = await sessionDb.ReplacePlanItemsAsync(
                    sessionId,
                    [
                        new PlanItem(
                            sessionId,
                            1,
                            PlanOperation.CopyTree,
                            source,
                            destination,
                            bytes,
                            ConflictPolicy.KeepBoth,
                            OverwriteApproved: false,
                            RecipeId: null),
                    ],
                    cancellationToken)
                .ConfigureAwait(false);
            await sessionDb.SetKvAsync(sessionId, InterruptedRestore.SourceRootKey, source, cancellationToken)
                .ConfigureAwait(false);
            await sessionDb.SetKvAsync(
                    sessionId,
                    InterruptedRestore.DestinationRootKey,
                    destination,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        RestorePlan plan = new(
            sessionId,
            source,
            destination,
            items,
            items.Sum(static item => item.Bytes));
        RestoreResult result = await new RestoreRunner(new CopyEngine(sessionDb, safeFs), sessionDb)
            .RunAsync(plan, cancellationToken)
            .ConfigureAwait(false);

        int destFiles = Directory.Exists(destination)
            ? Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).Count()
            : 0;
        int partials = Directory.Exists(destination)
            ? Directory.EnumerateFiles(
                    destination,
                    "*" + CopyEngine.PartialSuffix,
                    SearchOption.AllDirectories)
                .Count()
            : 0;
        bool passed = result.Completed && partials == 0;
        return string.Join(
            Environment.NewLine,
            [
                "Passed: " + (passed ? "true" : "false"),
                "Completed: " + result.Completed,
                "PausedDiskFull: " + result.PausedDiskFull,
                "PausedByUser: " + result.PausedByUser,
                "DestinationFiles: " + destFiles.ToString(CultureInfo.InvariantCulture),
                "PartialFiles: " + partials.ToString(CultureInfo.InvariantCulture),
                "Source: " + source,
                "Destination: " + destination,
            ]);
    }
}
