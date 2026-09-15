using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Planning;
using WinOldRecovery.Core.Restore;
using WinOldRecovery.Core.Safety;

if (args is { Length: >= 3 } &&
    string.Equals(args[0], "--probe-disk-full", StringComparison.OrdinalIgnoreCase))
{
    return await ProbeDiskFullAsync(args[1], args[2]);
}

if (args is { Length: >= 3 } &&
    string.Equals(args[0], "--probe-runtime-disk-full", StringComparison.OrdinalIgnoreCase))
{
    return await ProbeRuntimeDiskFullAsync(args[1], args[2]);
}

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: RestoreHarness <session.db> <session-id>");
    Console.Error.WriteLine("       RestoreHarness --probe-disk-full <source-dir> <dest-dir>");
    Console.Error.WriteLine("       RestoreHarness --probe-runtime-disk-full <source-dir> <dest-dir>");
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

static async Task<int> ProbeDiskFullAsync(string sourceDir, string destDir)
{
    string source = Path.GetFullPath(sourceDir);
    string dest = Path.GetFullPath(destDir);
    if (!Directory.Exists(source) || !Directory.Exists(dest))
    {
        Console.Error.WriteLine("Source and destination directories must exist.");
        return 2;
    }

    string markerPath = Path.Combine(dest, "keep-me.txt");
    if (!File.Exists(markerPath))
    {
        Console.Error.WriteLine("Destination marker keep-me.txt is missing.");
        return 2;
    }

    string markerBefore = await File.ReadAllTextAsync(markerPath);
    Dictionary<string, (long Length, DateTime Utc)> sourceBefore = Snapshot(source);

    SourceGuard guard = new();
    guard.RegisterSourceRoot(source);
    SafeFs safeFs = new(guard);
    string dbPath = Path.Combine(Path.GetTempPath(), "WinOldRecovery-diskfull-" + Guid.NewGuid().ToString("N") + ".db");
    await using SessionDb database = await SessionDb.OpenAsync(dbPath, safeFs);
    const string sessionId = "diskfull-1";
    await database.CreateSessionAsync(
        new SessionRecord(sessionId, DateTimeOffset.UtcNow, "Restoring", "0.1.0"));

    long bytes = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
    IReadOnlyList<PlanItem> items = await database.ReplacePlanItemsAsync(
        sessionId,
        [
            new PlanItem(
                sessionId,
                1,
                PlanOperation.CopyTree,
                source,
                dest,
                bytes,
                ConflictPolicy.KeepBoth,
                OverwriteApproved: false,
                RecipeId: null),
        ]);
    RestorePlan plan = new(sessionId, source, dest, items, bytes);
    PreflightResult preflight = new PreflightChecker().Check(plan);

    bool pausedDiskFull = false;
    string mode;
    if (!preflight.CanProceed)
    {
        mode = "PreflightBlocked";
    }
    else
    {
        RestoreResult result = await new RestoreRunner(new CopyEngine(database, safeFs), database)
            .RunAsync(plan);
        pausedDiskFull = result.PausedDiskFull;
        mode = pausedDiskFull ? "RuntimePaused" : (result.Completed ? "Completed" : "Other");
    }

    string markerAfter = await File.ReadAllTextAsync(markerPath);
    bool markerIntact = string.Equals(markerBefore, markerAfter, StringComparison.Ordinal);
    bool sourceUntouched = sourceBefore.All(
        pair => File.Exists(pair.Key) &&
            new FileInfo(pair.Key).Length == pair.Value.Length &&
            File.GetLastWriteTimeUtc(pair.Key) == pair.Value.Utc);
    bool passed = (mode is "PreflightBlocked" or "RuntimePaused") && markerIntact && sourceUntouched;

    Console.WriteLine("Passed: " + (passed ? "true" : "false"));
    Console.WriteLine("Mode: " + mode);
    Console.WriteLine("CanProceed: " + preflight.CanProceed);
    Console.WriteLine("RequiredBytes: " + preflight.RequiredBytes);
    Console.WriteLine("FreeBytes: " + preflight.FreeBytes);
    Console.WriteLine("PausedDiskFull: " + pausedDiskFull);
    Console.WriteLine("MarkerIntact: " + markerIntact);
    Console.WriteLine("SourceUntouched: " + sourceUntouched);
    if (preflight.BlockingIssues.Count > 0)
    {
        Console.WriteLine("Blocking: " + string.Join(" | ", preflight.BlockingIssues));
    }

    return passed ? 0 : 1;
}

