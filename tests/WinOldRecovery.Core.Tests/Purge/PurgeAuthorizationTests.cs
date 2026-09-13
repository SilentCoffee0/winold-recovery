using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Purge;
using WinOldRecovery.Core.Safety;

namespace WinOldRecovery.Core.Tests.Purge;

public sealed class PurgeAuthorizationTests
{
    [Fact]
    public void EachGateIndividuallyBlocks()
    {
        PurgeGateRequest ok = Valid();
        Assert.True(PurgeAuthorization.Evaluate(ok).Authorized);
        Assert.NotNull(PurgeAuthorization.Evaluate(ok).Token);

        Assert.Contains("verify", PurgeAuthorization.Evaluate(ok with { VerifyAllOk = false }).BlockedGates);
        Assert.Contains("files-checked", PurgeAuthorization.Evaluate(ok with { FilesChecked = false }).BlockedGates);
        Assert.Contains("undecided", PurgeAuthorization.Evaluate(ok with { UndecidedAcknowledged = false }).BlockedGates);
        Assert.Contains("restore-active", PurgeAuthorization.Evaluate(ok with { RestoreJobActive = true }).BlockedGates);
        Assert.Contains("folder-name", PurgeAuthorization.Evaluate(ok with { TypedFolderName = "wrong" }).BlockedGates);
        Assert.Contains(
            "custom-root",
            PurgeAuthorization.Evaluate(ok with { SourceFolderName = "OldInstall", TypedFolderName = "OldInstall", CustomRootConfirmed = false }).BlockedGates);
        Assert.Null(PurgeAuthorization.Evaluate(ok with { VerifyAllOk = false }).Token);
    }

    [Fact]
    public void CustomRootIsAllowedAfterExtraConfirmation()
    {
        PurgeGateResult result = PurgeAuthorization.Evaluate(
            Valid() with
            {
                SourceFolderName = "Custom.old",
                TypedFolderName = "Custom.old",
                CustomRootConfirmed = true,
            });
        Assert.True(result.Authorized);
    }

    private static PurgeGateRequest Valid()
    {
        string root = Path.Combine(Path.GetTempPath(), "Windows.old-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string canonical = PathCanonicalizer.Canonicalize(root);
        return new PurgeGateRequest(
            true,
            true,
            true,
            false,
            "Windows.old",
            "Windows.old",
            canonical,
            Environment.ProcessPath,
            Path.GetTempPath(),
            [Path.Combine(Path.GetTempPath(), "Recovered")],
            false);
    }
}
