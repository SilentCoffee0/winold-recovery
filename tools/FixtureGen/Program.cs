using WinOldRecovery.Core.Processes;

namespace WinOldRecovery.FixtureGen;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase))
            {
                PrintUsage();
                return args.Length == 0 ? 2 : 0;
            }

            if (string.Equals(args[0], "--bundle-self-test", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length != 1)
                {
                    PrintUsage();
                    return 2;
                }

                BundleSelfTestResult bundleResult = await BundleSelfTest.RunAsync();
                Console.WriteLine(
                    $"Bundle self-test passed: Registry={bundleResult.RegistryParsed}, " +
                    $"SQLite={bundleResult.SqliteLoaded}, " +
                    $"elapsed={bundleResult.ElapsedMilliseconds} ms.");
                return 0;
            }

            if (string.Equals(args[0], "--scale-tree", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length != 2)
                {
                    PrintUsage();
                    return 2;
                }

                string scaleRoot = Path.GetFullPath(args[1]);
                Console.WriteLine(
                    $"Writing {ScaleTree.DefaultDirectoryCount:N0} folders of {ScaleTree.DefaultFilesPerDirectory:N0} empty files at {scaleRoot} ...");
                ScaleTree.Create(scaleRoot);
                Console.WriteLine(
                    $"Scale tree created at '{scaleRoot}' ({ScaleTree.ExpectedNodes(ScaleTree.DefaultDirectoryCount, ScaleTree.DefaultFilesPerDirectory):N0} nodes including Users/Alice/Scale).");
                return 0;
            }

            if (string.Equals(args[0], "--self-check-only", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length != 2)
                {
                    PrintUsage();
                    return 2;
                }

                FixtureCheckResult existingResult = new FixtureSelfCheck().Check(args[1]);
                existingResult.ThrowIfFailed();
                Console.WriteLine("Fixture self-check passed.");
                return 0;
            }

            string target = args[0];
            bool portable = args.Contains("--portable", StringComparer.OrdinalIgnoreCase);
            int fileCount = ReadFileCount(args);
            FixtureOptions options = new(target, fileCount, portable);

            FixtureGenerator generator = new(new ProcessRunner());
            await generator.GenerateAsync(options);
            FixtureCheckResult result = new FixtureSelfCheck().Check(target);
            result.ThrowIfFailed();

            Console.WriteLine(
                $"Fixture created and checked at '{Path.GetFullPath(target)}' " +
                $"with {fileCount:N0} node_modules files.");
            if (portable)
            {
                Console.WriteLine(
                    "Portable mode left deny-ACL, orphan-SID, and EFS hazards explicitly unavailable.");
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static int ReadFileCount(IReadOnlyList<string> args)
    {
        for (int index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], "--files", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= args.Count ||
                !int.TryParse(args[index + 1], out int count) ||
                count < 1)
            {
                throw new ArgumentException("--files requires a positive integer.");
            }

            return count;
        }

        return 100_000;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            Usage:
              FixtureGen <target-Windows.old> [--files <count>] [--portable]
              FixtureGen --scale-tree <target-Windows.old>
              FixtureGen --self-check-only <target-Windows.old>
              FixtureGen --bundle-self-test

            Full generation requires an elevated administrator process.
            --portable is intended only for automated development tests and records
            privileged hazards as unavailable instead of pretending they were created.
            """);
    }
}
