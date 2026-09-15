using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Recipes;

namespace WinOldRecovery.Recipes;

public sealed class SshRecipe : IRecipe
{
    public string Id => "ssh";

    public DetectResult Detect(ProfileContext context)
    {
        List<RecipeCard> cards = [];
        List<(string RelativePath, string Kind, string Detail)> badges = [];
        string ssh = Path.Combine(context.OldProfileRoot, ".ssh");
        if (context.SafeFs.DirectoryExists(ssh) &&
            TryListFiles(context.SafeFs, ssh, out List<string> names, out List<string> fingerprints))
        {
            cards.Add(
                new RecipeCard(
                    Id,
                    "SSH keys (" + context.ProfileName + ")",
                    "Your SSH identities and the hosts you have trusted.",
                    "Without these keys you cannot log in to servers that trust this computer.",
                    "Private keys, public keys, config and known_hosts. Existing destination files are never overwritten; a conflict is saved as *.from-windows-old. Private key material is never shown.",
                    "Create new keys and update every server.",
                    "Keys do not regenerate.",
                    "You lose SSH access until you replace the keys on every host.",
                    [
                        new RecipeComponent("files", "SSH files", names.Count + " files", Decision.Restore, false, null, true),
                    ],
                    context.ProfileName,
                    new Dictionary<string, string>
                    {
                        ["source"] = ssh,
                        ["names"] = string.Join("|", names),
                        ["fingerprints"] = string.Join(", ", fingerprints),
                        ["component"] = "files",
                    }));
            badges.Add((".ssh", "SSH", "keys"));
        }

        string server = Path.GetFullPath(Path.Combine(context.OldProfileRoot, "..", "..", "ProgramData", "ssh"));
        if (context.SafeFs.DirectoryExists(server) &&
            TryListFiles(context.SafeFs, server, out List<string> hostNames, out _))
        {
            cards.Add(
                new RecipeCard(
                    Id,
                    "OpenSSH Server host keys",
                    "sshd host keys and sshd_config from the old ProgramData\\ssh folder.",
                    "These identify this PC to SSH clients. They are not your user keys.",
                    "Copied to Recovered\\OpenSSH-Server. Review them before replacing C:\\ProgramData\\ssh.",
                    "Generate new host keys with ssh-keygen after install.",
                    "Host keys do not regenerate the same identity.",
                    "Clients that pinned the old host key will see a warning.",
                    [
                        new RecipeComponent(
                            "host-keys",
                            "OpenSSH Server",
                            hostNames.Count + " files",
                            Decision.Undecided,
                            false,
                            null,
                            true),
                    ],
                    "openssh-server",
                    new Dictionary<string, string>
                    {
                        ["source"] = server,
                        ["names"] = string.Join("|", hostNames),
                        ["component"] = "host-keys",
                    }));
        }

        return new DetectResult(cards, badges);
    }

    public PlanResult Plan(CardDecisions decisions, DestinationContext destination)
    {
        string component = decisions.Card.Facts.GetValueOrDefault("component") ?? "files";
        if (!RecipeDecisions.ShouldRestore(decisions, component))
        {
            return new PlanResult(decisions.Card, []);
        }

        string sourceDir = decisions.Card.Facts["source"];
        string destDir = component == "host-keys"
            ? Path.Combine(destination.DestinationProfileRoot, "Recovered", "OpenSSH-Server")
            : Path.Combine(destination.DestinationProfileRoot, ".ssh");
        List<RecipeWrite> writes = [];
        foreach (string name in decisions.Card.Facts["names"].Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            string source = Path.Combine(sourceDir, name);
            string dest = Path.Combine(destDir, name);
            if (destination.SafeFs.FileExists(dest))
            {
                dest = Path.Combine(destDir, name + ".from-windows-old");
            }

            writes.Add(new RecipeWrite(RecipeWriteKind.CopyFile, source, dest, null, 1, component));
        }

        return new PlanResult(decisions.Card, writes);
    }

    public async Task ExecuteAsync(PlanResult plan, IRecipeJournal journal, CancellationToken cancellationToken = default)
    {
        foreach (RecipeWrite write in plan.Writes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileName(write.DestinationPath);
            if (name.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                HardenUserOnlyAcl(write.DestinationPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
        }

        if (plan.Destination is { } destination)
        {
            await destination.ProcessRunner.RunAsync(
                    new WinOldRecovery.Core.Processes.ProcessRequest(
                        "ssh.exe",
                        ["-G", "localhost"],
                        WorkingDirectory: Path.Combine(destination.DestinationProfileRoot, ".ssh"),
                        Timeout: TimeSpan.FromSeconds(15)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public RecipeVerifyResult Verify(PlanResult plan)
    {
        bool ok = plan.Writes.All(static write => File.Exists(write.DestinationPath));
        return new RecipeVerifyResult(ok, ok ? "SSH files present" : "SSH destination missing");
    }

    public IReadOnlyList<Prerequisite> Prerequisites(PlanResult plan) => [];

    internal static void HardenUserOnlyAcl(string path)
    {
        FileInfo file = new(path);
        if (!file.Exists)
        {
            return;
        }

        FileSecurity security = file.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(NTAccount)).Cast<FileSystemAccessRule>())
        {
            security.RemoveAccessRule(rule);
        }

        SecurityIdentifier? user = WindowsIdentity.GetCurrent().User;
        if (user is null)
        {
            return;
        }

        security.AddAccessRule(
            new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        file.SetAccessControl(security);
    }

    private static bool TryListFiles(
        SafeFs safeFs,
        string directory,
        out List<string> names,
        out List<string> fingerprints)
    {
        names = [];
        fingerprints = [];
        foreach (string entry in safeFs.EnumerateFileSystemEntries(directory))
        {
            if (safeFs.DirectoryExists(entry))
            {
                continue;
            }

            string name = Path.GetFileName(entry);
            names.Add(name);
            if (!name.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            byte[] bytes = ReadBytes(safeFs, entry, 4096);
            fingerprints.Add("SHA256:" + Convert.ToBase64String(SHA256.HashData(bytes)).TrimEnd('='));
        }

        return names.Count > 0;
    }

    private static byte[] ReadBytes(SafeFs safeFs, string path, int max)
    {
        using FileStream stream = safeFs.OpenRead(path);
        byte[] buffer = new byte[max];
        int read = stream.Read(buffer, 0, buffer.Length);
        return buffer.AsSpan(0, read).ToArray();
    }
}
