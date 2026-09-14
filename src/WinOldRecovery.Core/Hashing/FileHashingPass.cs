using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Scan;

namespace WinOldRecovery.Core.Hashing;

public sealed class FileHashingPass
{
    public const long MaxFileBytes = 64L * 1024 * 1024;

    public static bool RequiresContentHash(string path, long byteLength)
    {
        if (byteLength <= 0)
        {
            return false;
        }

        if (byteLength <= MaxFileBytes)
        {
            return true;
        }

        string extension = Path.GetExtension(path);
        return extension.Equals(".vhdx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".vhd", StringComparison.OrdinalIgnoreCase);
    }

    private readonly SessionDb sessionDb;
    private readonly SafeFs safeFs;

    public FileHashingPass(SessionDb sessionDb, SafeFs safeFs)
    {
        this.sessionDb = sessionDb ?? throw new ArgumentNullException(nameof(sessionDb));
        this.safeFs = safeFs ?? throw new ArgumentNullException(nameof(safeFs));
    }

    public async Task<int> HashSessionFilesAsync(
        string sessionId,
        string sourceRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);

        List<(long Id, string RelPath, long Size)> files = [];
        using (SqliteConnection reader = sessionDb.OpenReadConnection())
        using (SqliteCommand command = reader.CreateCommand())
        {
            command.CommandText =
                """
                SELECT id, rel_path, size
                FROM nodes
                WHERE session_id = $sessionId
                  AND kind = 'File'
                  AND problem = 'None'
                  AND size > 0
                  AND size <= $max;
                """;
            command.Parameters.AddWithValue("$sessionId", sessionId);
            command.Parameters.AddWithValue("$max", MaxFileBytes);
            using SqliteDataReader rows = command.ExecuteReader();
            while (rows.Read())
            {
                files.Add((rows.GetInt64(0), rows.GetString(1), rows.GetInt64(2)));
            }
        }

        int hashed = 0;
        foreach ((long id, string relPath, long _) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.Combine(sourceRoot, relPath);
            try
            {
                await using FileStream stream = safeFs.OpenRead(path);
                byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                string hex = Convert.ToHexString(hash);
                await sessionDb.SetKvAsync(
                        sessionId,
                        "filehash." + id.ToString(CultureInfo.InvariantCulture),
                        hex,
                        cancellationToken)
                    .ConfigureAwait(false);
                hashed++;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return hashed;
    }
}
