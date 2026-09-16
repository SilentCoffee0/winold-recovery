using System.Diagnostics;
using System.Xml.Linq;
using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Processes;
using WinOldRecovery.Core.Recipes;

namespace WinOldRecovery.Recipes;

public sealed class SyncthingRecipe : IRecipe
{
    public string Id => "syncthing";

    public DetectResult Detect(ProfileContext context)
    {
        return DetectHomes(context, CandidateHomes(context), useIndexIdentity: context.Index is not null);
    }

    public DetectResult DetectHome(ProfileContext context, string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        return DetectHomes(context, [Path.GetFullPath(home)], useIndexIdentity: false);
    }

    private DetectResult DetectHomes(
        ProfileContext context,
        IReadOnlyList<string> homes,
        bool useIndexIdentity)
    {
        List<RecipeCard> cards = [];
        List<(string RelativePath, string Kind, string Detail)> badges = [];
        List<(RecipeCard Card, DateTimeOffset Activity)> pending = [];
        string exeVersion = SyncTrayzorExeVersion(context);
        foreach (string home in homes)
        {
            if (!(useIndexIdentity ? IsIndexedHome(context, home) : IsHome(context.SafeFs, home)))
            {
                continue;
            }

            string xml = context.SafeFs.ReadAllText(Path.Combine(home, "config.xml"));
            XDocument document;
            try
            {
                document = SyncthingConfig.Load(xml);
            }
            catch (Exception exception) when (exception is System.Xml.XmlException or InvalidOperationException)
            {
                continue;
            }

            string certPem = context.SafeFs.ReadAllText(Path.Combine(home, "cert.pem"));
            string deviceId = SyncthingDeviceId.FromCertificatePem(certPem);
            string version = (string?)document.Root?.Attribute("version") ?? "?";
            int folders = SyncthingConfig.FolderCount(document);
            int devices = SyncthingConfig.DeviceCount(document);
            string scrubbed = SyncthingConfig.ScrubSecrets(xml);
            DateTimeOffset activity = LastActivity(context.SafeFs, home);
            bool guiTls;
            bool hasGui;
            if (DetectorWalk.TryIndexedRelative(context, home, out RecipeIndex index, out string relative))
            {
                guiTls = DetectorWalk.IndexedImmediatePath(index, home, relative, "https-cert.pem") is not null ||
                    DetectorWalk.IndexedImmediatePath(index, home, relative, "https-key.pem") is not null;
                hasGui = DetectorWalk.IndexedChildFolder(index, relative, "gui");
            }
            else
            {
                guiTls = context.SafeFs.FileExists(Path.Combine(home, "https-cert.pem")) ||
                    context.SafeFs.FileExists(Path.Combine(home, "https-key.pem"));
                hasGui = context.SafeFs.DirectoryExists(Path.Combine(home, "gui"));
            }
            List<RecipeComponent> components =
            [
                new RecipeComponent("identity", "Identity", "cert.pem and key.pem", Decision.Restore, false, null, true),
                new RecipeComponent("config", "Configuration", folders + " folders, version " + version, Decision.Restore, false, null, true),
            ];
            if (guiTls)
            {
                components.Add(
                    new RecipeComponent(
                        "gui-tls",
                        "GUI TLS certificate",
                        "https-cert.pem and https-key.pem regenerate for the local GUI",
                        Decision.LeaveBehind,
                        false,
                        null,
                        true));
            }

            if (hasGui)
            {
                components.Add(
                    new RecipeComponent(
                        "gui",
                        "Custom GUI assets",
                        "gui folder",
                        Decision.Restore,
                        false,
                        null,
                        false));
            }

            components.Add(
                new RecipeComponent(
                    "index",
                    "Index database",
                    "Never restored — missing files would look like deletions",
                    Decision.Undecided,
                    true,
                    "Syncthing rebuilds the index. Restoring it with incomplete folder data can delete files on peers.",
                    false));
            pending.Add((
                new RecipeCard(
                    Id,
                    "Syncthing — " + deviceId,
                    "This computer's Syncthing identity and folder list. Other devices trust this ID.",
                    "If you start fresh, every peer must be re-paired and every folder re-shared.",
                    "cert.pem, key.pem, and a rewritten config.xml with every folder paused. The index is never restored. GUI HTTPS certificates stay behind unless you restore them.",
                    "Start a new device ID and re-share folders.",
                    "The index database and GUI TLS certificate regenerate. The device ID does not.",
                    "Peers stop recognizing this PC. Folders are added paused so nothing syncs or deletes until you have checked the paths.",
                    components,
                    home,
                    new Dictionary<string, string>
                    {
                        ["source"] = home,
                        ["deviceId"] = deviceId,
                        ["version"] = version,
                        ["folders"] = folders.ToString(),
                        ["devices"] = devices.ToString(),
                        ["preview"] = scrubbed,
                        ["oldProfile"] = context.OldProfileRoot,
                        ["lastActivity"] = activity.ToString("O"),
                        ["syncthingExeVersion"] = exeVersion,
                        ["guiTls"] = guiTls ? "1" : "0",
                        ["hasGui"] = hasGui ? "1" : "0",
                    }),
                activity));
            DetectorWalk.AddTreeBadge(badges, context.OldProfileRoot, home, "Syncthing", deviceId);
            foreach (SyncthingFolder folder in SyncthingConfig.ListFolders(xml))
            {
                DetectorWalk.AddTreeBadge(
                    badges,
                    context.OldProfileRoot,
                    folder.Path,
                    "Syncthing folder",
                    folder.Label);
            }
        }

        DateTimeOffset newest = pending.Count == 0
            ? DateTimeOffset.MinValue
            : pending.Max(static item => item.Activity);
        foreach ((RecipeCard card, DateTimeOffset activity) in pending)
        {
            bool active = activity == newest && newest > DateTimeOffset.MinValue;
            Dictionary<string, string> facts = new(card.Facts, StringComparer.Ordinal)
            {
                ["probablyActive"] = active ? "1" : "0",
            };
            cards.Add(
                card with
                {
                    Title = active ? card.Title + " (probably active)" : card.Title,
                    Facts = facts,
                });
        }

        return new DetectResult(cards, badges);
    }

