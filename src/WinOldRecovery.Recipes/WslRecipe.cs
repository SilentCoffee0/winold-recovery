using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Processes;
using WinOldRecovery.Core.Recipes;
using WinOldRecovery.Core.Registry;

namespace WinOldRecovery.Recipes;

public sealed class WslRecipe : IRecipe
{
    public string Id => "wsl";

    public DetectResult Detect(ProfileContext context)
    {
        List<RecipeCard> cards = [];
        List<(string RelativePath, string Kind, string Detail)> badges = [];
        IReadOnlyList<WslLxss.Distro> lxss = ReadLxss(context);
        bool wslInstalled = ProbeWslInstalled(context.ProcessRunner);
        foreach (string disk in FindDisks(context.SafeFs, Path.Combine(context.OldProfileRoot, "AppData", "Local")))
        {
            WslLxss.Distro? lxssDistro = WslLxss.Match(disk, lxss);
            string name = !string.IsNullOrWhiteSpace(lxssDistro?.Name) ? lxssDistro.Name : DistroName(disk);
            bool docker = disk.Contains("Docker", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("docker", StringComparison.OrdinalIgnoreCase);
            long size = 0;
            long allocated = 0;
            string lastModified = string.Empty;
            if (context.SafeFs.TryReadSizes(disk, out size, out allocated))
            {
                try
                {
                    lastModified = File.GetLastWriteTimeUtc(disk).ToString("O");
                }
                catch (IOException)
                {
                }
            }
            else
            {
                try
                {
                    FileInfo info = new(disk);
                    size = info.Length;
                    lastModified = info.LastWriteTimeUtc.ToString("O");
                }
                catch (IOException)
                {
                }
            }

            cards.Add(
                new RecipeCard(
                    Id,
                    (docker ? "Docker disk — " : "WSL disk — ") + name,
                    "A whole Linux environment: packages, projects, dotfiles, databases. It lives in one virtual disk file.",
                    "We copy the disk out of Windows.old and verify the VHDX header. Registration with wsl.exe is optional and never runs against Windows.old.",
                    "Copy of " + Path.GetFileName(disk) + " to AppData\\Local\\wsl\\recovered. WSL 1 rootfs folders are listed as manual-only.",
                    "Reinstall the distro from the Store if the disk is expendable.",
                    "Store images can be re-downloaded; named Docker volumes may not.",
                    "The Linux install in Windows.old is gone after purge.",
                    [
                        new RecipeComponent(
                            "disk",
                            "Copy VHDX",
                            size + " bytes",
                            docker ? Decision.Undecided : Decision.Restore,
                            false,
                            null,
                            false),
                        new RecipeComponent(
                            "register",
                            "Register with wsl.exe",
                            wslInstalled
                                ? "wsl --import-in-place after copy; never against Windows.old"
                                : "Install WSL first (wsl --install --no-distribution) then reboot",
                            docker || !wslInstalled ? Decision.Undecided : Decision.Restore,
                            false,
                            null,
                            false),
                    ],
                    context.ProfileName + ":" + disk,
                    new Dictionary<string, string>
                    {
                        ["source"] = disk,
                        ["name"] = name,
                        ["docker"] = docker ? "1" : "0",
                        ["fileSize"] = size.ToString(),
                        ["allocatedSize"] = allocated.ToString(),
                        ["lastModified"] = lastModified,
                        ["defaultUid"] = lxssDistro?.DefaultUid ?? string.Empty,
                        ["wslVersion"] = lxssDistro?.Version ?? string.Empty,
                        ["wslInstalled"] = wslInstalled ? "1" : "0",
                        ["defaultDistribution"] = lxssDistro is { IsDefault: true } ? "1" : "0",
                        ["defaultUserCommands"] = FormatDefaultUserCommands(name, lxssDistro?.DefaultUid ?? string.Empty),
                    }));
            DetectorWalk.AddTreeBadge(
                badges,
                context.OldProfileRoot,
                disk,
                docker ? "Docker" : "WSL",
                name);
        }

        string wsl1 = Path.Combine(context.OldProfileRoot, "AppData", "Local", "lxss");
        if (context.SafeFs.DirectoryExists(wsl1))
        {
            cards.Add(
                new RecipeCard(
                    Id,
                    "WSL 1 — manual migration only",
                    "A WSL 1 rootfs. Linux metadata lives in NTFS extended attributes that a normal copy loses.",
                    "v0.1 does not copy WSL 1 filesystems.",
                    "Nothing. Export from the old install with wsl --export before purge if you still can.",
                    "Reinstall a WSL 2 distro.",
                    "Does not regenerate.",
                    "The WSL 1 tree is left behind.",
                    [
                        new RecipeComponent(
                            "wsl1",
                            "WSL 1 rootfs",
                            "Manual only",
                            Decision.LeaveBehind,
                            true,
                            "WSL 1 metadata is not preserved by a Windows copy.",
                            false),
                    ],
                    context.ProfileName + ":wsl1",
                    new Dictionary<string, string> { ["source"] = wsl1, ["wsl1"] = "1" }));
            DetectorWalk.AddTreeBadge(badges, context.OldProfileRoot, wsl1, "WSL", "WSL 1 (manual)");
        }

        return new DetectResult(cards, badges);
    }

    public PlanResult Plan(CardDecisions decisions, DestinationContext destination)
    {
        if (!RecipeDecisions.ShouldRestore(decisions, "disk"))
        {
            return new PlanResult(decisions.Card, []);
        }

        string source = decisions.Card.Facts["source"];
        string dest = Path.Combine(
            destination.DestinationProfileRoot,
            "AppData",
            "Local",
            "wsl",
            "recovered",
            decisions.Card.Facts["name"],
            Path.GetFileName(source));
        return new PlanResult(
            decisions.Card,
            [new RecipeWrite(RecipeWriteKind.CopyFile, source, dest, null, 1, "disk")]);
    }

    public Task ExecuteAsync(PlanResult plan, IRecipeJournal journal, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public static IReadOnlyList<WinOldRecovery.Core.Processes.ProcessRequest> CreateRegisterRequests(
        string distroName,
        string destinationVhdx)
    {
        return
        [
            new("wsl.exe", ["--shutdown"], Timeout: TimeSpan.FromSeconds(60)),
            new("wsl.exe", ["--import-in-place", distroName, destinationVhdx], Timeout: TimeSpan.FromMinutes(2)),
        ];
    }

    public static IReadOnlyList<ProcessRequest> CreateDefaultUserRequests(
        string distroName,
        string defaultUid)
    {
        TimeSpan timeout = TimeSpan.FromSeconds(30);
        List<ProcessRequest> requests =
        [
            new("wsl.exe", ["-d", distroName, "-u", "root", "cat", "/etc/wsl.conf"], Timeout: timeout),
        ];
        if (!string.IsNullOrWhiteSpace(defaultUid))
        {
            requests.Add(
                new(
                    "wsl.exe",
                    ["-d", distroName, "-u", "root", "getent", "passwd", defaultUid],
                    Timeout: timeout));
        }

        requests.Add(new("wsl.exe", ["--terminate", distroName], Timeout: timeout));
        return requests;
    }

    public static string FormatDefaultUserCommands(string distroName, string defaultUid)
    {
        string uid = string.IsNullOrWhiteSpace(defaultUid) ? "<DefaultUid>" : defaultUid;
        return string.Join(
            Environment.NewLine,
            "wsl.exe -d " + distroName + " -u root cat /etc/wsl.conf",
            "wsl.exe -d " + distroName + " -u root getent passwd " + uid,
            "wsl.exe --manage " + distroName + " --set-default-user <name-from-getent>",
            "wsl.exe --terminate " + distroName);
    }

    public RecipeVerifyResult Verify(PlanResult plan)
    {
        RecipeWrite? disk = plan.Writes.FirstOrDefault(static write => write.ComponentKey == "disk");
        if (disk is null)
        {
            return new RecipeVerifyResult(true, "No disk copy planned");
        }

        if (!File.Exists(disk.DestinationPath))
        {
            return new RecipeVerifyResult(false, "VHDX missing");
        }

        using FileStream stream = File.OpenRead(disk.DestinationPath);
        Span<byte> header = stackalloc byte[8];
        int read = stream.Read(header);
        bool magic = read == 8 && System.Text.Encoding.ASCII.GetString(header) == "vhdxfile";
        return new RecipeVerifyResult(magic, magic ? "VHDX header present" : "VHDX header missing");
    }

    public IReadOnlyList<Prerequisite> Prerequisites(PlanResult plan) =>
        [new Prerequisite("wsl", "Close WSL before registering a copied disk.")];

    private static IReadOnlyList<WslLxss.Distro> ReadLxss(ProfileContext context)
    {
        string hivePath = Path.Combine(context.OldProfileRoot, "NTUSER.DAT");
        if (!context.SafeFs.FileExists(hivePath))
        {
            return [];
        }

        try
        {
            OfflineRegistryHive hive = OfflineRegistryHive.OpenCopyAsync(
                    hivePath,
                    context.SessionTemporaryDirectory,
                    context.SafeFs)
                .GetAwaiter()
                .GetResult();
            IReadOnlyDictionary<string, string> root = hive.GetStringValues(WslLxss.KeyPath);
            Dictionary<string, IReadOnlyDictionary<string, string>> children = new(StringComparer.OrdinalIgnoreCase);
            foreach (string name in hive.GetSubKeyNames(WslLxss.KeyPath))
            {
                children[name] = hive.GetStringValues(WslLxss.KeyPath + "\\" + name);
            }

            return WslLxss.Read(root, children, context.OldProfileRoot, context.ProfileName);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return [];
        }
    }

    private static bool ProbeWslInstalled(IProcessRunner runner)
    {
        try
        {
            ProcessResult result = runner
                .RunAsync(
                    new ProcessRequest("wsl.exe", ["--version"], Timeout: TimeSpan.FromSeconds(15)))
                .GetAwaiter()
                .GetResult();
            return result.ExitCode == 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return false;
        }
    }

    private static IEnumerable<string> FindDisks(SafeFs safeFs, string root)
    {
        if (!safeFs.DirectoryExists(root))
        {
            yield break;
        }

        Stack<string> stack = new();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string directory = stack.Pop();
            foreach (string entry in safeFs.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    stack.Push(entry);
                    continue;
                }

                string name = Path.GetFileName(entry);
                if (name.Equals("ext4.vhdx", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("docker_data.vhdx", StringComparison.OrdinalIgnoreCase))
                {
                    yield return entry;
                }
            }
        }
    }

    private static string DistroName(string disk)
    {
        string? parent = Path.GetFileName(Path.GetDirectoryName(disk));
        if (string.Equals(parent, "LocalState", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(disk))) ?? "distro";
        }

        return string.IsNullOrEmpty(parent) ? "distro" : parent;
    }
}
