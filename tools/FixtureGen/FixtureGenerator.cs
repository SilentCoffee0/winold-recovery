using System.Buffers.Binary;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Processes;
using WinOldRecovery.Core.Safety;
using WinOldRecovery.Native;
using WinOldRecovery.Recipes;

namespace WinOldRecovery.FixtureGen;

public sealed class FixtureGenerator
{
    private const string Canary = "WINOLD_RECOVERY_CANARY_DO_NOT_LOG_7F3A91";

    private readonly SafeFs safeFs;
    private readonly IProcessRunner processRunner;

    public FixtureGenerator(IProcessRunner processRunner)
    {
        this.processRunner =
            processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        safeFs = new SafeFs(new SourceGuard());
    }

    public async Task<FixtureManifest> GenerateAsync(
        FixtureOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.NodeModulesFileCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The node_modules file count must be positive.");
        }

        string targetRoot = Path.GetFullPath(options.TargetRoot);
        EnsureTargetIsNewOrEmpty(targetRoot);

        bool isAdministrator = IsAdministrator();
        if (!options.PortableMode && !isAdministrator)
        {
            throw new InvalidOperationException(
                "Full fixture generation requires an elevated administrator process. " +
                "Use --portable only for development tests that explicitly allow privileged hazards to remain pending.");
        }

        if (!options.PortableMode)
        {
            Privileges.EnableBackupAndRestore();
        }

        safeFs.CreateDirectory(targetRoot);
        Dictionary<string, FixtureHazard> hazards =
            new(StringComparer.OrdinalIgnoreCase);

        string liveProfile = Path.Combine(
            Path.GetDirectoryName(targetRoot)
                ?? throw new InvalidOperationException("The fixture needs a parent directory."),
            $"{Path.GetFileName(targetRoot)}-live-profile");
        EnsureTargetIsNewOrEmpty(liveProfile);
        safeFs.CreateDirectory(liveProfile);
        WriteText(Path.Combine(liveProfile, "live-only.txt"), "must never be scanned");

        Console.WriteLine("Creating profiles and reparse hazards...");
        CreateProfiles(targetRoot, options.PortableMode, hazards);
        await CreateReparseHazardsAsync(
            targetRoot,
            liveProfile,
            options.PortableMode,
            hazards,
            cancellationToken);
        Console.WriteLine($"Writing {options.NodeModulesFileCount:N0} node_modules files...");
        CreateFilesystemHazards(targetRoot, options.NodeModulesFileCount, hazards);
        Console.WriteLine("Creating recipe shells...");
        CreateRecipeShells(targetRoot, hazards);

        if (options.PortableMode)
        {
            hazards["deny-acl"] = Unavailable(
                targetRoot,
                Path.Combine(targetRoot, "Users", "Alice", "Denied"),
                "Requires an elevated full-fixture run.");
            hazards["orphan-sid"] = Unavailable(
                targetRoot,
                Path.Combine(targetRoot, "Users", "Bob", "Orphaned", "owned.txt"),
                "Requires an elevated full-fixture run (unmapped owner SID).");
            hazards["efs"] = Unavailable(
                targetRoot,
                Path.Combine(targetRoot, "Users", "Alice", "Efs", "encrypted.txt"),
                "Requires EFS support in an elevated full-fixture run.");
        }
        else
        {
            Console.WriteLine("Creating privileged hazards (deny-ACL, EFS, orphan SID)...");
            await CreatePrivilegedHazardsAsync(
                targetRoot,
                hazards,
                cancellationToken);
        }

        Console.WriteLine("Writing fixture-manifest.json...");

