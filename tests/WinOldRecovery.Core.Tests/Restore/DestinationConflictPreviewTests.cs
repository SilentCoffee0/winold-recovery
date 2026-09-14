using System.Diagnostics;
using WinOldRecovery.Core.Restore;

namespace WinOldRecovery.Core.Tests.Restore;

public sealed class DestinationConflictPreviewTests
{
    [Fact]
    public void Scan_MissingDestination_ReportsZero()
    {
        string root = CreateRoot();
        try
        {
            string source = Path.Combine(root, "old", "note.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, "from-old");

            DestinationConflictPreview preview = DestinationConflictPreview.Scan(
                source,
                Path.Combine(root, "new", "note.txt"));

            Assert.Equal(0, preview.DestinationFiles);
            Assert.Equal("Destination does not already have these files.", DestinationConflictPreview.Format(preview));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scan_MatchingFile_ReportsNoDifference()
    {
        string root = CreateRoot();
        try
        {
            string source = Path.Combine(root, "old", "note.txt");
            string dest = Path.Combine(root, "new", "note.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            DateTime stamp = new(2024, 4, 5, 6, 7, 8, DateTimeKind.Utc);
            File.WriteAllText(source, "same");
            File.WriteAllText(dest, "same");
            File.SetLastWriteTimeUtc(source, stamp);
            File.SetLastWriteTimeUtc(dest, stamp);

            DestinationConflictPreview preview = DestinationConflictPreview.Scan(source, dest);

            Assert.Equal(1, preview.DestinationFiles);
            Assert.Equal(0, preview.Differing);
            Assert.Equal("Destination already has 1 files.", DestinationConflictPreview.Format(preview));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scan_Directory_CountsDifferingFilesAndIgnoresDestinationJunctions()
    {
        string root = CreateRoot();
        try
        {
            string source = Path.Combine(root, "old");
            string dest = Path.Combine(root, "new");
            string trap = Path.Combine(root, "trap");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(dest);
            Directory.CreateDirectory(trap);
            File.WriteAllText(Path.Combine(source, "keep.txt"), "keep");
            File.WriteAllText(Path.Combine(source, "change.txt"), "from-old");
            File.WriteAllText(Path.Combine(dest, "keep.txt"), "keep");
            File.WriteAllText(Path.Combine(dest, "change.txt"), "already");
            File.WriteAllText(Path.Combine(trap, "outside.txt"), "trap");
            DateTime stamp = new(2024, 4, 5, 6, 7, 8, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(Path.Combine(source, "keep.txt"), stamp);
            File.SetLastWriteTimeUtc(Path.Combine(dest, "keep.txt"), stamp);
            CreateJunction(Path.Combine(dest, "linked"), trap);

            DestinationConflictPreview preview = DestinationConflictPreview.Scan(source, dest);

            Assert.Equal(2, preview.DestinationFiles);
            Assert.Equal(1, preview.Differing);
            Assert.Contains("2 files", DestinationConflictPreview.Format(preview), StringComparison.Ordinal);
            Assert.Contains("1 differ", DestinationConflictPreview.Format(preview), StringComparison.Ordinal);
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinOldRecovery-ConflictPreview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void CreateJunction(string junction, string target)
    {
        ProcessStartInfo startInfo = new(
            Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junction);
        startInfo.ArgumentList.Add(target);
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("mklink");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
        }
    }

    private static void DeleteTree(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(entry);
            }
            else if ((attributes & FileAttributes.Directory) != 0)
            {
                DeleteTree(entry);
            }
            else
            {
                File.Delete(entry);
            }
        }

        Directory.Delete(root);
    }
}