    public PlanResult Plan(CardDecisions decisions, DestinationContext destination)
    {
        string source = decisions.Card.Facts["source"];
        string dest = DestinationHome(destination);
        List<RecipeWrite> writes = [];
        if (RecipeDecisions.ShouldRestore(decisions, "identity"))
        {
            writes.Add(Copy(source, dest, "cert.pem", "identity"));
            writes.Add(Copy(source, dest, "key.pem", "identity"));
        }

        if (RecipeDecisions.ShouldRestore(decisions, "config"))
        {
            string xml = destination.SafeFs.ReadAllText(Path.Combine(source, "config.xml"));
            IReadOnlyDictionary<string, string> overrides = RecipeFolderMap.Parse(
                decisions.Card.Facts.GetValueOrDefault(RecipeFolderMap.FactKey));
            string rewritten = SyncthingConfig.Rewrite(
                xml,
                decisions.Card.Facts.GetValueOrDefault("oldProfile") ?? string.Empty,
                destination.DestinationProfileRoot,
                overrides.Count == 0 ? null : overrides);
            writes.Add(
                new RecipeWrite(
                    RecipeWriteKind.WriteContent,
                    null,
                    Path.Combine(dest, "config.xml"),
                    rewritten,
                    rewritten.Length,
                    "config"));
        }

        if (RecipeDecisions.ShouldRestore(decisions, "gui-tls"))
        {
            if (destination.SafeFs.FileExists(Path.Combine(source, "https-cert.pem")))
            {
                writes.Add(Copy(source, dest, "https-cert.pem", "gui-tls"));
            }

            if (destination.SafeFs.FileExists(Path.Combine(source, "https-key.pem")))
            {
                writes.Add(Copy(source, dest, "https-key.pem", "gui-tls"));
            }
        }

        if (RecipeDecisions.ShouldRestore(decisions, "gui"))
        {
            string gui = Path.Combine(source, "gui");
            if (destination.SafeFs.DirectoryExists(gui))
            {
                AnkiRecipe.AddTree(
                    destination.SafeFs,
                    gui,
                    Path.Combine(dest, "gui"),
                    gui,
                    "gui",
                    writes,
                    static _ => false);
            }
        }

        return new PlanResult(decisions.Card, writes);
    }

