using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Streaming;
using Xunit;

namespace Jellyfin.Plugin.TvStation.Tests.Streaming;

public class FfmpegArgsBuilderTests
{
    private static readonly EncodingProfile Profile = new(); // 1280x720

    private static PlayableItem Item(bool hasAudio = true) => new()
    {
        ItemId = Guid.NewGuid(),
        Path = "/media/show.mkv",
        RuntimeTicks = TimeSpan.FromMinutes(22).Ticks,
        Title = "Show",
        HasAudio = hasAudio
    };

    private static string ValueAfter(List<string> args, string flag)
    {
        var i = args.IndexOf(flag);
        Assert.True(i >= 0 && i + 1 < args.Count, flag + " not found");
        return args[i + 1];
    }

    [Fact]
    public void WithoutOverlay_UsesPlainVideoFilter()
    {
        var args = FfmpegArgsBuilder.ForFile(Item(), TimeSpan.Zero, null, TimeSpan.Zero, Profile);

        Assert.DoesNotContain("-filter_complex", args);
        Assert.Contains("-vf", args);
        Assert.Equal("0:v:0", ValueAfter(args, "-map"));
        Assert.Single(args.Where(a => a == "-i"));
    }

    [Fact]
    public void WithOverlay_AddsLogoInputAndOverlayGraph()
    {
        var overlay = new FfmpegArgsBuilder.LogoOverlayInput("/data/logo.png", LogoCorner.TopRight, 70, 8);
        var args = FfmpegArgsBuilder.ForFile(Item(), TimeSpan.Zero, null, TimeSpan.Zero, Profile, overlay);

        Assert.Equal(2, args.Count(a => a == "-i"));
        Assert.Equal("/data/logo.png", args[args.LastIndexOf("-i") + 1]);
        Assert.DoesNotContain("-vf", args);
        Assert.Equal("[v]", ValueAfter(args, "-map"));

        var graph = ValueAfter(args, "-filter_complex");
        Assert.StartsWith("[0:v]scale=1280:720", graph, StringComparison.Ordinal);
        Assert.Contains("[1:v]scale=-1:57,format=rgba,colorchannelmixer=aa=0.70[logo]", graph, StringComparison.Ordinal);
        Assert.EndsWith("[base][logo]overlay=W-w-20:20:format=yuv420[v]", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void WithOverlayAndSilentSource_LogoIsThirdInput()
    {
        var overlay = new FfmpegArgsBuilder.LogoOverlayInput("/data/logo.png", LogoCorner.BottomLeft, 100, 10);
        var args = FfmpegArgsBuilder.ForFile(Item(hasAudio: false), TimeSpan.Zero, null, TimeSpan.Zero, Profile, overlay);

        var graph = ValueAfter(args, "-filter_complex");
        Assert.Contains("[2:v]scale=-1:72,format=rgba,colorchannelmixer=aa=1.00[logo]", graph, StringComparison.Ordinal);
        Assert.Contains("overlay=20:H-h-20:format=yuv420[v]", graph, StringComparison.Ordinal);
        Assert.Contains("1:a:0", args);
        Assert.Contains("-shortest", args);
    }

    [Theory]
    [InlineData(LogoCorner.TopLeft, "overlay=20:20:")]
    [InlineData(LogoCorner.BottomRight, "overlay=W-w-20:H-h-20:")]
    public void Corners_MapToPositions(LogoCorner corner, string expected)
    {
        var graph = FfmpegArgsBuilder.OverlayFilter(Profile, new FfmpegArgsBuilder.LogoOverlayInput("x.png", corner, 50, 8), 1);
        Assert.Contains(expected, graph, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlayValues_AreClamped()
    {
        var graph = FfmpegArgsBuilder.OverlayFilter(Profile, new FfmpegArgsBuilder.LogoOverlayInput("x.png", LogoCorner.TopRight, 500, 90), 1);
        Assert.Contains("scale=-1:180,", graph, StringComparison.Ordinal);
        Assert.Contains("aa=1.00", graph, StringComparison.Ordinal);
    }
}
