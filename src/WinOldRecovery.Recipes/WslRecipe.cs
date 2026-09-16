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
        Dictionary<string, string> facts = new(decisions.Card.Facts, StringComparer.Ordinal);
        facts["register"] = RecipeDecisions.ShouldRestore(decisions, "register") ? "1" : "0";
        return new PlanResult(
            decisions.Card with { Facts = facts },
            [new RecipeWrite(RecipeWriteKind.CopyFile, source, dest, null, 1, "disk")]);
    }

    public async Task ExecuteAsync(PlanResult plan, IRecipeJournal journal, CancellationToken cancellationToken = default)
    {
        if (plan.Card.Facts.GetValueOrDefault("register") != "1" ||
            plan.Destination is null)
        {
            return;
        }

        RecipeWrite? disk = plan.Writes.FirstOrDefault(static write => write.ComponentKey == "disk");
        if (disk is null)
        {
            return;
        }

        string destinationVhdx = Path.GetFullPath(disk.DestinationPath);
        string sourceVhdx = Path.GetFullPath(plan.Card.Facts["source"]);
        string sourcePrefix = sourceVhdx.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (destinationVhdx.Equals(sourceVhdx, StringComparison.OrdinalIgnoreCase) ||
            destinationVhdx.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("WSL register refuses a disk that is still under Windows.old.");
        }

        await journal.StartedAsync("register", cancellationToken).ConfigureAwait(false);
        try
        {
            IProcessRunner runner = plan.Destination.ProcessRunner;
            ProcessResult listed = await runner
                .RunAsync(
                    new ProcessRequest("wsl.exe", ["-l", "-v"], Timeout: TimeSpan.FromSeconds(30)),
                    cancellationToken)
                .ConfigureAwait(false);
            if (listed.ExitCode != 0)
            {
                throw new IOException("wsl.exe -l -v exited " + listed.ExitCode + ".");
            }

            string registerName = ChooseRegisteredDistroName(plan.Card.Facts["name"], listed.StandardOutput);
            RememberRegisterName(plan.Card, registerName);
            foreach (ProcessRequest request in CreateRegisterRequests(registerName, destinationVhdx))
            {
                ProcessResult result = await runner
                    .RunAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                if (result.ExitCode != 0)
                {
                    throw new IOException(
                        "wsl.exe " + string.Join(' ', request.Arguments) + " exited " + result.ExitCode + ".");
                }
            }

            await journal.CompletedAsync("register", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await journal.FailedAsync("register", exception.Message, cancellationToken).ConfigureAwait(false);
            throw;
        }

        await SetDefaultUserAsync(plan, journal, cancellationToken).ConfigureAwait(false);
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

    public static bool TryParseWslConfUserDefault(string conf, out string userName)
    {
        userName = string.Empty;
        bool inUser = false;
        using StringReader reader = new(conf);
        while (reader.ReadLine() is { } line)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                inUser = trimmed.Equals("[user]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inUser || !trimmed.StartsWith("default", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int equals = trimmed.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            string value = trimmed[(equals + 1)..].Trim();
            if (IsSafeLinuxUserName(value))
            {
                userName = value;
                return true;
            }
        }

        return false;
    }

    public static bool TryParsePasswdName(string passwdLine, out string userName)
    {
        userName = string.Empty;
        int colon = passwdLine.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        string value = passwdLine[..colon].Trim();
        if (!IsSafeLinuxUserName(value))
        {
            return false;
        }

        userName = value;
        return true;
    }

    public static bool IsSafeLinuxUserName(string value)
    {
        if (value.Length is < 1 or > 32)
        {
            return false;
        }

        char first = value[0];
        if (first is not ('_' or (>= 'a' and <= 'z')))
        {
            return false;
        }

        for (int i = 1; i < value.Length; i++)
        {
            char c = value[i];
            if (c is not ('_' or '-' or (>= 'a' and <= 'z') or (>= '0' and <= '9')))
            {
                return false;
            }
        }

        return true;
    }

    public static string ChooseRegisteredDistroName(string requested, string listing)
    {
        string sourceName = string.IsNullOrWhiteSpace(requested) ? "distro" : requested.Trim();
        string text = listing.Replace("\0", string.Empty, StringComparison.Ordinal);
        string candidate = sourceName;
        int suffix = 0;
        while (ListingContainsDistro(text, candidate))
        {
            suffix++;
            candidate = suffix == 1
                ? sourceName + "-recovered"
                : sourceName + "-recovered-" + suffix;
            if (suffix > 100)
            {
                throw new InvalidOperationException("No free WSL distro name for " + sourceName + ".");
            }
        }

        return candidate;
    }

    public static bool ListingContainsDistro(string listing, string name)
    {
        using StringReader reader = new(listing);
        while (reader.ReadLine() is { } line)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith('*'))
            {
                trimmed = trimmed.TrimStart('*').TrimStart();
            }

            int space = trimmed.IndexOfAny([' ', '\t']);
            string token = space < 0 ? trimmed : trimmed[..space];
            if (token.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string RegisteredDistroName(RecipeCard card)
    {
        string? registered = card.Facts.GetValueOrDefault("registerName");
        return string.IsNullOrWhiteSpace(registered) ? card.Facts["name"] : registered;
    }

    private static void RememberRegisterName(RecipeCard card, string registerName)
    {
        if (card.Facts is Dictionary<string, string> writable)
        {
            writable["registerName"] = registerName;
        }
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

    private static async Task SetDefaultUserAsync(
        PlanResult plan,
        IRecipeJournal journal,
        CancellationToken cancellationToken)
    {
        IProcessRunner runner = plan.Destination!.ProcessRunner;
        string distroName = RegisteredDistroName(plan.Card);
        string defaultUid = plan.Card.Facts.GetValueOrDefault("defaultUid") ?? string.Empty;
        TimeSpan timeout = TimeSpan.FromSeconds(30);
        await journal.StartedAsync("defaultUser", cancellationToken).ConfigureAwait(false);
        try
        {
            ProcessResult conf = await runner
                .RunAsync(
                    new ProcessRequest(
                        "wsl.exe",
                        ["-d", distroName, "-u", "root", "cat", "/etc/wsl.conf"],
                        Timeout: timeout),
                    cancellationToken)
                .ConfigureAwait(false);
            if (conf.ExitCode == 0 && TryParseWslConfUserDefault(conf.StandardOutput, out _))
            {
                await journal.CompletedAsync("defaultUser", cancellationToken).ConfigureAwait(false);
                return;
            }

            string? userName = null;
            if (!string.IsNullOrWhiteSpace(defaultUid))
            {
                ProcessResult passwd = await runner
                    .RunAsync(
                        new ProcessRequest(
                            "wsl.exe",
                            ["-d", distroName, "-u", "root", "getent", "passwd", defaultUid],
                            Timeout: timeout),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (passwd.ExitCode != 0 || !TryParsePasswdName(passwd.StandardOutput, out string parsed))
                {
                    throw new IOException(
                        "wsl.exe getent passwd " + defaultUid + " did not return a Linux user name.");
                }

                userName = parsed;
            }

            if (userName is null)
            {
                await journal.CompletedAsync("defaultUser", cancellationToken).ConfigureAwait(false);
                return;
            }

            ProcessResult manage = await runner
                .RunAsync(
                    new ProcessRequest(
                        "wsl.exe",
                        ["--manage", distroName, "--set-default-user", userName],
                        Timeout: timeout),
                    cancellationToken)
                .ConfigureAwait(false);
            if (manage.ExitCode != 0)
            {
                ProcessResult append = await runner
                    .RunAsync(
                        new ProcessRequest(
                            "wsl.exe",
                            [
                                "-d",
                                distroName,
                                "-u",
                                "root",
                                "--",
                                "sh",
                                "-c",
                                "printf '\\n[user]\\ndefault=" + userName + "\\n' >> /etc/wsl.conf",
                            ],
                            Timeout: timeout),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (append.ExitCode != 0)
                {
                    throw new IOException(
                        "wsl.exe could not set the default user for " + distroName + ".");
                }
            }

            ProcessResult terminate = await runner
                .RunAsync(
                    new ProcessRequest("wsl.exe", ["--terminate", distroName], Timeout: timeout),
                    cancellationToken)
                .ConfigureAwait(false);
            if (terminate.ExitCode != 0)
            {
                throw new IOException("wsl.exe --terminate " + distroName + " exited " + terminate.ExitCode + ".");
            }

            await journal.CompletedAsync("defaultUser", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await journal.FailedAsync("defaultUser", exception.Message, cancellationToken).ConfigureAwait(false);
            throw;
        }
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

    public async Task<RecipeVerifyResult> VerifyAsync(
        PlanResult plan,
        CancellationToken cancellationToken = default)
    {
        RecipeVerifyResult header = Verify(plan);
        if (!header.Ok ||
            plan.Card.Facts.GetValueOrDefault("register") != "1" ||
            plan.Destination is null)
        {
            return header;
        }

        string name = RegisteredDistroName(plan.Card);
        ProcessResult listed = await plan.Destination.ProcessRunner
            .RunAsync(new ProcessRequest("wsl.exe", ["-l", "-v"], Timeout: TimeSpan.FromSeconds(30)), cancellationToken)
            .ConfigureAwait(false);
        if (listed.ExitCode != 0)
        {
            return new RecipeVerifyResult(false, "wsl -l -v exited " + listed.ExitCode + ".");
        }

        if (!string.IsNullOrWhiteSpace(listed.StandardOutput) &&
            listed.StandardOutput.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return new RecipeVerifyResult(false, "wsl -l -v does not list " + name + ".");
        }

        ProcessResult ping = await plan.Destination.ProcessRunner
            .RunAsync(
                new ProcessRequest(
                    "wsl.exe",
                    ["-d", name, "-u", "root", "--", "true"],
                    Timeout: TimeSpan.FromSeconds(30)),
                cancellationToken)
            .ConfigureAwait(false);
        if (ping.ExitCode != 0)
        {
            return new RecipeVerifyResult(
                false,
                "wsl -d " + name + " -u root -- true exited " + ping.ExitCode + ".");
        }

        return new RecipeVerifyResult(true, header.Detail + "; registered");
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
