using Jellyfin.Plugin.JellyStations.Weather.Radar;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Weather;

public class RainViewerHostTests
{
    [Theory]
    [InlineData("https://tilecache.rainviewer.com")]
    [InlineData("https://tilecache.rainviewer.com/")]
    [InlineData("https://cdn.rainviewer.com")]
    [InlineData("https://rainviewer.com")]
    public void HttpsRainViewerHostsAreAccepted(string host)
    {
        Assert.True(RainViewerSource.IsRainViewerHost(host));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://tilecache.rainviewer.com")]
    [InlineData("https://169.254.169.254")]
    [InlineData("https://rainviewer.com.evil.example")]
    [InlineData("https://evilrainviewer.com")]
    [InlineData("https://user@rainviewer.com@internal.example")]
    [InlineData("file:///etc/passwd")]
    [InlineData("tilecache.rainviewer.com")]
    public void AnythingElseIsRefused(string? host)
    {
        Assert.False(RainViewerSource.IsRainViewerHost(host));
    }

    [Fact]
    public void IndexWithForeignHostFallsBackToTheDefault()
    {
        var index = RainViewerSource.ParseIndex("{\"host\":\"http://10.0.0.5\",\"radar\":{\"past\":[{\"time\":1700000000,\"path\":\"/v2/radar/1700000000\"}]}}");

        Assert.Equal(RainViewerSource.DefaultHost, index.Host);
        Assert.Single(index.Past);
    }

    [Fact]
    public void IndexWithRainViewerHostKeepsIt()
    {
        var index = RainViewerSource.ParseIndex("{\"host\":\"https://tilecache.rainviewer.com/\",\"radar\":{\"past\":[]}}");

        Assert.Equal("https://tilecache.rainviewer.com", index.Host);
    }
}
