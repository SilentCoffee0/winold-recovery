using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.IO;

namespace WinOldRecovery.Recipes;

internal static class ReadOnlySqlite
{
    public static string CopyToTemp(SafeFs safeFs, string sourcePath, string tempDirectory, string fileName)
    {
        string destination = Path.Combine(tempDirectory, fileName);
        safeFs.CopyReadToWrite(sourcePath, destination);
        return destination;
    }

    public static SqliteConnection OpenReadOnly(string path)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
        };
        SqliteConnection connection = new(builder.ConnectionString);
        connection.Open();
        return connection;
    }
}
