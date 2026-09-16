using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Planning;
using WinOldRecovery.Core.Restore;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Integration.Tests.Safety;

namespace WinOldRecovery.Integration.Tests.Restore;

[Collection("Scale")]
public sealed class I15KillProcessRestoreTests
{
    private const int DirectoryCount = 100;
    private const int FilesPerDirectory = 500;
    private const int FileCount = DirectoryCount * FilesPerDirectory;

    [Fact(Timeout = 900_000)]
    public async Task I15_KillProcessDuringCopyTree_ResumesWithoutPartialsOrSourceWrites()
    {
        string root = Path.Combine(Path.GetTempPath(), $"WinOldRecovery-KillCopy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "Windows.old", "Desktop");
        string destination = Path.Combine(root, "Recovered", "Desktop");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        CreateTree(source);

        SourceGuard guard = new();
        guard.RegisterSourceRoot(Path.Combine(root, "Windows.old"));
        SafeFs safeFs = new(guard);
        string databasePath = Path.Combine(root, "session.db");
        DateTime sourceWrite = Directory.GetLastWriteTimeUtc(source);

        try
        {
            await using (SessionDb database = await SessionDb.OpenAsync(databasePath, safeFs))
            {
                await database.CreateSessionAsync(
                    new SessionRecord("session-1", DateTimeOffset.UtcNow, "Restoring", "0.1.0"));
                PlanItem item = new(
                    "session-1",
                    1,
                    PlanOperation.CopyTree,
                    source,
                    destination,
                    FileCount,
                    ConflictPolicy.KeepBoth,
                    OverwriteApproved: false,
                    RecipeId: null);
                await database.ReplacePlanItemsAsync("session-1", [item]);
            }

            SqliteConnection.ClearAllPools();

            using SourceWatchdog watchdog = new(Path.Combine(root, "Windows.old"));
            using Process harness = StartHarness(databasePath);
            try
            {
                WaitUntilCopied(harness, destination, minimumFiles: 50, TimeSpan.FromSeconds(60));
            }
            finally
            {
                if (!harness.HasExited)
                {
                    harness.Kill(entireProcessTree: true);
                    harness.WaitForExit(15_000);
                }
            }

            int copiedBeforeResume = CountRestoredFiles(destination);
            Assert.InRange(copiedBeforeResume, 1, FileCount - 1);

            SqliteConnection.ClearAllPools();
            await using (SessionDb database = await OpenAfterKillAsync(databasePath, safeFs))
            {
                CopyEngine engine = new(database, safeFs);
                PlanItem item = Assert.Single(database.ListPlanItems("session-1"));
                RestoreItemResult result = await engine.CopyAsync(item);
                Assert.Equal("Completed", result.State);
            }

            Assert.Equal(FileCount, CountRestoredFiles(destination));
            Assert.DoesNotContain(
                Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories),
                path => path.EndsWith(CopyEngine.PartialSuffix, StringComparison.OrdinalIgnoreCase));
            AssertEqualBytes(source, destination);
            Assert.Equal(sourceWrite, Directory.GetLastWriteTimeUtc(source));
            watchdog.ThrowIfSourceChanged();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void CreateTree(string source)
    {
        Parallel.For(
            0,
            DirectoryCount,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            directory =>
            {
                string folder = Path.Combine(
                    source,
                    "d" + directory.ToString("D3", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(folder);
                for (int file = 0; file < FilesPerDirectory; file++)
                {
                    int id = (directory * FilesPerDirectory) + file;
                    string path = Path.Combine(
                        folder,
                        "f" + file.ToString("D4", CultureInfo.InvariantCulture) + ".dat");
                    File.WriteAllBytes(path, BitConverter.GetBytes(id));
                }
            });
    }

    private static Process StartHarness(string databasePath)
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "RestoreHarness.dll");
        Assert.True(File.Exists(dll), "RestoreHarness.dll was not copied to the test output.");
        string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "dotnet",
                "dotnet.exe");
        ProcessStartInfo start = new(host)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(dll);
        start.ArgumentList.Add(databasePath);
        start.ArgumentList.Add("session-1");
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start RestoreHarness.");
    }

    private static void WaitUntilCopied(
        Process harness,
        string destination,
        int minimumFiles,
        TimeSpan timeout)
    {
        Stopwatch wait = Stopwatch.StartNew();
        while (wait.Elapsed < timeout)
        {
            if (harness.HasExited)
            {
                throw new InvalidOperationException(
                    "RestoreHarness exited with " + harness.ExitCode.ToString(CultureInfo.InvariantCulture)
                    + " before producing files.");
            }

            if (CountRestoredFiles(destination) >= minimumFiles)
            {
                return;
            }

            Thread.Sleep(50);
        }

        throw new TimeoutException(
            "Copy harness did not produce " + minimumFiles.ToString(CultureInfo.InvariantCulture)
            + " destination files before kill.");
    }

    private static int CountRestoredFiles(string destination)
    {
        if (!Directory.Exists(destination))
        {
            return 0;
        }

        return Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
            .Count(static path => !path.EndsWith(CopyEngine.PartialSuffix, StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertEqualBytes(string source, string destination)
    {
        foreach (string sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, sourceFile);
            string destFile = Path.Combine(destination, relative);
            Assert.True(File.Exists(destFile), destFile);
            Assert.Equal(File.ReadAllBytes(sourceFile), File.ReadAllBytes(destFile));
        }
    }

    private static async Task<SessionDb> OpenAfterKillAsync(string databasePath, SafeFs safeFs)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                return await SessionDb.OpenAsync(databasePath, safeFs);
            }
            catch (SqliteException)
            {
                await Task.Delay(100);
            }
        }

        return await SessionDb.OpenAsync(databasePath, safeFs);
    }
}

[CollectionDefinition("Scale", DisableParallelization = true)]
public sealed class ScaleCollection;
