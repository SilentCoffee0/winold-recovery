using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Safety;

namespace WinOldRecovery.Core.Tests.Safety;

public sealed class SourceGuardTests : IDisposable
{
    private readonly string testRoot;
    private readonly string sourceRoot;
    private readonly string destinationRoot;
    private readonly SourceGuard guard = new();
    private readonly SafeFs safeFs;

    public SourceGuardTests()
    {
        testRoot = Path.Combine(Path.GetTempPath(), $"WinOldRecovery-{Guid.NewGuid():N}");
        sourceRoot = Path.Combine(testRoot, "Windows.old");
        destinationRoot = Path.Combine(testRoot, "destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destinationRoot);
        safeFs = new SafeFs(guard);
    }

    [Fact]
    public void Canonicalize_NormalizesExtendedPrefixDotSegmentsAndCasing()
    {
        string registered = guard.RegisterSourceRoot(sourceRoot);
        string trickyPath = Path.Combine(
            sourceRoot.ToUpperInvariant(),
            "folder",
            "..",
            "file.txt");

        Assert.StartsWith(@"\\?\", registered, StringComparison.Ordinal);
        Assert.True(guard.IsSourcePath(PathCanonicalizer.ToExtendedPath(trickyPath)));
    }

    [Fact]
    public void I1_SourceRootAndChildrenRefuseWritesWithoutPurgeToken()
    {
        guard.RegisterSourceRoot(sourceRoot);
        string directory = Path.Combine(sourceRoot, "new-directory");
        string file = Path.Combine(sourceRoot, "new-file.txt");

        Assert.Throws<SourceWriteDeniedException>(() => safeFs.CreateDirectory(directory));
        Assert.Throws<SourceWriteDeniedException>(
            () => safeFs.OpenWrite(file, FileMode.CreateNew));
        Assert.False(Directory.Exists(directory));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void I1_MoveRefusesMutationOfSourceEvenWhenDestinationIsOutside()
    {
        string sourceFile = Path.Combine(sourceRoot, "only-copy.txt");
        File.WriteAllText(sourceFile, "irreplaceable");
        guard.RegisterSourceRoot(sourceRoot);

        Assert.Throws<SourceWriteDeniedException>(
            () => safeFs.MoveFile(
                sourceFile,
                Path.Combine(destinationRoot, "moved.txt")));

        Assert.True(File.Exists(sourceFile));
        Assert.False(File.Exists(Path.Combine(destinationRoot, "moved.txt")));
    }

    [Fact]
    public void I1_SetAccessControlRefusesRegisteredSource()
    {
        string sourceFile = Path.Combine(sourceRoot, "id_ed25519");
        File.WriteAllText(sourceFile, "key");
        guard.RegisterSourceRoot(sourceRoot);
        FileSecurity security = new FileInfo(sourceFile).GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        Assert.Throws<SourceWriteDeniedException>(() => safeFs.SetAccessControl(sourceFile, security));
    }

    [Fact]
    public void PrefixSiblingIsNotTreatedAsAChild()
    {
        guard.RegisterSourceRoot(sourceRoot);
        string sibling = sourceRoot + "-backup";

        safeFs.CreateDirectory(sibling);

        Assert.True(Directory.Exists(sibling));
        Assert.False(guard.IsSourcePath(sibling));
    }

    [Fact]
    public void ExtendedPathPreservesTrailingSpacesAndStillProtectsSource()
    {
        guard.RegisterSourceRoot(sourceRoot);
        string trailingSpaceChild = PathCanonicalizer.ToExtendedPath(
            Path.Combine(sourceRoot, "trailing-space "));

        Assert.EndsWith(" ", PathCanonicalizer.NormalizeLexically(trailingSpaceChild));
        Assert.Throws<SourceWriteDeniedException>(
            () => safeFs.OpenWrite(trailingSpaceChild, FileMode.CreateNew));
    }

    [Fact]
    public void CorrectlyScopedPurgeTokenAllowsAWrite()
    {
        string canonicalRoot = guard.RegisterSourceRoot(sourceRoot);
        PurgeToken token = new(canonicalRoot);
        string file = Path.Combine(sourceRoot, "purge-authorized.txt");

        using (safeFs.OpenWrite(file, FileMode.CreateNew, token))
        {
        }

        Assert.True(File.Exists(file));
    }

    [Fact]
    public void TokenForAnotherSourceDoesNotAuthorizeAWrite()
    {
        guard.RegisterSourceRoot(sourceRoot);
        string otherSource = Path.Combine(testRoot, "Windows.old.001");
        Directory.CreateDirectory(otherSource);
        string otherCanonical = guard.RegisterSourceRoot(otherSource);
        PurgeToken wrongToken = new(otherCanonical);

        Assert.Throws<SourceWriteDeniedException>(
            () => safeFs.CreateDirectory(
                Path.Combine(sourceRoot, "not-authorized"),
                wrongToken));
    }

    [Fact]
    public void ReparseAliasIntoSourceIsRecognizedAndRefused()
    {
        guard.RegisterSourceRoot(sourceRoot);
        string alias = Path.Combine(testRoot, "source-alias");
        CreateJunction(alias, sourceRoot);

        try
        {
            string pathThroughAlias = Path.Combine(alias, "blocked");
            Assert.True(guard.IsSourcePath(pathThroughAlias));
            Assert.Throws<SourceWriteDeniedException>(
                () => safeFs.CreateDirectory(pathThroughAlias));
            Assert.Throws<IOException>(() => safeFs.OpenRead(alias));
        }
        finally
        {
            Directory.Delete(alias);
        }
    }

    [Fact]
    public void ExistingShortNameAliasIsRecognizedWhenNtfsProvidesOne()
    {
        string longRoot = Path.Combine(testRoot, "Windows Old Source With Long Name");
        Directory.CreateDirectory(longRoot);
        guard.RegisterSourceRoot(longRoot);

        string? shortPath = TryGetShortPath(longRoot);
        if (shortPath is null ||
            string.Equals(shortPath, longRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Assert.True(guard.IsSourcePath(Path.Combine(shortPath, "child.txt")));
    }

    [Fact]
    public void I12_OpenReadRefusesOfflinePlaceholders()
    {
        string cloud = Path.Combine(sourceRoot, "offline-placeholder.txt");
        File.WriteAllText(cloud, string.Empty);
        File.SetAttributes(cloud, FileAttributes.Offline);
        guard.RegisterSourceRoot(sourceRoot);

        IOException exception = Assert.Throws<IOException>(() => safeFs.OpenRead(cloud));
        Assert.Contains("offline", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.GetAttributes(cloud).HasFlag(FileAttributes.Offline));
    }

    [Fact]
    public void OpenReadReadsOrdinarySourceFileWithoutWritingIt()
    {
        string sourceFile = Path.Combine(sourceRoot, "read-me.txt");
        File.WriteAllText(sourceFile, "source data");
        DateTime before = File.GetLastWriteTimeUtc(sourceFile);
        guard.RegisterSourceRoot(sourceRoot);

        using StreamReader reader = new(safeFs.OpenRead(sourceFile));
        string contents = reader.ReadToEnd();

        Assert.Equal("source data", contents);
        Assert.Equal(before, File.GetLastWriteTimeUtc(sourceFile));
    }

    [Fact]
    public void RelativePathsAreRejected()
    {
        Assert.Throws<ArgumentException>(
            () => guard.RegisterSourceRoot(@"relative\Windows.old"));
    }

    public void Dispose()
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
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

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start mklink.");
        process.WaitForExit();

        Assert.True(
            process.ExitCode == 0,
            $"Could not create test junction: {process.StandardError.ReadToEnd()}");
    }

    private static string? TryGetShortPath(string path)
    {
        StringBuilder buffer = new(512);
        uint length = GetShortPathName(path, buffer, (uint)buffer.Capacity);
        return length is 0 or >= 512 ? null : buffer.ToString();
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathName(
        string longPath,
        StringBuilder shortPath,
        uint bufferLength);
}
