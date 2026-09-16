using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Core.Scan;

namespace WinOldRecovery.Core.Tests.Scan;

public sealed class ProfileDetectorTests : IDisposable
{
    private readonly string testRoot;

    public ProfileDetectorTests()
    {
        testRoot = Path.Combine(Path.GetTempPath(), $"WinOldRecovery-Profiles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
    }

    [Fact]
    public void Detect_ClassifiesHumanPublicServiceAndSkipsTemplates()
    {
        string source = Path.Combine(testRoot, "Windows.old");
        CreateHuman(source, "Alice");
        Directory.CreateDirectory(Path.Combine(source, "Users", "Public", "Documents"));
        Directory.CreateDirectory(Path.Combine(source, "Users", "Default"));
        File.WriteAllText(Path.Combine(source, "Users", "Default", "NTUSER.DAT"), "template");
        Directory.CreateDirectory(Path.Combine(source, "Users", "SyncthingServiceAcct"));
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice", "Calibre Library"));
        Directory.CreateDirectory(Path.Combine(source, "Users", "Alice", "AppData"));

        IReadOnlyList<DetectedProfile> profiles = new ProfileDetector().Detect(source);

        Assert.Equal(3, profiles.Count);
        DetectedProfile alice = Assert.Single(profiles, profile => profile.Name == "Alice");
        Assert.Equal(ProfileKind.Human, alice.Kind);
        Assert.Equal("Alice", alice.DisplayName);
        Assert.Contains("Calibre Library", alice.CustomFolders);
        Assert.DoesNotContain("AppData", alice.CustomFolders);
        Assert.Contains(
            alice.StandardFolders,
            folder => folder.KnownName == "Desktop" && folder.PresentInSource);
        Assert.Equal("Public (shared)", Assert.Single(profiles, profile => profile.Kind == ProfileKind.PublicShared).DisplayName);
        Assert.Equal(ProfileKind.Service, Assert.Single(profiles, profile => profile.Name == "SyncthingServiceAcct").Kind);
        Assert.DoesNotContain(profiles, profile => profile.Name == "Default");
    }

    [Fact]
    public void Detect_ReportsRedirectedFoldersOutsideTheSource()
    {
        string source = Path.Combine(testRoot, "Windows.old");
        CreateHuman(source, "Alice");
        string outside = Path.Combine(testRoot, "D-Docs");
        Directory.CreateDirectory(outside);

        StubShellFolders stub = new(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Personal"] = outside,
            });

        DetectedProfile alice = Assert.Single(new ProfileDetector(stub).Detect(source), profile => profile.Name == "Alice");
        StandardFolderMatch documents = Assert.Single(alice.StandardFolders, folder => folder.KnownName == "Documents");
        Assert.False(documents.PresentInSource);
        Assert.Equal(outside, documents.RedirectedAbsolutePath);
        Assert.Null(documents.RelativePathInSource);
    }

    [Fact]
    public void Detect_KeepsRedirectedFoldersThatStillSitInTheSource()
    {
        string source = Path.Combine(testRoot, "Windows.old");
        CreateHuman(source, "Alice");
        string redirected = Path.Combine(source, "Users", "Alice", "MovedDocs");
        Directory.CreateDirectory(redirected);

        StubShellFolders stub = new(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["{F42EE2D3-909F-4907-8871-4C22FC0BF756}"] = redirected,
            });

        DetectedProfile alice = Assert.Single(new ProfileDetector(stub).Detect(source), profile => profile.Name == "Alice");
        StandardFolderMatch documents = Assert.Single(alice.StandardFolders, folder => folder.KnownName == "Documents");
        Assert.True(documents.PresentInSource);
        Assert.Equal(@"Users\Alice\MovedDocs", documents.RelativePathInSource);
        Assert.Null(documents.RedirectedAbsolutePath);
    }

    [Fact]
    public async Task InsertProfiles_PersistsDetectedKinds()
    {
        string source = Path.Combine(testRoot, "Windows.old");
        CreateHuman(source, "Alice");
        SafeFs safeFs = new(new SourceGuard());
        string databasePath = Path.Combine(testRoot, $"session-{Guid.NewGuid():N}.db");
        SessionDb database = await SessionDb.OpenAsync(databasePath, safeFs);
        try
        {
            await database.CreateSessionAsync(
                new SessionRecord("session-1", DateTimeOffset.UtcNow, "Scanning", "0.1.0", source));

            DetectedProfile alice = Assert.Single(new ProfileDetector().Detect(source));
            await database.InsertProfilesAsync(
            [
                new ProfileRecord("session-1", alice.Name, alice.SourcePath, alice.Kind, alice.LastUsedUtc),
            ]);

            using SqliteConnection reader = database.OpenReadConnection();
            using SqliteCommand command = reader.CreateCommand();
            command.CommandText = "SELECT name, kind FROM profiles;";
            using SqliteDataReader rows = command.ExecuteReader();
            Assert.True(rows.Read());
            Assert.Equal("Alice", rows.GetString(0));
            Assert.Equal("Human", rows.GetString(1));
        }
        finally
        {
            await database.DisposeAsync();
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task OfflineHive_ReadsUserShellFoldersFromCopiedNtuser()
    {
        string defaultHive = Path.Combine(
            Directory.GetParent(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))?.FullName
                ?? string.Empty,
            "Default",
            "NTUSER.DAT");
        if (!File.Exists(defaultHive))
        {
            return;
        }

        SafeFs safeFs = new(new SourceGuard());
        WinOldRecovery.Core.Registry.OfflineRegistryHive hive =
            await WinOldRecovery.Core.Registry.OfflineRegistryHive.OpenCopyAsync(
                defaultHive,
                Path.Combine(testRoot, "hive-tmp"),
                safeFs);
        IReadOnlyDictionary<string, string> values = hive.GetStringValues(
            OfflineHiveShellFolderSource.UserShellFoldersKey);

        Assert.NotEmpty(values);
        Assert.Contains(
            values.Keys,
            key => key.Contains("Desktop", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("B4BFCC3A", StringComparison.OrdinalIgnoreCase));
        WinOldRecovery.Core.Registry.OfflineRegistryHive again =
            await WinOldRecovery.Core.Registry.OfflineRegistryHive.OpenCopyAsync(
                defaultHive,
                Path.Combine(testRoot, "hive-tmp"),
                safeFs);
        Assert.Equal(hive.CopiedHivePath, again.CopiedHivePath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static void CreateHuman(string source, string name)
    {
        string profile = Path.Combine(source, "Users", name);
        Directory.CreateDirectory(Path.Combine(profile, "Desktop"));
        File.WriteAllText(Path.Combine(profile, "NTUSER.DAT"), "hive");
        File.WriteAllText(Path.Combine(profile, "Desktop", "note.txt"), name);
    }

    private sealed class StubShellFolders(Dictionary<string, string> values) : IShellFolderValueSource
    {
        public IReadOnlyDictionary<string, string> GetValues(string profileRoot) => values;
    }
}