    public Task ExecuteAsync(PlanResult plan, IRecipeJournal journal, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public RecipeVerifyResult Verify(PlanResult plan)
    {
        RecipeWrite? config = plan.Writes.FirstOrDefault(static write =>
            write.DestinationPath.EndsWith("config.xml", StringComparison.OrdinalIgnoreCase) &&
            write.Kind == RecipeWriteKind.WriteContent);
        if (plan.Writes.Any(static write =>
                write.DestinationPath.Contains("index-v", StringComparison.OrdinalIgnoreCase)))
        {
            return new RecipeVerifyResult(false, "Index must never be restored");
        }

        RecipeVerifyResult present = DetectorWalk.FilesPresentMatchingSourceLength(
            plan,
            "Syncthing identity present",
            "Syncthing destination missing",
            "Syncthing destination size does not match the source");
        if (!present.Ok)
        {
            return present;
        }

        if (config is not null)
        {
            string destXml = File.ReadAllText(config.DestinationPath);
            if (!SyncthingConfig.AllFoldersPaused(destXml))
            {
                return new RecipeVerifyResult(false, "Not every Syncthing folder is paused");
            }

            XDocument destDocument = SyncthingConfig.Load(destXml);
            if (!int.TryParse(plan.Card.Facts.GetValueOrDefault("folders"), out int folders) ||
                SyncthingConfig.FolderCount(destDocument) != folders)
            {
                return new RecipeVerifyResult(false, "Folder count does not match the source");
            }

            if (!int.TryParse(plan.Card.Facts.GetValueOrDefault("devices"), out int devices) ||
                SyncthingConfig.DeviceCount(destDocument) != devices)
            {
                return new RecipeVerifyResult(false, "Device count does not match the source");
            }
        }

        RecipeWrite? cert = plan.Writes.FirstOrDefault(static write =>
            write.DestinationPath.EndsWith("cert.pem", StringComparison.OrdinalIgnoreCase) &&
            write.ComponentKey == "identity");
        if (cert is not null)
        {
            string destId = SyncthingDeviceId.FromCertificatePem(File.ReadAllText(cert.DestinationPath));
            if (!destId.Equals(plan.Card.Facts.GetValueOrDefault("deviceId"), StringComparison.Ordinal))
            {
                return new RecipeVerifyResult(false, "Restored device ID does not match the source");
            }
        }

        RecipeWrite? key = plan.Writes.FirstOrDefault(static write =>
            write.DestinationPath.EndsWith("key.pem", StringComparison.OrdinalIgnoreCase) &&
            write.ComponentKey == "identity");
        if (key?.SourcePath is string sourceKey &&
            File.Exists(sourceKey) &&
            !Sha256(sourceKey).SequenceEqual(Sha256(key.DestinationPath)))
        {
            return new RecipeVerifyResult(false, "Restored key.pem hash does not match the source");
        }

        return new RecipeVerifyResult(true, "Syncthing identity present, folders paused, index omitted");
    }

    public async Task<RecipeVerifyResult> VerifyAsync(
        PlanResult plan,
        CancellationToken cancellationToken = default)
    {
        RecipeVerifyResult files = Verify(plan);
        if (!files.Ok || plan.Destination is null)
        {
            return files;
        }

        RecipeWrite? cert = plan.Writes.FirstOrDefault(static write =>
            write.DestinationPath.EndsWith("cert.pem", StringComparison.OrdinalIgnoreCase) &&
            write.ComponentKey == "identity");
        if (cert is null)
        {
            return files;
        }

        string destHome = Path.GetDirectoryName(cert.DestinationPath) ?? string.Empty;
        ProcessResult result;
        try
        {
            result = await plan.Destination.ProcessRunner
                .RunAsync(
                    new ProcessRequest(
                        "syncthing.exe",
                        ["--home", destHome, "--device-id"],
                        Timeout: TimeSpan.FromSeconds(15)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return files;
        }

        string printed = result.StandardOutput.Trim();
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(printed))
        {
            return files;
        }

        if (!printed.Equals(plan.Card.Facts.GetValueOrDefault("deviceId"), StringComparison.Ordinal))
        {
            return new RecipeVerifyResult(false, "syncthing --device-id does not match the source");
        }

        return new RecipeVerifyResult(true, "Syncthing CLI device ID matches");
    }

    public IReadOnlyList<Prerequisite> Prerequisites(PlanResult plan) =>
    [
        new Prerequisite("syncthing", "Syncthing must be closed before the identity is restored."),
        new Prerequisite("SyncTrayzor", "SyncTrayzor must be closed before the identity is restored."),
    ];

    private static byte[] Sha256(string path)
    {
        return System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path));
    }

    private static RecipeWrite Copy(string sourceHome, string destHome, string name, string component)
    {
        return new RecipeWrite(
            RecipeWriteKind.CopyFile,
            Path.Combine(sourceHome, name),
            Path.Combine(destHome, name),
            null,
            1,
            component);
    }

