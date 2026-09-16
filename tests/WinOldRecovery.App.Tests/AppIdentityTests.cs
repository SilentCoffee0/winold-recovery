using System.Reflection;

namespace WinOldRecovery.App.Tests;

public sealed class AppIdentityTests
{
    [Fact]
    public void Current_ReportsVersionTrademarkAndOptionalGitStamp()
    {
        AppIdentityInfo info = AppIdentity.From(typeof(AppIdentity).Assembly);
        Assert.StartsWith("0.1.0", info.Version, StringComparison.Ordinal);
        Assert.Contains("independent community project", info.Trademark, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not affiliated", info.Trademark, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Microsoft", info.Trademark, StringComparison.Ordinal);
        Assert.Equal(AppIdentity.Trademark, info.Trademark);

        string formatted = AppIdentity.Format(info);
        Assert.Contains("Version " + info.Version, formatted, StringComparison.Ordinal);
        Assert.Contains(info.Trademark, formatted, StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(info.Commit))
        {
            Assert.DoesNotContain(' ', info.Commit);
            Assert.Contains("Commit " + info.Commit, formatted, StringComparison.Ordinal);
        }

        if (!string.IsNullOrWhiteSpace(info.BuildDate))
        {
            Assert.True(AppIdentity.LooksLikeIsoDate(info.BuildDate));
            Assert.Contains("Built " + info.BuildDate, formatted, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Format_UsesUntaggedPlaceholdersWhenGitMetadataIsMissing()
    {
        string text = AppIdentity.Format(new AppIdentityInfo("0.1.0", "", "", AppIdentity.Trademark));
        Assert.Contains("Commit untagged", text, StringComparison.Ordinal);
        Assert.Contains("Build date unknown", text, StringComparison.Ordinal);
        Assert.Contains(AppIdentity.Trademark, text, StringComparison.Ordinal);
    }

    [Fact]
    public void From_SplitsInformationalVersionAtPlus()
    {
        Assembly assembly = typeof(AppIdentity).Assembly;
        string informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? string.Empty;
        AppIdentityInfo info = AppIdentity.From(assembly);
        if (informational.Contains('+', StringComparison.Ordinal))
        {
            Assert.Equal(informational.Split('+')[0], info.Version);
            Assert.False(string.IsNullOrWhiteSpace(info.Commit));
        }
    }
}
