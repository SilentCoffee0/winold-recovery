using System.Diagnostics;
using System.Globalization;
using WinOldRecovery.Core.Browse;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Safety;

namespace WinOldRecovery.Core.Scan;

public static class PublishedScanProbe
{
    public const long MemoryCeilingBytes = 1_500L * 1024 * 1024;

    public static async Task<string> RunAsync(
        SessionDb sessionDb,
        SafeFs safeFs,
        SourceGuard sourceGuard,
        string sessionId,
        string sourceRoot,
        string sessionTemporaryDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionDb);
        ArgumentNullException.ThrowIfNull(safeFs);
        ArgumentNullException.ThrowIfNull(sourceGuard);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionTemporaryDirectory);

        string fullSource = Path.GetFullPath(sourceRoot);
        Process process = Process.GetCurrentProcess();
        long peakWorkingSet = process.WorkingSet64;
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task sampler = SampleWorkingSetAsync(
            process,
            linked.Token,
            value =>
            {
                if (value > peakWorkingSet)
                {
                    peakWorkingSet = value;
                }
            });
        ScanOrchestrator orchestrator = new(sessionDb, safeFs, sourceGuard);
        Stopwatch scanClock = Stopwatch.StartNew();
        ScanRunResult result = await orchestrator.RunAsync(
                sessionId,
                fullSource,
                sessionTemporaryDirectory,
                computeFolderSizes: false,
                cancellationToken: linked.Token)
            .ConfigureAwait(false);
        scanClock.Stop();
        await linked.CancelAsync().ConfigureAwait(false);
        try
        {
            await sampler.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
        NodeBrowser browser = new(sessionDb, sessionId);
        Stopwatch treeClock = Stopwatch.StartNew();
        NodePage folders = browser.GetLargest(null, folders: true);
        TreeNodeRow? scale = folders.Rows.FirstOrDefault(
            static row => string.Equals(row.Name, "Scale", StringComparison.OrdinalIgnoreCase))
            ?? folders.Rows.FirstOrDefault();
        NodePage page = scale is null
            ? browser.GetChildren(null)
            : browser.GetChildren(scale.Id);
        treeClock.Stop();

        bool passed = result.Walk.Completed &&
            peakWorkingSet < MemoryCeilingBytes &&
            page.Rows.Count <= NodeBrowser.ChildPageSize;
        return string.Join(
            Environment.NewLine,
            [
                "Passed: " + (passed ? "true" : "false"),
                "Source: " + result.SourceRoot,
                "NodesVisited: " + result.Walk.NodesVisited.ToString(CultureInfo.InvariantCulture),
                "ScanSeconds: " + scanClock.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture),
                "WalkSeconds: " + result.WalkElapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture),
                "ClassifySeconds: " + result.ClassifyElapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture),
                "PeakWorkingSetBytes: " + peakWorkingSet.ToString(CultureInfo.InvariantCulture),
                "PeakWorkingSetMiB: " + (peakWorkingSet / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture),
                "TreePageRows: " + page.Rows.Count.ToString(CultureInfo.InvariantCulture),
                "TreePageMilliseconds: " + treeClock.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture),
                "TreeParent: " + (scale?.Name ?? "(root)"),
            ]);
    }

    private static async Task SampleWorkingSetAsync(
        Process process,
        CancellationToken cancellationToken,
        Action<long> onSample)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            process.Refresh();
            onSample(process.WorkingSet64);
            try
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
