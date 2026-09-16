using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Planning;
using WinOldRecovery.Core.Restore;
using WinOldRecovery.Core.Safety;

namespace WinOldRecovery.Integration.Tests.Safety;

[Collection("Scale")]
public sealed class I7NetworkMonitorTests
{
    private const int DirectoryCount = 20;
    private const int FilesPerDirectory = 20;

    [Fact(Timeout = 120_000)]
    public async Task I7_RestoreHarnessCopy_OpensNoRemoteSockets()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinOldRecovery-I7-" + Guid.NewGuid().ToString("N"));
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
                    DirectoryCount * FilesPerDirectory,
                    ConflictPolicy.KeepBoth,
                    OverwriteApproved: false,
                    RecipeId: null);
                await database.ReplacePlanItemsAsync("session-1", [item]);
            }

            SqliteConnection.ClearAllPools();

            using Process harness = StartHarness(databasePath);
            List<string> remote = [];
            try
            {
                while (!harness.HasExited)
                {
                    remote.AddRange(ListRemoteEndpoints(harness.Id));
                    await Task.Delay(50);
                }
            }
            finally
            {
                if (!harness.HasExited)
                {
                    harness.Kill(entireProcessTree: true);
                    harness.WaitForExit(15_000);
                }
            }

            remote.AddRange(ListRemoteEndpoints(harness.Id));
            Assert.Equal(0, harness.ExitCode);
            Assert.True(
                remote.Count == 0,
                "RestoreHarness opened remote sockets: " + string.Join("; ", remote.Distinct(StringComparer.Ordinal)));
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

    [SkippableFact(Timeout = 120_000)]
    public async Task I7_PublishedScan_OpensNoRemoteSockets()
    {
        string? exe = Environment.GetEnvironmentVariable("WINOLD_RECOVERY_EXE");
        Skip.If(
            string.IsNullOrWhiteSpace(exe) || !File.Exists(exe),
            "Set WINOLD_RECOVERY_EXE to the published WinOldRecovery.exe.");

        string root = Path.Combine(Path.GetTempPath(), "WinOldRecovery-I7-scan-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "OldInstall");
        string report = Path.Combine(root, "report.txt");
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice", "Desktop"));
        await File.WriteAllTextAsync(Path.Combine(source, "Users", "Alice", "Desktop", "note.txt"), "i7");

        ProcessStartInfo start = new(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--scan");
        start.ArgumentList.Add(source);
        start.ArgumentList.Add("--report");
        start.ArgumentList.Add(report);

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("Could not start WinOldRecovery.exe.");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 740)
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            Skip.If(true, "Published EXE requires elevation (UIPI). Run this test from an elevated testhost.");
            return;
        }

        List<string> remote = [];
        int exitCode;
        try
        {
            while (!process.HasExited)
            {
                remote.AddRange(ListRemoteEndpoints(process.Id));
                await Task.Delay(50);
            }

            exitCode = process.ExitCode;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(15_000);
            }

            process.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        Assert.Equal(0, exitCode);
        Assert.True(
            remote.Count == 0,
            "Published --scan opened remote sockets: " + string.Join("; ", remote.Distinct(StringComparer.Ordinal)));
    }

    private static void CreateTree(string source)
    {
        byte[] payload = new byte[4096];
        for (int directory = 0; directory < DirectoryCount; directory++)
        {
            string folder = Path.Combine(
                source,
                "d" + directory.ToString("D2", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            for (int file = 0; file < FilesPerDirectory; file++)
            {
                string path = Path.Combine(
                    folder,
                    "f" + file.ToString("D2", CultureInfo.InvariantCulture) + ".dat");
                File.WriteAllBytes(path, payload);
            }
        }
    }

    private static Process StartHarness(string databasePath)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "RestoreHarness.exe");
        ProcessStartInfo start;
        if (File.Exists(exe))
        {
            start = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(databasePath);
            start.ArgumentList.Add("session-1");
        }
        else
        {
            string dll = Path.Combine(AppContext.BaseDirectory, "RestoreHarness.dll");
            Assert.True(File.Exists(dll), "RestoreHarness was not copied to the test output.");
            string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "dotnet",
                    "dotnet.exe");
            start = new ProcessStartInfo(host)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(dll);
            start.ArgumentList.Add(databasePath);
            start.ArgumentList.Add("session-1");
        }

        return Process.Start(start) ?? throw new InvalidOperationException("Could not start RestoreHarness.");
    }

    private static IReadOnlyList<string> ListRemoteEndpoints(int processId)
    {
        ProcessStartInfo start = new("netstat")
        {
            Arguments = "-ano",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using Process netstat = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start netstat.");
        string output = netstat.StandardOutput.ReadToEnd();
        if (!netstat.WaitForExit(10_000))
        {
            netstat.Kill(entireProcessTree: true);
            throw new TimeoutException("netstat did not exit.");
        }

        string pid = processId.ToString(CultureInfo.InvariantCulture);
        List<string> remote = [];
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 ||
                !parts[^1].Equals(pid, StringComparison.Ordinal) ||
                (!parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase) &&
                    !parts[0].Equals("UDP", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string foreign = parts[2];
            if (IsLoopbackOrUnspecified(foreign))
            {
                continue;
            }

            if (parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length < 5)
                {
                    continue;
                }

                string state = parts[3];
                if (!state.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase) &&
                    !state.Equals("SYN_SENT", StringComparison.OrdinalIgnoreCase) &&
                    !state.Equals("SYN_RECEIVED", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            remote.Add(line);
        }

        return remote;
    }

    private static bool IsLoopbackOrUnspecified(string endpoint)
    {
        if (endpoint.Equals("*:*", StringComparison.Ordinal) ||
            endpoint.StartsWith("0.0.0.0:", StringComparison.Ordinal) ||
            endpoint.StartsWith("[::]:", StringComparison.Ordinal) ||
            endpoint.StartsWith("[::0]:", StringComparison.Ordinal) ||
            endpoint.StartsWith("127.0.0.1:", StringComparison.Ordinal) ||
            endpoint.StartsWith("[::1]:", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }
}
