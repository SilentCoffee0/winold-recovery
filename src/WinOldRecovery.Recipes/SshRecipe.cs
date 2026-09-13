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
        string ssh = Path.Combine(context.OldProfileRoot, ".ssh");
        if (!context.SafeFs.DirectoryExists(ssh))
        {
            return new DetectResult([], []);
        }

        List<string> names = [];
        List<string> fingerprints = [];
        foreach (string entry in context.SafeFs.EnumerateFileSystemEntries(ssh))
        {
            if (context.SafeFs.DirectoryExists(entry))
            {
                continue;
            }

            string name = Path.GetFileName(entry);
            names.Add(name);
            if (!name.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            byte[] bytes = ReadBytes(context.SafeFs, entry, 4096);
            fingerprints.Add("SHA256:" + Convert.ToBase64String(SHA256.HashData(bytes)).TrimEnd('='));
        }

        if (names.Count == 0)
        {
            return new DetectResult([], []);
        }

        RecipeCard card = new(
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
            });

        return new DetectResult([card], [(".ssh", "SSH", "keys")]);
    }

    public PlanResult Plan(CardDecisions decisions, DestinationContext destination)
    {
        if (!RecipeDecisions.ShouldRestore(decisions, "files"))
        {
            return new PlanResult(decisions.Card, []);
        }

        string sourceDir = decisions.Card.Facts["source"];
        string destDir = Path.Combine(destination.DestinationProfileRoot, ".ssh");
        List<RecipeWrite> writes = [];
        foreach (string name in decisions.Card.Facts["names"].Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            string source = Path.Combine(sourceDir, name);
            string dest = Path.Combine(destDir, name);
            if (destination.SafeFs.FileExists(dest))
            {
                dest = Path.Combine(destDir, name + ".from-windows-old");
            }

            writes.Add(new RecipeWrite(RecipeWriteKind.CopyFile, source, dest, null, 1, "files"));
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

    private static byte[] ReadBytes(SafeFs safeFs, string path, int max)
    {
        using FileStream stream = safeFs.OpenRead(path);
        byte[] buffer = new byte[max];
        int read = stream.Read(buffer, 0, buffer.Length);
        return buffer.AsSpan(0, read).ToArray();
    }
}
