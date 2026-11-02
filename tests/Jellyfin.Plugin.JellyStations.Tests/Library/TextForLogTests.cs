using Jellyfin.Plugin.JellyStations.Models;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Library;

public class TextForLogTests
{
    [Fact]
    public void ForLog_ReplacesLineBreaksAndControlCharacters()
    {
        Assert.Equal("Retro TV  fake line [INF] admin logged in", Text.ForLog("Retro TV\r\nfake line [INF] admin logged in"));
        Assert.Equal("a b c", Text.ForLog("a\tb\u0007c"));
    }

    [Fact]
    public void ForLog_LeavesOrdinaryNamesAlone()
    {
        Assert.Equal("Sci-Fi & Fantasy (HD) — ñ", Text.ForLog("Sci-Fi & Fantasy (HD) — ñ"));
        Assert.Equal(string.Empty, Text.ForLog(null));
        Assert.Equal(string.Empty, Text.ForLog(string.Empty));
    }
}