        FixtureManifest manifest = new(
            Version: 1,
            FullHazardsRequested: !options.PortableMode,
            NodeModulesFileCount: options.NodeModulesFileCount,
            Hazards: hazards);
        WriteText(
            Path.Combine(targetRoot, FixtureOptions.ManifestFileName),
            JsonSerializer.Serialize(
                manifest,
                new JsonSerializerOptions { WriteIndented = true }));
        return manifest;
    }

    private static void EnsureTargetIsNewOrEmpty(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        if (Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new IOException(
                $"Fixture generation refuses to replace non-empty directory '{path}'.");
        }
    }

    private void CreateProfiles(
        string targetRoot,
        bool portableMode,
        IDictionary<string, FixtureHazard> hazards)
    {
        string defaultProfileHive = Path.Combine(
            Directory.GetParent(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))?.FullName
                ?? string.Empty,
            "Default",
            "NTUSER.DAT");
        bool templateHiveAvailable = File.Exists(defaultProfileHive);
        if (!templateHiveAvailable && !portableMode)
        {
            throw new FileNotFoundException(
                "The clean Default profile NTUSER.DAT template was not found.",
                defaultProfileHive);
        }

        foreach (string profile in new[] { "Alice", "Bob" })
        {
            string profileRoot = Path.Combine(targetRoot, "Users", profile);
            safeFs.CreateDirectory(profileRoot);
            string fixtureHive = Path.Combine(profileRoot, "NTUSER.DAT");
            if (templateHiveAvailable)
            {
                CopyFile(defaultProfileHive, fixtureHive);
                foreach (string suffix in new[] { ".LOG1", ".LOG2" })
                {
                    string sourceLog = defaultProfileHive + suffix;
                    if (File.Exists(sourceLog))
                    {
                        CopyFile(sourceLog, fixtureHive + suffix);
                    }
                }
            }
            else
            {
                WriteText(fixtureHive, "REGF fixture unavailable in portable mode");
            }

            WriteText(
                Path.Combine(profileRoot, "Desktop", $"{profile}-document.txt"),
                $"fixture document for {profile}");
            hazards[$"profile-{profile.ToLowerInvariant()}"] =
                Created(targetRoot, profileRoot);
        }

        string publicRoot = Path.Combine(targetRoot, "Users", "Public");
        safeFs.CreateDirectory(publicRoot);
        WriteText(Path.Combine(publicRoot, "shared.txt"), "shared fixture");
        safeFs.CreateDirectory(Path.Combine(targetRoot, "Windows", "System32", "config"));
        safeFs.CreateDirectory(Path.Combine(targetRoot, "ProgramData"));
        hazards["registry-hive"] = templateHiveAvailable
            ? Created(targetRoot, Path.Combine(targetRoot, "Users", "Alice", "NTUSER.DAT"))
            : Unavailable(
                targetRoot,
                Path.Combine(targetRoot, "Users", "Alice", "NTUSER.DAT"),
                "The clean Windows Default profile hive was not available.");
    }

    private async Task CreateReparseHazardsAsync(
        string targetRoot,
        string liveProfile,
        bool portableMode,
        IDictionary<string, FixtureHazard> hazards,
        CancellationToken cancellationToken)
    {
        string alice = Path.Combine(targetRoot, "Users", "Alice");
        await CreateLinkAsync(
            "legacy-junction",
            targetRoot,
            Path.Combine(alice, "Application Data"),
            liveProfile,
            isDirectory: true,
            junction: true,
            portableMode,
            hazards,
            cancellationToken);
        await CreateLinkAsync(
            "junction-loop",
            targetRoot,
            Path.Combine(alice, "Loop"),
            alice,
            isDirectory: true,
            junction: true,
            portableMode,
            hazards,
            cancellationToken);
        await CreateLinkAsync(
            "directory-symlink",
            targetRoot,
            Path.Combine(alice, "DirectorySymlink"),
            liveProfile,
            isDirectory: true,
            junction: false,
            portableMode,
            hazards,
            cancellationToken);
        await CreateLinkAsync(
            "file-symlink",
            targetRoot,
            Path.Combine(alice, "file-link.txt"),
            Path.Combine(liveProfile, "live-only.txt"),
            isDirectory: false,
            junction: false,
            portableMode,
            hazards,
            cancellationToken);
    }

    private async Task CreateLinkAsync(
        string key,
        string targetRoot,
        string linkPath,
        string linkTarget,
        bool isDirectory,
        bool junction,
        bool portableMode,
        IDictionary<string, FixtureHazard> hazards,
        CancellationToken cancellationToken)
    {
        List<string> arguments = ["/d", "/c", "mklink"];
        if (junction)
        {
            arguments.Add("/J");
        }
        else if (isDirectory)
        {
            arguments.Add("/D");
        }

        arguments.Add(linkPath);
        arguments.Add(linkTarget);
        ProcessResult result = await processRunner.RunAsync(
            new ProcessRequest(
                Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
                arguments),
            cancellationToken);

        if (result.ExitCode == 0)
        {
            hazards[key] = Created(targetRoot, linkPath);
            return;
        }

        string detail = $"mklink exited with code {result.ExitCode}.";
        if (!portableMode)
        {
            throw new InvalidOperationException(
                $"Could not create required {key} hazard. {detail}");
        }

        hazards[key] = Unavailable(targetRoot, linkPath, detail);
    }

    private void CreateFilesystemHazards(
        string targetRoot,
        int nodeModulesFileCount,
        IDictionary<string, FixtureHazard> hazards)
    {
        string alice = Path.Combine(targetRoot, "Users", "Alice");

        string longDirectory = Path.Combine(alice, "LongPaths");
        while (Path.Combine(longDirectory, "deep-file.txt").Length < 310)
        {
            longDirectory = Path.Combine(
                longDirectory,
                $"segment-{longDirectory.Length:D4}-abcdefghijklmnopqrstuvwxyz");
        }

        string longFile = Path.Combine(longDirectory, "deep-file.txt");
        WriteText(longFile, "long path fixture");
        hazards["long-path"] = Created(targetRoot, longFile);

        string offlineFile = Path.Combine(alice, "Cloud", "offline-placeholder.txt");
        WriteText(offlineFile, string.Empty);
        safeFs.SetAttributes(offlineFile, FileAttributes.Offline);
        hazards["offline-placeholder"] = Created(targetRoot, offlineFile);

        string invalidName = Path.Combine(alice, "InvalidNames", "trailing-space. ");
        WriteText(
            PathCanonicalizer.ToExtendedPath(invalidName),
            "invalid destination name fixture");
        hazards["invalid-name"] = Created(targetRoot, invalidName);

        string nodeModules = Path.Combine(alice, "Projects", "fixture-app", "node_modules");
        safeFs.CreateDirectory(nodeModules);
        for (int index = 0; index < nodeModulesFileCount; index++)
        {
            using FileStream file = safeFs.OpenWrite(
                Path.Combine(nodeModules, $"package-{index:D6}.tmp"),
                FileMode.CreateNew);
            if (index > 0 && index % 25_000 == 0)
            {
                Console.WriteLine($"  wrote {index:N0} / {nodeModulesFileCount:N0}");
            }
        }

        hazards["node-modules"] = new FixtureHazard(
            "Created",
            Relative(targetRoot, nodeModules),
            $"{nodeModulesFileCount} files");
    }

    private void CreateRecipeShells(
        string targetRoot,
        IDictionary<string, FixtureHazard> hazards)
    {
        string alice = Path.Combine(targetRoot, "Users", "Alice");

        string firefox = Path.Combine(
            alice,
            "AppData",
            "Roaming",
            "Mozilla",
            "Firefox",
            "Profiles",
            "fixture.default");
        WriteText(
            Path.Combine(firefox, "logins.json"),
            $$"""{"logins":[{"encryptedUsername":"{{Canary}}"}]}""");
        WriteText(Path.Combine(firefox, "key4.db"), string.Empty);
        WriteSqlite(
            Path.Combine(firefox, "places.sqlite"),
            """
            CREATE TABLE moz_places(id INTEGER PRIMARY KEY, url TEXT, title TEXT);
            CREATE TABLE moz_bookmarks(id INTEGER PRIMARY KEY, type INTEGER, fk INTEGER, parent INTEGER, title TEXT);
            INSERT INTO moz_places(url, title) VALUES ('https://example.invalid/', 'Fixture');
            INSERT INTO moz_bookmarks(type, fk, parent, title) VALUES (1, 1, 0, 'Fixture');
            """);
        WriteBytes(
            Path.Combine(firefox, "sessionstore.jsonlz4"),
            MozLz4.Encode(
                """{"windows":[{"tabs":[{"entries":[{"url":"https://example.invalid/","title":"Fixture"}]}]}]}"""u8));
        WriteText(
            Path.Combine(alice, "AppData", "Roaming", "Mozilla", "Firefox", "profiles.ini"),
            """
            [General]
            StartWithLastProfile=1
            Version=2

            [Profile0]
            Name=fixture
            IsRelative=1
            Path=Profiles/fixture.default
            Default=1
            """);

        string chromeUserData = Path.Combine(
            alice,
            "AppData",
            "Local",
            "Google",
            "Chrome",
            "User Data");
        string chrome = Path.Combine(chromeUserData, "Default");
        WriteText(
            Path.Combine(chrome, "Bookmarks"),
            """{"checksum":"","roots":{"bookmark_bar":{"children":[{"name":"Fixture","type":"url","url":"https://example.invalid/"}],"name":"Bookmarks bar","type":"folder"},"other":{"children":[],"name":"Other bookmarks","type":"folder"}},"version":1}""");
        WriteText(Path.Combine(chrome, "Login Data"), Canary);
        WriteText(
            Path.Combine(chromeUserData, "Local State"),
            "{\"profile\":{\"info_cache\":{\"Default\":{\"name\":\"Alice\"}}},\"os_crypt\":{\"encrypted_key\":\"" + Canary + "\"}}");
        WriteText(Path.Combine(chromeUserData, "Last Version"), "131.0.0.0");
        WriteSqlite(
            Path.Combine(chrome, "History"),
            """
            CREATE TABLE urls(id INTEGER PRIMARY KEY, url TEXT, title TEXT);
            INSERT INTO urls(url, title) VALUES ('https://example.invalid/', 'Fixture');
            """);
        WriteBytes(
            Path.Combine(chrome, "Sessions", "Session_1"),
            SnssReader.CreateSessionFile(3, [(SnssReader.UpdateTabNavigation, "https://example.invalid/"u8.ToArray())]));

        string syncthing = Path.Combine(alice, "AppData", "Local", "Syncthing");
        (string certPem, string keyPem, string deviceId) = CreateSyncthingIdentity();
        WriteText(Path.Combine(syncthing, "config.xml"), "<configuration version=\"1\" />");
        WriteText(Path.Combine(syncthing, "cert.pem"), certPem);
        WriteText(Path.Combine(syncthing, "key.pem"), keyPem);
        hazards["syncthing"] = new FixtureHazard(
            "Created",
            Relative(targetRoot, syncthing),
            deviceId);

        string ssh = Path.Combine(alice, ".ssh");
        WriteBytes(Path.Combine(ssh, "id_ed25519"), OpenSshPem("none"));
        WriteBytes(Path.Combine(ssh, "id_ed25519_encrypted"), OpenSshPem("aes256-ctr"));
        WriteText(Path.Combine(ssh, "id_ed25519.pub"), "ssh-ed25519 FIXTURE");

        WriteGitStateFixtures(Path.Combine(alice, "Projects"));

        string wsl = Path.Combine(
            alice,
            "AppData",
            "Local",
            "wsl",
            "{00000000-0000-0000-0000-000000000001}",
            "ext4.vhdx");
        byte[] vhdxHeader = new byte[512];
        Encoding.ASCII.GetBytes("vhdxfile").CopyTo(vhdxHeader, 0);
        WriteBytes(wsl, vhdxHeader);

        string anki = Path.Combine(
            alice,
            "AppData",
            "Roaming",
            "Anki2",
            "User 1");
        WriteSqlite(
            Path.Combine(anki, "collection.anki2"),
            """
            CREATE TABLE notes(id INTEGER PRIMARY KEY, guid TEXT);
            CREATE TABLE cards(id INTEGER PRIMARY KEY, nid INTEGER);
            CREATE TABLE col(id INTEGER PRIMARY KEY, ver INTEGER, scm INTEGER);
            """);
        WriteText(Path.Combine(anki, "collection.media", "image.png"), "fixture media");

        WriteText(Path.Combine(alice, "Documents", "Passwords", "fixture.kdbx"), Canary);
        WriteText(Path.Combine(alice, "Documents", "Passwords", "fixture.keyx"), "keyfile");
        WriteText(
            Path.Combine(
                alice,
                "AppData",
                "Roaming",
                "gnupg",
                "private-keys-v1.d",
                "fixture.key"),
            Canary);
        WriteText(
            Path.Combine(alice, "AppData", "Roaming", "Code", "User", "settings.json"),
            """{"editor.fontSize":14}""");
        WriteText(
            Path.Combine(alice, ".vscode", "extensions", "extensions.json"),
            """[{"identifier":{"id":"ms-python.python"}}]""");
        WriteText(
            Path.Combine(alice, "AppData", "Roaming", "Thunderbird", "Profiles", "fixture.default", "prefs.js"),
            "user_pref(\"mail.identity.id1.useremail\",\"alice@example.com\");");
        WriteText(
            Path.Combine(alice, "AppData", "Roaming", "Thunderbird", "Profiles", "fixture.default", "key4.db"),
            Canary);
        WriteText(
            Path.Combine(
                alice,
                "AppData",
                "Local",
                "Packages",
                "Microsoft.WindowsTerminal_8wekyb3d8bbwe",
                "LocalState",
                "settings.json"),
            """{"profiles":{}}""");
        WriteText(Path.Combine(alice, "Documents", "Notes", ".obsidian", "app.json"), "{}");
        WriteText(Path.Combine(alice, "Documents", "Notes", "welcome.md"), "hello");
        WriteText(Path.Combine(alice, "Documents", "Outlook Files", "archive.pst"), "pst");
        WriteText(Path.Combine(alice, "AppData", "Local", "Microsoft", "Outlook", "user.ost"), "ost");

        hazards["recipe-shells"] = Created(
            targetRoot,
            Path.Combine(alice, "AppData"));
    }

    private async Task CreatePrivilegedHazardsAsync(
        string targetRoot,
        IDictionary<string, FixtureHazard> hazards,
        CancellationToken cancellationToken)
    {
        string denyDirectory = Path.Combine(
            targetRoot,
            "Users",
            "Alice",
            "Denied");
        WriteText(Path.Combine(denyDirectory, "protected.txt"), "backup privilege reads this");
        await RunRequiredAsync(
            "icacls.exe",
            [
                denyDirectory,
                "/inheritance:r",
                "/deny",
                "*S-1-5-32-545:(OI)(CI)(F)",
                "/Q",
            ],
            "apply the deny ACL",
            cancellationToken);
        hazards["deny-acl"] = Created(targetRoot, denyDirectory);

        string efsFile = Path.Combine(
            targetRoot,
            "Users",
            "Alice",
            "Efs",
            "encrypted.txt");
        WriteText(efsFile, "EFS fixture");
        try
        {
            File.Encrypt(efsFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidOperationException(
                "Could not EFS-encrypt the fixture file without cipher.exe: " + exception.Message,
                exception);
        }

        if ((File.GetAttributes(efsFile) & FileAttributes.Encrypted) == 0)
        {
            throw new InvalidOperationException(
                "File.Encrypt returned without setting the Encrypted attribute.");
        }

        hazards["efs"] = Created(targetRoot, efsFile);

        string orphanFile = Path.Combine(
            targetRoot,
            "Users",
            "Bob",
            "Orphaned",
            "owned.txt");
        WriteText(orphanFile, "orphan owner fixture");
        string ownerSid = AssignUnmappedOwner(orphanFile);
        hazards["orphan-sid"] = new FixtureHazard(
            "Created",
            Relative(targetRoot, orphanFile),
            ownerSid);
    }

    private async Task RunRequiredAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string operation,
        CancellationToken cancellationToken)
    {
        ProcessResult result = await processRunner.RunAsync(
            new ProcessRequest(fileName, arguments, Timeout: TimeSpan.FromSeconds(45)),
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not {operation}; {fileName} exited with code {result.ExitCode}.");
        }
    }

    private static string AssignUnmappedOwner(string path)
    {
        SecurityIdentifier sid = new("S-1-5-21-2147483647-1-1-1001");
        FileInfo file = new(path);
        FileSecurity security = file.GetAccessControl();
        security.SetOwner(sid);
        file.SetAccessControl(security);
        try
        {
            _ = sid.Translate(typeof(NTAccount));
            throw new InvalidOperationException(
                "The orphan fixture SID unexpectedly mapped to a local account.");
        }
        catch (IdentityNotMappedException)
        {
        }

        return FileSecurityInfo.GetOwnerSid(path);
    }

    private void WriteGitStateFixtures(string projects)
    {
        const string oidMain = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string oidOrigin = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string oidZero = "0000000000000000000000000000000000000000";
        const string withOrigin =
            """
            [core]
            	repositoryformatversion = 0
            [remote "origin"]
            	url = https://example.invalid/alice/repo.git
            [branch "main"]
            	remote = origin
            	merge = refs/heads/main
            """;
        const string noRemote =
            """
            [core]
            	repositoryformatversion = 0
            """;

        WriteGitWorkTree(
            Path.Combine(projects, "git-clean-pushed"),
            withOrigin,
            oidMain,
            originMain: oidMain);
        WriteGitWorkTree(
            Path.Combine(projects, "git-uncommitted"),
            withOrigin,
            oidMain,
            originMain: oidMain,
            index: true,
            workFiles: [("dirty.txt", "dirty working tree")]);
        WriteGitWorkTree(
            Path.Combine(projects, "git-untracked"),
            withOrigin,
            oidMain,
            originMain: oidMain,
            workFiles: [("scratch.txt", "never added")]);
        WriteGitWorkTree(
            Path.Combine(projects, "git-unpushed"),
            withOrigin,
            oidMain,
            originMain: oidOrigin);
        WriteGitWorkTree(
            Path.Combine(projects, "git-local-only-branch"),
            withOrigin,
            oidMain,
            originMain: oidMain,
            extraHeads: [("topic", oidMain)]);
        WriteGitWorkTree(
            Path.Combine(projects, "git-stash"),
            withOrigin,
            oidMain,
            originMain: oidMain,
            stash: oidMain);
        WriteGitWorkTree(
            Path.Combine(projects, "local-repository"),
            noRemote,
            oidZero);

        string worktree = Path.Combine(projects, "git-worktree");
        WriteText(Path.Combine(worktree, "README.md"), "linked worktree\n");
        WriteText(
            Path.Combine(worktree, ".git"),
            "gitdir: ../git-clean-pushed/.git/worktrees/linked\n");
        WriteText(
            Path.Combine(projects, "git-clean-pushed", ".git", "worktrees", "linked", "HEAD"),
            "ref: refs/heads/main\n");
        WriteText(
            Path.Combine(projects, "git-clean-pushed", ".git", "worktrees", "linked", "commondir"),
            "../..\n");
    }

    private void WriteGitWorkTree(
        string workTree,
        string config,
        string headOid,
        string? originMain = null,
        IReadOnlyList<(string Name, string Oid)>? extraHeads = null,
        string? stash = null,
        bool index = false,
        IReadOnlyList<(string Name, string Contents)>? workFiles = null)
    {
        WriteText(Path.Combine(workTree, ".git", "HEAD"), "ref: refs/heads/main\n");
        WriteText(Path.Combine(workTree, ".git", "config"), config);
        WriteText(Path.Combine(workTree, ".git", "refs", "heads", "main"), headOid + "\n");
        if (originMain is not null)
        {
            WriteText(Path.Combine(workTree, ".git", "refs", "remotes", "origin", "main"), originMain + "\n");
        }

        if (extraHeads is not null)
        {
            foreach ((string name, string oid) in extraHeads)
            {
                WriteText(Path.Combine(workTree, ".git", "refs", "heads", name), oid + "\n");
            }
        }

        if (stash is not null)
        {
            WriteText(Path.Combine(workTree, ".git", "refs", "stash"), stash + "\n");
        }

        if (index)
        {
            WriteText(Path.Combine(workTree, ".git", "index"), "DIRC");
        }

        WriteText(Path.Combine(workTree, "README.md"), "fixture repository\n");
        if (workFiles is null)
        {
            return;
        }

        foreach ((string name, string contents) in workFiles)
        {
            WriteText(Path.Combine(workTree, name), contents);
        }
    }

    private static (string CertPem, string KeyPem, string DeviceId) CreateSyncthingIdentity()
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new(
            "CN=syncthing-fixture",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using X509Certificate2 cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));
        string certPem = cert.ExportCertificatePem();
        return (certPem, rsa.ExportPkcs8PrivateKeyPem(), SyncthingDeviceId.FromCertificatePem(certPem));
    }

    private static byte[] OpenSshPem(string cipher)
    {
        using MemoryStream payload = new();
        payload.Write("openssh-key-v1\0"u8);
        WriteSshString(payload, cipher);
        WriteSshString(payload, cipher == "none" ? "none" : "bcrypt");
        WriteSshString(payload, string.Empty);
        string b64 = Convert.ToBase64String(payload.ToArray());
        return Encoding.ASCII.GetBytes(
            "-----BEGIN OPENSSH PRIVATE KEY-----\n" + b64 + "\n-----END OPENSSH PRIVATE KEY-----\n");
    }

    private static void WriteSshString(Stream stream, string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }

    private void WriteSqlite(string path, string sql)
    {
        string temp = Path.Combine(Path.GetTempPath(), "WinOldRecovery-fixture-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = temp }.ConnectionString))
            {
                connection.Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();
            WriteBytes(path, File.ReadAllBytes(temp));
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private void WriteText(string path, string contents)
    {
        safeFs.CreateDirectory(
            Path.GetDirectoryName(path)
                ?? throw new ArgumentException("The fixture path needs a parent.", nameof(path)));
        using StreamWriter writer = new(
            safeFs.OpenWrite(path, FileMode.CreateNew),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(contents);
    }

    private void WriteBytes(string path, ReadOnlySpan<byte> contents)
    {
        safeFs.CreateDirectory(
            Path.GetDirectoryName(path)
                ?? throw new ArgumentException("The fixture path needs a parent.", nameof(path)));
        using FileStream writer = safeFs.OpenWrite(path, FileMode.CreateNew);
        writer.Write(contents);
    }

    private void CopyFile(string sourcePath, string destinationPath)
    {
        safeFs.CreateDirectory(
            Path.GetDirectoryName(destinationPath)
                ?? throw new ArgumentException(
                    "The fixture path needs a parent.",
                    nameof(destinationPath)));
        using FileStream source = safeFs.OpenRead(sourcePath);
        using FileStream destination = safeFs.OpenWrite(
            destinationPath,
            FileMode.CreateNew);
        source.CopyTo(destination);
    }

    private static FixtureHazard Created(string root, string path)
    {
        return new FixtureHazard("Created", Relative(root, path));
    }

    private static FixtureHazard Unavailable(
        string root,
        string path,
        string detail)
    {
        return new FixtureHazard("Unavailable", Relative(root, path), detail);
    }

    private static string Relative(string root, string path)
    {
        string rootWithSeparator = Path.TrimEndingDirectorySeparator(root) +
            Path.DirectorySeparatorChar;
        if (path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return path[rootWithSeparator.Length..];
        }

        return Path.GetRelativePath(root, path);
    }

    private static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity)
            .IsInRole(WindowsBuiltInRole.Administrator);
    }
}
