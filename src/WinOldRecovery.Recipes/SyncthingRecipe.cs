using System.Xml.Linq;
using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Recipes;

namespace WinOldRecovery.Recipes;

public sealed class SyncthingRecipe : IRecipe
{
    public string Id => "syncthing";

    public DetectResult Detect(ProfileContext context)
    {
        List<RecipeCard> cards = [];
        List<(string RelativePath, string Kind, string Detail)> badges = [];
        foreach (string home in CandidateHomes(context))
        {
            if (!IsHome(context.SafeFs, home))
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
            cards.Add(
                new RecipeCard(
                    Id,
                    "Syncthing — " + deviceId,
                    "This computer's Syncthing identity and folder list. Other devices trust this ID.",
                    "If you start fresh, every peer must be re-paired and every folder re-shared.",
                    "cert.pem, key.pem, and a rewritten config.xml with every folder paused. The index is never restored.",
                    "Start a new device ID and re-share folders.",
                    "The index database regenerates. The device ID does not.",
                    "Peers stop recognizing this PC. Folders are added paused so nothing syncs or deletes until you have checked the paths.",
                    [
                        new RecipeComponent("identity", "Identity", "cert.pem and key.pem", Decision.Restore, false, null, true),
                        new RecipeComponent("config", "Configuration", folders + " folders, version " + version, Decision.Restore, false, null, true),
                        new RecipeComponent(
                            "index",
                            "Index database",
                            "Never restored — missing files would look like deletions",
                            Decision.Undecided,
                            true,
                            "Syncthing rebuilds the index. Restoring it with incomplete folder data can delete files on peers.",
                            false),
                    ],
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
                    }));
            badges.Add((Path.GetRelativePath(context.OldProfileRoot, home), "Syncthing", deviceId));
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
            string rewritten = SyncthingConfig.Rewrite(
                xml,
                decisions.Card.Facts.GetValueOrDefault("oldProfile") ?? string.Empty,
                destination.DestinationProfileRoot);
            writes.Add(
                new RecipeWrite(
                    RecipeWriteKind.WriteContent,
                    null,
                    Path.Combine(dest, "config.xml"),
                    rewritten,
                    rewritten.Length,
                    "config"));
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
        if (config?.Utf8Content is string xml && !SyncthingConfig.AllFoldersPaused(xml))
        {
            return new RecipeVerifyResult(false, "Not every Syncthing folder is paused");
        }

        if (plan.Writes.Any(static write =>
                write.DestinationPath.Contains("index-v", StringComparison.OrdinalIgnoreCase)))
        {
            return new RecipeVerifyResult(false, "Index must never be restored");
        }

        bool ok = plan.Writes.All(static write => File.Exists(write.DestinationPath));
        return new RecipeVerifyResult(ok, ok ? "Syncthing identity present, folders paused, index omitted" : "Syncthing destination missing");
    }

    public IReadOnlyList<Prerequisite> Prerequisites(PlanResult plan) =>
    [
        new Prerequisite("syncthing", "Syncthing must be closed before the identity is restored."),
        new Prerequisite("SyncTrayzor", "SyncTrayzor must be closed before the identity is restored."),
    ];

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

    private static bool IsHome(SafeFs safeFs, string home)
    {
        return safeFs.FileExists(Path.Combine(home, "config.xml")) &&
            safeFs.FileExists(Path.Combine(home, "cert.pem")) &&
            safeFs.FileExists(Path.Combine(home, "key.pem"));
    }

    private static IReadOnlyList<string> CandidateHomes(ProfileContext context)
    {
        List<string> homes =
        [
            Path.Combine(context.OldProfileRoot, "AppData", "Local", "Syncthing"),
            Path.Combine(context.OldProfileRoot, "AppData", "Roaming", "Syncthing"),
        ];
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
                    homes.Add(custom.Value);
                }
            }
            catch (System.Xml.XmlException)
            {
            }
        }

        string sourceRoot = Path.GetFullPath(Path.Combine(context.OldProfileRoot, "..", ".."));
        homes.Add(Path.Combine(sourceRoot, "ProgramData", "Syncthing"));
        homes.Add(Path.Combine(sourceRoot, "Windows", "System32", "config", "systemprofile", "AppData", "Local", "Syncthing"));
        return homes;
    }
}
