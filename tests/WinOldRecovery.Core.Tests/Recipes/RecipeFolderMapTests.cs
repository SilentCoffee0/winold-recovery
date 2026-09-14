using WinOldRecovery.Core.Recipes;

namespace WinOldRecovery.Core.Tests.Recipes;

public sealed class RecipeFolderMapTests
{
    [Fact]
    public void RoundTrip_PreservesFolderIds()
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal)
        {
            ["default"] = @"C:\Users\V\Sync",
            ["ext"] = @"E:\Moved",
        };

        Dictionary<string, string> parsed = RecipeFolderMap.Parse(RecipeFolderMap.Format(map));
        Assert.Equal(@"E:\Moved", parsed["ext"]);
        Assert.Equal("recipe.folderMap.home", RecipeFolderMap.KvKey("home"));
        Assert.Empty(RecipeFolderMap.Parse(null));
    }
}