    private static string DestinationHome(DestinationContext destination)
    {
        string local = Path.Combine(destination.DestinationProfileRoot, "AppData", "Local", "Syncthing");
        if (destination.SafeFs.FileExists(Path.Combine(local, "config.xml")) &&
            destination.SafeFs.FileExists(Path.Combine(local, "cert.pem")) &&
            destination.SafeFs.FileExists(Path.Combine(local, "key.pem")))
        {
            return Path.Combine(destination.DestinationProfileRoot, "AppData", "Local", "Syncthing.recovered");
        }

        return local;
    }

    private static DateTimeOffset LastActivity(SafeFs safeFs, string home)
    {
        DateTimeOffset latest = DateTimeOffset.MinValue;
        foreach (string name in new[] { "config.xml", "index-v2", "index-v0.14.0.db", "logs" })
        {
            string path = Path.Combine(home, name);
            if (!safeFs.FileExists(path) && !safeFs.DirectoryExists(path))
            {
                continue;
            }

            try
            {
                DateTime utc = File.GetLastWriteTimeUtc(path);
                if (utc > latest.UtcDateTime)
                {
                    latest = utc;
                }
            }
            catch (IOException)
            {
            }
        }

        return latest;
    }

    private static string SyncTrayzorExeVersion(ProfileContext context)
    {
        string exe = Path.Combine(
            context.OldProfileRoot,
            "AppData",
            "Roaming",
            "SyncTrayzor",
            "syncthing.exe");
        if (!context.SafeFs.FileExists(exe))
        {
            return string.Empty;
        }

        try
        {
            return FileVersionInfo.GetVersionInfo(exe).FileVersion ?? string.Empty;
        }
        catch (Exception exception) when (exception is FileNotFoundException or BadImageFormatException)
        {
            return string.Empty;
        }
    }

    private static bool IsHome(SafeFs safeFs, string home)
    {
        return safeFs.FileExists(Path.Combine(home, "config.xml")) &&
            safeFs.FileExists(Path.Combine(home, "cert.pem")) &&
            safeFs.FileExists(Path.Combine(home, "key.pem"));
    }

    private static bool IsIndexedHome(ProfileContext context, string home)
    {
        if (DetectorWalk.TryIndexedRelative(context, home, out RecipeIndex index, out string relative))
        {
            return DetectorWalk.IndexedImmediatePath(index, home, relative, "config.xml") is not null &&
                DetectorWalk.IndexedImmediatePath(index, home, relative, "cert.pem") is not null &&
                DetectorWalk.IndexedImmediatePath(index, home, relative, "key.pem") is not null;
        }

        return IsHome(context.SafeFs, home);
    }

    private static IReadOnlyList<string> CandidateHomes(ProfileContext context)
    {
        HashSet<string> homes = new(StringComparer.OrdinalIgnoreCase);
        Add(homes, Path.Combine(context.OldProfileRoot, "AppData", "Local", "Syncthing"));
        Add(homes, Path.Combine(context.OldProfileRoot, "AppData", "Roaming", "Syncthing"));
        string trayzor = Path.Combine(context.OldProfileRoot, "AppData", "Roaming", "SyncTrayzor", "config.xml");
        if (context.SafeFs.FileExists(trayzor))
        {
            try
            {
                XDocument document = SyncthingConfig.Load(context.SafeFs.ReadAllText(trayzor));
                XElement? custom = document.Descendants().FirstOrDefault(static element =>
                    element.Name.LocalName.Contains("Syncthing", StringComparison.OrdinalIgnoreCase) &&
                    element.Name.LocalName.Contains("Home", StringComparison.OrdinalIgnoreCase));
                if (custom is not null && !string.IsNullOrWhiteSpace(custom.Value) && Path.IsPathRooted(custom.Value))
                {
                    Add(homes, custom.Value);
                }
            }
            catch (System.Xml.XmlException)
            {
            }
        }

        string sourceRoot = Path.GetFullPath(Path.Combine(context.OldProfileRoot, "..", ".."));
        Add(homes, Path.Combine(sourceRoot, "ProgramData", "Syncthing"));
        Add(
            homes,
            Path.Combine(sourceRoot, "Windows", "System32", "config", "systemprofile", "AppData", "Local", "Syncthing"));
        if (context.Index is { } index)
        {
            foreach (string parent in index.ParentsOfChildNamed("cert.pem"))
            {
                Add(homes, parent);
            }
        }

        return homes.ToList();
    }

    private static void Add(HashSet<string> homes, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        homes.Add(Path.GetFullPath(path));
    }
}
