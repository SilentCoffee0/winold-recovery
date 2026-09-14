using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Planning;

namespace WinOldRecovery.Core.Tests.Planning;

public sealed class DestinationMapTests
{
    [Fact]
    public void Resolve_UsesLongestMatchingPrefix()
    {
        string destRoot = Path.Combine(Path.GetTempPath(), $"WinOldRecovery-DestRoot-{Guid.NewGuid():N}");
        string profileOut = Path.Combine(Path.GetTempPath(), $"WinOldRecovery-ProfileOut-{Guid.NewGuid():N}");
        string deskOut = Path.Combine(Path.GetTempPath(), $"WinOldRecovery-DeskOut-{Guid.NewGuid():N}");
        Directory.CreateDirectory(destRoot);
        Directory.CreateDirectory(profileOut);
        Directory.CreateDirectory(deskOut);
        try
        {
            Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase)
            {
                [@"Users\Alice"] = profileOut,
                [@"Users\Alice\Desktop"] = deskOut,
            };

            string desktop = DestinationMap.Resolve(destRoot, map, @"Users\Alice\Desktop");
            string nested = DestinationMap.Resolve(destRoot, map, @"Users\Alice\Desktop\notes.txt");
            string pictures = DestinationMap.Resolve(destRoot, map, @"Users\Alice\Pictures\a.jpg");

            Assert.Equal(PathCanonicalizer.Canonicalize(deskOut), desktop, ignoreCase: true);
            Assert.EndsWith(@"notes.txt", nested, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(@"Pictures", pictures, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(destRoot, recursive: true);
            Directory.Delete(profileOut, recursive: true);
            Directory.Delete(deskOut, recursive: true);
        }
    }

    [Fact]
    public void ParseAndFormat_RoundTrip()
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Users\\Alice"] = @"C:\Users\VJ",
        };

        Dictionary<string, string> parsed = DestinationMap.Parse(DestinationMap.Format(map));
        Assert.Equal(@"C:\Users\VJ", parsed[@"Users\Alice"]);
    }
}
