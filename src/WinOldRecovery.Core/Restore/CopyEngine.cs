using System.ComponentModel;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Planning;

namespace WinOldRecovery.Core.Restore;

public sealed class RestorePausedException : Exception
{
    public RestorePausedException(string reason)
        : base(reason)
    {
        Reason = reason;
    }

    public string Reason { get; }
}

public sealed record RestoreItemResult(long PlanItemId, string State, string? DestinationPath);

public sealed record RestoreResult(
    bool Completed,
    bool PausedDiskFull,
    IReadOnlyList<RestoreItemResult> Items);

public sealed class CopyEngine
{
    public const string PartialSuffix = ".winold-partial";
    private const int BufferSize = 1024 * 1024;

    private readonly SessionDb sessionDb;
    private readonly SafeFs safeFs;

    public CopyEngine(SessionDb sessionDb, SafeFs safeFs)
    {
        this.sessionDb = sessionDb ?? throw new ArgumentNullException(nameof(sessionDb));
        this.safeFs = safeFs ?? throw new ArgumentNullException(nameof(safeFs));
    }

    public async Task<RestoreItemResult> CopyAsync(
        PlanItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Id is not long planItemId)
        {
            throw new InvalidOperationException("Plan items must be stored before copy.");
        }

        string? latest = sessionDb.GetLatestJournalState(planItemId);
        if (latest is "Completed" or "Skipped")
        {
            return new RestoreItemResult(planItemId, latest, item.DestinationPath);
        }

        if (latest == "Started")
        {
            DeletePartial(item.DestinationPath);
        }

        await sessionDb.AppendJournalAsync(planItemId, "Started", cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        try
        {
            bool resume = latest == "Started";
            if (item.Operation == PlanOperation.CopyTree)
            {
                CopyTree(item, resume, cancellationToken);
            }
            else
            {
                CopyOneFile(item.SourcePath, item.DestinationPath, item, resume, cancellationToken);
            }

            await sessionDb.AppendJournalAsync(planItemId, "Completed", cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new RestoreItemResult(planItemId, "Completed", item.DestinationPath);
        }
        catch (RestorePausedException)
        {
            await sessionDb.AppendJournalAsync(planItemId, "Paused", "DiskFull", cancellationToken)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            if (IsDiskFull(exception))
            {
                await sessionDb.AppendJournalAsync(planItemId, "Paused", "DiskFull", cancellationToken)
                    .ConfigureAwait(false);
                throw new RestorePausedException("DiskFull");
            }

            await sessionDb.AppendJournalAsync(
                    planItemId,
                    "Failed",
                    exception.Message,
                    cancellationToken)
                .ConfigureAwait(false);
            return new RestoreItemResult(planItemId, "Failed", item.DestinationPath);
        }
    }

    internal static string KeepBothPath(string destinationPath)
    {
        foreach (string candidate in KeepBothCandidates(destinationPath))
        {
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return destinationPath + " (from Windows.old)";
    }

    internal static IEnumerable<string> KeepBothCandidates(string destinationPath)
    {
        string directory = Path.GetDirectoryName(destinationPath) ?? destinationPath;
        string name = Path.GetFileNameWithoutExtension(destinationPath);
        string extension = Path.GetExtension(destinationPath);
        yield return Path.Combine(directory, name + " (from Windows.old)" + extension);
        for (int suffix = 2; suffix <= 1000; suffix++)
        {
            yield return Path.Combine(
                directory,
                name + " (from Windows.old " + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" + extension);
        }
    }

    public static bool LooksLikeSuccessfulCopy(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath) || !File.Exists(destinationPath))
        {
            return false;
        }

        FileInfo source = new(sourcePath);
        FileInfo destination = new(destinationPath);
        if (source.Length != destination.Length)
        {
            return false;
        }

        return Math.Abs((destination.LastWriteTimeUtc - source.LastWriteTimeUtc).TotalSeconds) <= 2;
    }

    public static string? FindRestoredPath(string sourcePath, string plannedDestination)
    {
        foreach (string candidate in KeepBothCandidates(plannedDestination))
        {
            if (LooksLikeSuccessfulCopy(sourcePath, candidate))
            {
                return candidate;
            }
        }

        if (LooksLikeSuccessfulCopy(sourcePath, plannedDestination))
        {
            return plannedDestination;
        }

        foreach (string candidate in KeepBothCandidates(plannedDestination))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        if (File.Exists(plannedDestination))
        {
            return plannedDestination;
        }

        return null;
    }

    private void CopyTree(PlanItem item, bool resume, CancellationToken cancellationToken)
    {
        foreach (string sourceFile in EnumerateSourceFiles(item.SourcePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(item.SourcePath, sourceFile);
            string destinationFile = Path.Combine(item.DestinationPath, relative);
            CopyOneFile(sourceFile, destinationFile, item, resume, cancellationToken);
        }
    }

    private void CopyOneFile(
        string sourcePath,
        string destinationPath,
        PlanItem item,
        bool resume,
        CancellationToken cancellationToken)
    {
        FileAttributes attributes = File.GetAttributes(sourcePath);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.Encrypted)) != 0)
        {
            return;
        }

