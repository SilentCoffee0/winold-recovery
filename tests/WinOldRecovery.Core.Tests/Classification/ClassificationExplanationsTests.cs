using WinOldRecovery.Core.Browse;
using WinOldRecovery.Core.Classification;
using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.Scan;

namespace WinOldRecovery.Core.Tests.Classification;

public sealed class ClassificationExplanationsTests
{
    [Fact]
    public void ForNode_UsesKeePassWhyOnVaultFiles()
    {
        IReadOnlyList<ClassificationRule> rules = ClassificationRuleCatalog.LoadEmbedded();
        TreeNodeRow vault = new(
            1,
            null,
            "vault.kdbx",
            @"Users\Alice\vault.kdbx",
            NodeKind.File,
            12,
            12,
            1,
            null,
            NodeProblem.None,
            Decision.Restore,
            false,
            false,
            0,
            ["HighValue: KeePass"]);
        IReadOnlyList<string> lines = ClassificationExplanations.ForNode(vault, rules);
        Assert.Contains(
            lines,
            line => line.Contains("password vault", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ForNode_UsesDirectoryNameWhyNotASiblingRule()
    {
        IReadOnlyList<ClassificationRule> rules = ClassificationRuleCatalog.LoadEmbedded();
        TreeNodeRow modules = new(
            2,
            1,
            "node_modules",
            @"Users\Alice\proj\node_modules",
            NodeKind.Directory,
            1,
            1,
            1,
            null,
            NodeProblem.None,
            Decision.Undecided,
            false,
            false,
            1,
            ["Regeneratable: Regeneratable"]);
        IReadOnlyList<string> lines = ClassificationExplanations.ForNode(modules, rules);
        Assert.Contains(lines, line => line.Contains("npm install", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("virtual environment", StringComparison.Ordinal));
    }
}