static async Task<int> ProbeRuntimeDiskFullAsync(string sourceDir, string destDir)
{
    string source = Path.GetFullPath(sourceDir);
    string dest = Path.GetFullPath(destDir);
    if (!Directory.Exists(source) || !Directory.Exists(dest))
    {
        Console.Error.WriteLine("Source and destination directories must exist.");
        return 2;
    }

    string markerPath = Path.Combine(dest, "keep-me.txt");
    if (!File.Exists(markerPath))
    {
        Console.Error.WriteLine("Destination marker keep-me.txt is missing.");
        return 2;
    }

    string markerBefore = await File.ReadAllTextAsync(markerPath);
    Dictionary<string, (long Length, DateTime Utc)> sourceBefore = Snapshot(source);

    SourceGuard guard = new();
    guard.RegisterSourceRoot(source);
    SafeFs safeFs = new(guard);
    string dbPath = Path.Combine(Path.GetTempPath(), "WinOldRecovery-runtime-diskfull-" + Guid.NewGuid().ToString("N") + ".db");
    await using SessionDb database = await SessionDb.OpenAsync(dbPath, safeFs);
    const string sessionId = "diskfull-runtime-1";
    await database.CreateSessionAsync(
        new SessionRecord(sessionId, DateTimeOffset.UtcNow, "Restoring", "0.1.0"));

    long bytes = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
    IReadOnlyList<PlanItem> items = await database.ReplacePlanItemsAsync(
        sessionId,
        [
            new PlanItem(
                sessionId,
                1,
                PlanOperation.CopyTree,
                source,
                dest,
                bytes,
                ConflictPolicy.KeepBoth,
                OverwriteApproved: false,
                RecipeId: null),
        ]);
    RestorePlan plan = new(sessionId, source, dest, items, bytes);
    PreflightResult preflight = new PreflightChecker().Check(plan);
    if (!preflight.CanProceed)
    {
        Console.WriteLine("Passed: false");
        Console.WriteLine("Mode: PreflightBlocked");
        Console.WriteLine("RequiredBytes: " + preflight.RequiredBytes);
        Console.WriteLine("FreeBytes: " + preflight.FreeBytes);
        Console.WriteLine("Blocking: " + string.Join(" | ", preflight.BlockingIssues));
        return 1;
    }

    string fillerPath = Path.Combine(dest, "wor-runtime-filler.bin");
    const long leaveFree = 2L * 1024 * 1024;
    long fillSize = preflight.FreeBytes - leaveFree;
    if (fillSize < bytes)
    {
        Console.Error.WriteLine("Destination is not large enough to pass preflight and then run out during copy.");
        return 2;
    }

    FillConsuming(fillerPath, fillSize);

    CopyEngine engine = new(database, safeFs);
    RestoreResult paused;
    try
    {
        try
        {
            RestoreItemResult copied = await engine.CopyAsync(items[0]);
            paused = new RestoreResult(
                copied.State is "Completed" or "Skipped",
                false,
                [copied]);
        }
        catch (RestorePausedException exception) when (exception.IsDiskFull)
        {
            paused = new RestoreResult(false, true, []);
        }
    }
    finally
    {
        if (File.Exists(fillerPath))
        {
            File.Delete(fillerPath);
        }
    }

    RestoreRunner runner = new(engine, database);
    RestoreResult resumed = await runner.RunAsync(plan);

    string markerAfter = await File.ReadAllTextAsync(markerPath);
    bool markerIntact = string.Equals(markerBefore, markerAfter, StringComparison.Ordinal);
    bool sourceUntouched = sourceBefore.All(
        pair => File.Exists(pair.Key) &&
            new FileInfo(pair.Key).Length == pair.Value.Length &&
            File.GetLastWriteTimeUtc(pair.Key) == pair.Value.Utc);
    bool passed = paused.PausedDiskFull &&
        !paused.Completed &&
        resumed.Completed &&
        !resumed.PausedDiskFull &&
        markerIntact &&
        sourceUntouched &&
        !File.Exists(fillerPath);

    Console.WriteLine("Passed: " + (passed ? "true" : "false"));
    Console.WriteLine("Mode: RuntimePausedThenResumed");
    Console.WriteLine("CanProceed: " + preflight.CanProceed);
    Console.WriteLine("RequiredBytes: " + preflight.RequiredBytes);
    Console.WriteLine("FreeBytes: " + preflight.FreeBytes);
    Console.WriteLine("PausedDiskFull: " + paused.PausedDiskFull);
    Console.WriteLine("ResumedCompleted: " + resumed.Completed);
    Console.WriteLine("MarkerIntact: " + markerIntact);
    Console.WriteLine("SourceUntouched: " + sourceUntouched);
    return passed ? 0 : 1;
}

static void FillConsuming(string path, long size)
{
    byte[] buffer = new byte[1024 * 1024];
    using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
    long written = 0;
    while (written < size)
    {
        int chunk = (int)Math.Min(buffer.Length, size - written);
        stream.Write(buffer, 0, chunk);
        written += chunk;
    }
}

static Dictionary<string, (long Length, DateTime Utc)> Snapshot(string root)
{
    Dictionary<string, (long Length, DateTime Utc)> map = new(StringComparer.OrdinalIgnoreCase);
    foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
    {
        FileInfo info = new(path);
        map[path] = (info.Length, info.LastWriteTimeUtc);
    }

    return map;
}
