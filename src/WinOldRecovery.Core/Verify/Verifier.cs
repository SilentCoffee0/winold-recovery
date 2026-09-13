using System.Security.Cryptography;
using WinOldRecovery.Core.Hashing;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Planning;
using WinOldRecovery.Core.Restore;

namespace WinOldRecovery.Core.Verify;

public sealed class Verifier
{
    public const int LastWriteToleranceSeconds = 2;

    private readonly SessionDb sessionDb;
    private readonly SafeFs safeFs;

    public Verifier(SessionDb sessionDb, SafeFs safeFs)
    {
        this.sessionDb = sessionDb ?? throw new ArgumentNullException(nameof(sessionDb));
        this.safeFs = safeFs ?? throw new ArgumentNullException(nameof(safeFs));
    }

    public async Task<VerifyReport> VerifyAsync(
        RestorePlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string reportId = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<VerifyResultRow> rows = [];

        foreach (PlanItem item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Id is not long planItemId)
            {
                continue;
            }

            IReadOnlyList<(string Source, string Destination)> files = ListCopiedFiles(item);
            bool l0 = files.Count > 0 && files.All(pair => File.Exists(pair.Destination) || Directory.Exists(item.DestinationPath));
            if (item.Operation == PlanOperation.CopyTree)
            {
                l0 = Directory.Exists(item.DestinationPath);
            }
            else
            {
                l0 = File.Exists(item.DestinationPath) ||
                    File.Exists(CopyEngine.KeepBothPath(item.DestinationPath));
            }

            rows.Add(new VerifyResultRow(planItemId, reportId, 0, l0, l0 ? "exists" : "missing", now));

            bool l1 = l0;
            bool l2 = l0;
            foreach ((string source, string destination) in files)
            {
                string dest = File.Exists(destination)
                    ? destination
                    : CopyEngine.KeepBothPath(destination);
                if (!File.Exists(dest))
                {
                    l1 = false;
                    l2 = false;
                    continue;
                }

                FileInfo sourceInfo = new(source);
                FileInfo destInfo = new(dest);
                TimeSpan delta = destInfo.LastWriteTimeUtc - sourceInfo.LastWriteTimeUtc;
                if (destInfo.Length != sourceInfo.Length ||
                    Math.Abs(delta.TotalSeconds) > LastWriteToleranceSeconds)
                {
                    l1 = false;
                }

                if (sourceInfo.Length > 0 && sourceInfo.Length <= FileHashingPass.MaxFileBytes)
                {
                    byte[] sourceHash = await HashAsync(source, cancellationToken).ConfigureAwait(false);
                    byte[] destHash = await HashAsync(dest, cancellationToken).ConfigureAwait(false);
                    if (!sourceHash.AsSpan().SequenceEqual(destHash))
                    {
                        l2 = false;
                    }
                }
            }

            rows.Add(new VerifyResultRow(planItemId, reportId, 1, l1, l1 ? "size-time" : "size-time mismatch", now));
            rows.Add(new VerifyResultRow(planItemId, reportId, 2, l2, l2 ? "hash" : "hash mismatch", now));
        }

        await sessionDb.InsertVerifyResultsAsync(rows, cancellationToken).ConfigureAwait(false);
        return new VerifyReport(reportId, rows.All(static row => row.Ok), rows);
    }

    private async Task<byte[]> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = safeFs.OpenRead(path);
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<(string Source, string Destination)> ListCopiedFiles(PlanItem item)
    {
        if (item.Operation == PlanOperation.CopyFile)
        {
            return [(item.SourcePath, item.DestinationPath)];
        }

        if (!Directory.Exists(item.SourcePath))
        {
            return [];
        }

        List<(string, string)> files = [];
        foreach (string source in CopyEngine.EnumerateSourceFiles(item.SourcePath))
        {
            string relative = Path.GetRelativePath(item.SourcePath, source);
            files.Add((source, Path.Combine(item.DestinationPath, relative)));
        }

        return files;
    }
}
