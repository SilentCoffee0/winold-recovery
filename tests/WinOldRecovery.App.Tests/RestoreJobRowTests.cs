using WinOldRecovery.App.ViewModels;

namespace WinOldRecovery.App.Tests;

public sealed class RestoreJobRowTests
{
    [Fact]
    public void Display_PrefixesUxStatusGlyphs()
    {
        RestoreJobRow row = new(1, "Documents", "waiting");
        Assert.Equal("○  Documents", row.Display);

        row.SetStatus("running");
        Assert.Equal("▶  Documents", row.Display);

        row.SetStatus("paused");
        Assert.Equal("❚❚  Documents", row.Display);

        row.SetStatus("done");
        Assert.Equal("✔  Documents", row.Display);

        row.SetStatus("failed");
        Assert.Equal("⚠  Documents", row.Display);
    }
}