        string finalPath = destinationPath;
        IReadOnlySet<string> approved = OverwriteApprovals.Parse(
            sessionDb.GetKv(item.SessionId, OverwriteApprovals.KvKey));
        bool overwriteThis = item.OverwriteApproved || approved.Contains(destinationPath);
        string? already = FindRestoredPath(sourcePath, destinationPath);
        if (already is not null && LooksLikeSuccessfulCopy(sourcePath, already) && !overwriteThis)
        {
            bool alreadyIsPlanned = already.Equals(destinationPath, StringComparison.OrdinalIgnoreCase);
            if (!alreadyIsPlanned || resume)
            {
                return;
            }
        }

        if (File.Exists(finalPath) || Directory.Exists(finalPath))
        {
            if (item.ConflictPolicy == ConflictPolicy.Skip && !overwriteThis)
            {
                return;
            }

            if (!overwriteThis)
            {
                finalPath = KeepBothPath(finalPath);
            }
        }

        string? directory = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrEmpty(directory))
        {
            safeFs.CreateDirectory(directory);
        }

        string partial = finalPath + PartialSuffix;
        if (File.Exists(partial))
        {
            safeFs.DeleteFile(partial);
        }

        DateTime creation = File.GetCreationTimeUtc(sourcePath);
        DateTime written = File.GetLastWriteTimeUtc(sourcePath);

        try
        {
            using (FileStream source = safeFs.OpenRead(sourcePath))
            using (FileStream destination = safeFs.OpenWrite(partial, FileMode.CreateNew))
            {
                byte[] buffer = new byte[BufferSize];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    destination.Write(buffer, 0, read);
                }
            }

            safeFs.SetCreationTimeUtc(partial, creation);
            safeFs.SetLastWriteTimeUtc(partial, written);
            safeFs.MoveFile(partial, finalPath, overwrite: overwriteThis && File.Exists(finalPath));
        }
        catch (Exception exception) when (IsDiskFull(exception))
        {
            if (File.Exists(partial))
            {
                try
                {
                    safeFs.DeleteFile(partial);
                }
                catch (IOException)
                {
                }
            }

            throw new RestorePausedException("DiskFull");
        }
    }

    internal static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        Stack<string> directories = new();
        directories.Push(root);
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
        };

        while (directories.Count > 0)
        {
            string directory = directories.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory, "*", options))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(entry);
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }

    private void DeletePartial(string destinationPath)
    {
        string partial = destinationPath + PartialSuffix;
        if (File.Exists(partial))
        {
            safeFs.DeleteFile(partial);
        }

        if (!Directory.Exists(destinationPath))
        {
            return;
        }

        Stack<string> directories = new();
        directories.Push(destinationPath);
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
        };
        while (directories.Count > 0)
        {
            string directory = directories.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory, "*", options);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(entry);
                    continue;
                }

                if (entry.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    safeFs.DeleteFile(entry);
                }
            }
        }
    }

    private static bool IsDiskFull(Exception exception)
    {
        const int errorDiskFull = 112;
        const int errorHandleDiskFull = 39;
        if (exception is RestorePausedException)
        {
            return true;
        }

        if (exception is Win32Exception win32 &&
            win32.NativeErrorCode is errorDiskFull or errorHandleDiskFull)
        {
            return true;
        }

        if (exception is IOException io &&
            (io.HResult == unchecked((int)0x80070070) || io.HResult == unchecked((int)0x80070027)))
        {
            return true;
        }

        return exception.InnerException is not null && IsDiskFull(exception.InnerException);
    }
}
