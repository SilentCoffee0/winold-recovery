using System.Globalization;

namespace WinOldRecovery.FixtureGen;

public static class ScaleTree
{
    public const int DefaultDirectoryCount = 1000;
    public const int DefaultFilesPerDirectory = 1000;

    public static int ExpectedNodes(int directoryCount, int filesPerDirectory) =>
        4 + directoryCount + (directoryCount * filesPerDirectory);

    public static void Create(
        string targetRoot,
        int directoryCount = DefaultDirectoryCount,
        int filesPerDirectory = DefaultFilesPerDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);
        ArgumentOutOfRangeException.ThrowIfLessThan(directoryCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(filesPerDirectory, 1);

        string root = Path.GetFullPath(targetRoot);
        Directory.CreateDirectory(Path.Combine(root, "Users", "Alice"));
        string scale = Path.Combine(root, "Users", "Alice", "Scale");
        Directory.CreateDirectory(scale);
        Parallel.For(
            0,
            directoryCount,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            directory =>
            {
                string folder = Path.Combine(
                    scale,
                    "d" + directory.ToString("D4", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(folder);
                for (int file = 0; file < filesPerDirectory; file++)
                {
                    string path = Path.Combine(
                        folder,
                        "f" + file.ToString("D4", CultureInfo.InvariantCulture) + ".txt");
                    File.Create(path).Dispose();
                }
            });
    }
}
