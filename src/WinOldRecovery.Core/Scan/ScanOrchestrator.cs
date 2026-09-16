using System.Diagnostics;
using System.Globalization;
using WinOldRecovery.Core.Classification;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Safety;

namespace WinOldRecovery.Core.Scan;

public sealed class ScanOrchestrator
{
    private readonly SessionDb sessionDb;
    private readonly SafeFs safeFs;
    private readonly SourceGuard sourceGuard;

    public ScanOrchestrator(SessionDb sessionDb, SafeFs safeFs, SourceGuard sourceGuard)
    {
        this.sessionDb = sessionDb ?? throw new ArgumentNullException(nameof(sessionDb));
        this.safeFs = safeFs ?? throw new ArgumentNullException(nameof(safeFs));
        this.sourceGuard = sourceGuard ?? throw new ArgumentNullException(nameof(sourceGuard));
    }

    public async Task<ScanRunResult> RunAsync(
        string sessionId,
        string sourceRoot,
        string sessionTemporaryDirectory,
        IShellFolderValueSource? shellFolders = null,
        IProgress<WalkProgress>? progress = null,
        bool resume = false,
        bool computeFolderSizes = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionTemporaryDirectory);

        string registered = sourceGuard.RegisterSourceRoot(sourceRoot);
        IShellFolderValueSource folders = shellFolders ??
            new OfflineHiveShellFolderSource(safeFs, sessionTemporaryDirectory);
        ProfileDetector detector = new(folders);
        IReadOnlyList<DetectedProfile> profiles = detector.Detect(registered);
        progress?.Report(
            new WalkProgress(
                0,
                0,
                string.Empty,
                [],
                ProfileNames: profiles.Select(static profile => profile.DisplayName).ToArray()));
        if (!resume)
        {
            await sessionDb.ClearScanDataAsync(sessionId, cancellationToken).ConfigureAwait(false);
            await sessionDb.InsertProfilesAsync(
                    profiles.Select(profile => new ProfileRecord(
                        sessionId,
                        profile.Name,
                        profile.SourcePath,
                        profile.Kind,
                        profile.LastUsedUtc))
                        .ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        FileSystemWalker walker = new(sessionDb);
        Stopwatch walkClock = Stopwatch.StartNew();
        WalkResult walk = await walker.WalkAsync(
                new WalkRequest(sessionId, registered, Resume: resume, ComputeFolderSizes: computeFolderSizes),
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        walkClock.Stop();

        string[] profileNames = profiles.Select(static profile => profile.DisplayName).ToArray();
        void ReportPhase(string label)
        {
            progress?.Report(
                new WalkProgress(
                    walk.NodesVisited,
                    walk.BytesSeen,
                    label,
                    walk.CompletedTopLevelDirectories,
                    walk.JunctionsSkipped,
                    walk.CloudSkipped,
                    walk.EncryptedSkipped,
                    walk.AccessDenied,
                    ProfileNames: profileNames));
        }

        ReportPhase("Classifying scanned files…");
        ClassificationEngine classifier = new(sessionDb, safeFs);
        Stopwatch classifyClock = Stopwatch.StartNew();
        ClassificationSummary classification = await classifier.ClassifyAsync(
                sessionId,
                registered,
                profiles,
                cancellationToken,
                visited =>
                {
                    if ((visited & 65535) == 0)
                    {
                        ReportPhase("Classifying scanned files (" + visited.ToString("N0", CultureInfo.InvariantCulture) + ")…");
                    }
                })
            .ConfigureAwait(false);
        classifyClock.Stop();

        return new ScanRunResult(
            registered,
            profiles,
            walk,
            classification,
            walkClock.Elapsed,
            classifyClock.Elapsed);
    }
}

public sealed record ScanRunResult(
    string SourceRoot,
    IReadOnlyList<DetectedProfile> Profiles,
    WalkResult Walk,
    ClassificationSummary Classification,
    TimeSpan WalkElapsed,
    TimeSpan ClassifyElapsed);
