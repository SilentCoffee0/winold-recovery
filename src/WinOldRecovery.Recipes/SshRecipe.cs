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
            TryListFiles(context.SafeFs, ssh, out SshFolderFacts userFacts))
        {
            cards.Add(
                new RecipeCard(
                    Id,
                    "SSH keys (" + context.ProfileName + ")",
                    "Your SSH identities and the hosts you have trusted.",
                    "Without these keys you cannot log in to servers that trust this computer.",
                    "Private keys, public keys, config and known_hosts. Existing destination files are never overwritten; a conflict is saved as *.from-windows-old. Private key material is never shown." +
                        (userFacts.Unencrypted > 0
                            ? " At least one private key has no passphrase; consider adding one with ssh-keygen -p."
                            : string.Empty) +
                        (userFacts.Efs > 0
                            ? " At least one private key has the EFS attribute and may be unreadable after copy."
                            : string.Empty),
                    "Create new keys and update every server.",
                    "Keys do not regenerate.",
                    "You lose SSH access until you replace the keys on every host.",
                    [
                        new RecipeComponent("files", "SSH files", userFacts.Names.Count + " files", Decision.Restore, false, null, true),
                    ],
                    context.ProfileName,
                    UserFacts(ssh, userFacts)));
            badges.Add((".ssh", "SSH", "keys"));
        }

        string server = Path.GetFullPath(Path.Combine(context.OldProfileRoot, "..", "..", "ProgramData", "ssh"));
        if (context.SafeFs.DirectoryExists(server) &&
            TryListFiles(context.SafeFs, server, out SshFolderFacts hostFacts))
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
                            hostFacts.Names.Count + " files",
                            Decision.Undecided,
                            false,
                            null,
                            true),
                    ],
                    "openssh-server",
                    new Dictionary<string, string>
                    {
                        ["source"] = server,
                        ["names"] = string.Join("|", hostFacts.Names),
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
            if (!NeedsStrictAcl(write.DestinationPath))
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
        foreach (RecipeWrite write in plan.Writes)
        {
            if (!File.Exists(write.DestinationPath))
            {
                return new RecipeVerifyResult(false, "SSH destination missing");
            }

            if (!NeedsStrictAcl(write.DestinationPath))
            {
                continue;
            }

            if (AclAllowsBroadUsers(write.DestinationPath))
            {
                return new RecipeVerifyResult(false, "SSH private key ACL allows Everyone, Users, or Authenticated Users");
            }
        }

        return new RecipeVerifyResult(true, "SSH files present");
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
        security.AddAccessRule(
            new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        security.AddAccessRule(
            new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        file.SetAccessControl(security);
    }

    internal static bool AclAllowsBroadUsers(string path)
    {
        FileSecurity security;
        try
        {
            security = new FileInfo(path).GetAccessControl();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return true;
        }

        SecurityIdentifier[] forbidden =
        [
            new(WellKnownSidType.WorldSid, null),
            new(WellKnownSidType.BuiltinUsersSid, null),
            new(WellKnownSidType.AuthenticatedUserSid, null),
        ];
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>())
        {
            if (rule.AccessControlType != AccessControlType.Allow)
            {
                continue;
            }

            SecurityIdentifier sid = (SecurityIdentifier)rule.IdentityReference;
            if (forbidden.Any(sid.Equals))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool NeedsStrictAcl(string path)
    {
        string name = Path.GetFileName(path);
        if (name.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (name.Equals("config", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("authorized_keys", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("id_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("ssh_host_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            byte[] buffer = new byte[64];
            int read = stream.Read(buffer, 0, buffer.Length);
            string text = System.Text.Encoding.ASCII.GetString(buffer, 0, read);
            return text.Contains("BEGIN", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static Dictionary<string, string> UserFacts(string source, SshFolderFacts facts)
    {
        return new Dictionary<string, string>
        {
            ["source"] = source,
            ["names"] = string.Join("|", facts.Names),
            ["fingerprints"] = string.Join(", ", facts.Fingerprints),
            ["keyTypes"] = string.Join(", ", facts.KeyTypes),
            ["unencrypted"] = facts.Unencrypted > 0 ? "1" : "0",
            ["unencryptedCount"] = facts.Unencrypted.ToString(),
            ["configHosts"] = facts.ConfigHosts.ToString(),
            ["knownHosts"] = facts.KnownHosts.ToString(),
            ["efsKeys"] = facts.Efs.ToString(),
            ["component"] = "files",
        };
    }

    private static bool TryListFiles(SafeFs safeFs, string directory, out SshFolderFacts facts)
    {
        List<string> names = [];
        List<string> fingerprints = [];
        List<string> keyTypes = [];
        int unencrypted = 0;
        int efs = 0;
        int configHosts = 0;
        int knownHosts = 0;
        foreach (string entry in safeFs.EnumerateFileSystemEntries(directory))
        {
            if (safeFs.DirectoryExists(entry))
            {
                continue;
            }

            string name = Path.GetFileName(entry);
            names.Add(name);
            if (name.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
            {
                byte[] pub = ReadBytes(safeFs, entry, 4096);
                fingerprints.Add("SHA256:" + Convert.ToBase64String(SHA256.HashData(pub)).TrimEnd('='));
                string type = KeyTypeFromPub(pub, name);
                if (type.Length > 0 && !keyTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
                {
                    keyTypes.Add(type);
                }

                continue;
            }

            if (name.Equals("config", StringComparison.OrdinalIgnoreCase))
            {
                configHosts = CountConfigHosts(ReadBytes(safeFs, entry, 64 * 1024));
                continue;
            }

            if (name.Equals("known_hosts", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("known_hosts.old", StringComparison.OrdinalIgnoreCase))
            {
                knownHosts += CountKnownHosts(ReadBytes(safeFs, entry, 64 * 1024));
                continue;
            }

            byte[] head = ReadBytes(safeFs, entry, 512);
            if (!LooksLikePrivateKey(name, head))
            {
                continue;
            }

            if (OpenSshPrivateKeyHeader.IsUnencrypted(head))
            {
                unencrypted++;
            }

            if (HasEfsAttribute(entry))
            {
                efs++;
            }
        }

        facts = new SshFolderFacts(names, fingerprints, keyTypes, unencrypted, efs, configHosts, knownHosts);
        return names.Count > 0;
    }

    internal static bool HasEfsAttribute(string path)
    {
        try
        {
            return HasEfsAttribute(File.GetAttributes(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool HasEfsAttribute(FileAttributes attributes)
    {
        return (attributes & FileAttributes.Encrypted) != 0;
    }

    private static bool LooksLikePrivateKey(string name, ReadOnlySpan<byte> head)
    {
        if (name.Equals("id_rsa", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("id_ecdsa", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("id_ed25519", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("id_dsa", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string text = System.Text.Encoding.ASCII.GetString(head);
        return text.Contains("BEGIN", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase);
    }

    private static string KeyTypeFromPub(ReadOnlySpan<byte> pub, string name)
    {
        string text = System.Text.Encoding.ASCII.GetString(pub);
        if (text.StartsWith("ssh-ed25519", StringComparison.Ordinal))
        {
            return "ed25519";
        }

        if (text.StartsWith("ssh-rsa", StringComparison.Ordinal))
        {
            return "rsa";
        }

        if (text.StartsWith("ecdsa-", StringComparison.Ordinal))
        {
            return "ecdsa";
        }

        if (text.StartsWith("ssh-dss", StringComparison.Ordinal))
        {
            return "dsa";
        }

        if (name.Contains("ed25519", StringComparison.OrdinalIgnoreCase))
        {
            return "ed25519";
        }

        return string.Empty;
    }

    private static int CountConfigHosts(ReadOnlySpan<byte> bytes)
    {
        int count = 0;
        foreach (string line in System.Text.Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("Host ", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("HostName", StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }

    private static int CountKnownHosts(ReadOnlySpan<byte> bytes)
    {
        int count = 0;
        foreach (string line in System.Text.Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
            {
                count++;
            }
        }

        return count;
    }

    private readonly record struct SshFolderFacts(
        List<string> Names,
        List<string> Fingerprints,
        List<string> KeyTypes,
        int Unencrypted,
        int Efs,
        int ConfigHosts,
        int KnownHosts);

    private static byte[] ReadBytes(SafeFs safeFs, string path, int max)
    {
        using FileStream stream = safeFs.OpenRead(path);
        byte[] buffer = new byte[max];
        int read = stream.Read(buffer, 0, buffer.Length);
        return buffer.AsSpan(0, read).ToArray();
    }
}
