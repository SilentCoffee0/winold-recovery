using System.Diagnostics;
using WinOldRecovery.Core.Classification;

namespace WinOldRecovery.Core.Tests.Classification;

public sealed class RuleGlobTests
{
    [Fact]
    public void MatchesPath_AcceptsSingleStarSegments()
    {
        Assert.True(
            RuleGlob.MatchesPath(
                @"Users\Alice\Documents\PowerShell\profile.ps1",
                @"**\Documents\PowerShell\*.ps1"));
        Assert.False(
            RuleGlob.MatchesPath(
                @"Users\Alice\Scale\d0000\f0000.txt",
                @"**\.nuget\packages"));
        Assert.False(
            RuleGlob.MatchesPath(
                @"Users\Alice\Scale\d0000\f0000.txt",
                @"**\Joplin\database.sqlite"));
    }

    [Fact]
    public void MatchesPath_OneMillionCallsAgainstCachedGlobsStayUnderTwoSeconds()
    {
        string[] globs =
        [
            @"**\.nuget\packages",
            @"**\Documents\PowerShell\*.ps1",
            @"**\Joplin\database.sqlite",
        ];
        Stopwatch clock = Stopwatch.StartNew();
        int hits = 0;
        for (int index = 0; index < 1_000_000; index++)
        {
            string path = @"Users\Alice\Scale\d" + (index / 1000).ToString("D4") + @"\f" + (index % 1000).ToString("D4") + ".txt";
            foreach (string glob in globs)
            {
                if (RuleGlob.MatchesPath(path, glob))
                {
                    hits++;
                }
            }
        }

        clock.Stop();
        Assert.Equal(0, hits);
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(2),
            "Cached path-glob matching took " + clock.Elapsed.TotalSeconds.ToString("0.000") + " s.");
    }
}
