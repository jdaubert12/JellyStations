using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Streaming;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Streaming;

/// <summary>Burning a source's subtitles into the picture: text through libass after scaling, bitmaps overlaid before it.</summary>
public class SubtitleBurnInTests
{
    private static readonly EncodingProfile Profile = new() { HardwareDecode = string.Empty };

    private static PlayableItem Item(string? subtitlePath = null, int? streamIndex = null, bool bitmap = false) => new()
    {
        ItemId = Guid.NewGuid(),
        Path = "/media/anime/ep01.mkv",
        RuntimeTicks = TimeSpan.FromMinutes(24).Ticks,
        Title = "Anime",
        SubtitlePath = subtitlePath,
        SubtitleStreamIndex = streamIndex,
        SubtitleIsBitmap = bitmap
    };

    private static string ValueAfter(List<string> args, string flag)
    {
        var i = args.IndexOf(flag);
        Assert.True(i >= 0 && i + 1 < args.Count, flag + " not found");
        return args[i + 1];
    }

    [Fact]
    public void TextSubtitles_RenderAfterScalingAndBeforePadding_FromTheStart()
    {
        var args = FfmpegArgsBuilder.ForFile(Item(), TimeSpan.Zero, null, TimeSpan.Zero, Profile, subtitles: FfmpegArgsBuilder.SubtitleInput.Text("/config/data/JellyStations/subtitles/abc.ass"));

        var vf = ValueAfter(args, "-vf");
        Assert.Contains("flags=bicubic,subtitles=filename='/config/data/JellyStations/subtitles/abc.ass',pad=1280:720", vf);
        Assert.DoesNotContain("setpts", vf);
        Assert.DoesNotContain("-filter_complex", args);
    }

    [Fact]
    public void TextSubtitles_AfterAnInputSeek_RunOnTheFilesOwnClock()
    {
        var args = FfmpegArgsBuilder.ForFile(Item(), TimeSpan.FromMinutes(10), null, TimeSpan.Zero, Profile, subtitles: FfmpegArgsBuilder.SubtitleInput.Text("/subs/a.ass"));

        Assert.Equal("600.000", ValueAfter(args, "-ss"));
        Assert.Contains(",setpts=PTS+600.000/TB,subtitles=filename='/subs/a.ass',setpts=PTS-600.000/TB,pad=", ValueAfter(args, "-vf"));
    }

    [Fact]
    public void TextSubtitles_SmallSeek_IsNotAppliedToTheInput_SoNoShiftEither()
    {
        var args = FfmpegArgsBuilder.ForFile(Item(), TimeSpan.FromMilliseconds(300), null, TimeSpan.Zero, Profile, subtitles: FfmpegArgsBuilder.SubtitleInput.Text("/subs/a.ass"));

        Assert.DoesNotContain("-ss", args);
        Assert.DoesNotContain("setpts", ValueAfter(args, "-vf"));
    }

    [Fact]
    public void TextSubtitles_WithTheLogo_GoIntoTheComposedGraph()
    {
        var overlay = new FfmpegArgsBuilder.LogoOverlayInput("/logos/a.png", LogoCorner.TopRight, 80, 10);
        var args = FfmpegArgsBuilder.ForFile(Item(), TimeSpan.Zero, null, TimeSpan.Zero, Profile, overlay, subtitles: FfmpegArgsBuilder.SubtitleInput.Text("/subs/a.ass"));

        var graph = ValueAfter(args, "-filter_complex");
        Assert.StartsWith("[0:v]scale=", graph);
        Assert.Contains(",subtitles=filename='/subs/a.ass',pad=", graph);
        Assert.Contains("[base]", graph);
    }

    [Fact]
    public void BitmapSubtitles_AreOverlaidOnTheSourceFrame_BeforeScaling()
    {
        var args = FfmpegArgsBuilder.ForFile(Item(streamIndex: 1, bitmap: true), TimeSpan.FromMinutes(5), null, TimeSpan.Zero, Profile, subtitles: FfmpegArgsBuilder.SubtitleInput.Bitmap(1));

        Assert.DoesNotContain("-vf", args);
        var graph = ValueAfter(args, "-filter_complex");
        Assert.StartsWith("[0:v][0:s:1]overlay=eof_action=pass:repeatlast=0[subbed];[subbed]scale=1280:720", graph);
        Assert.DoesNotContain("subtitles=", graph);
        Assert.DoesNotContain("setpts", graph);
        Assert.EndsWith("[v]", graph);
        Assert.Equal("[v]", ValueAfter(args, "-map"));
    }

    [Fact]
    public void BitmapSubtitles_WithTheLogo_KeepTheLogoOnTop()
    {
        var overlay = new FfmpegArgsBuilder.LogoOverlayInput("/logos/a.png", LogoCorner.TopRight, 80, 10);
        var args = FfmpegArgsBuilder.ForFile(Item(streamIndex: 0, bitmap: true), TimeSpan.Zero, null, TimeSpan.Zero, Profile, overlay, subtitles: FfmpegArgsBuilder.SubtitleInput.Bitmap(0));

        var graph = ValueAfter(args, "-filter_complex");
        Assert.StartsWith("[0:v][0:s:0]overlay=eof_action=pass:repeatlast=0[subbed];[subbed]scale=", graph);
        Assert.Contains("[base][logo]overlay=", graph);
    }

    [Fact]
    public void NoSubtitles_LeavesTheChainAsItWas()
    {
        var plain = FfmpegArgsBuilder.ForFile(Item(), TimeSpan.Zero, null, TimeSpan.Zero, Profile);

        Assert.DoesNotContain("subtitles", ValueAfter(plain, "-vf"));
    }

    [Theory]
    [InlineData("/media/show.ass", "/media/show.ass")]
    [InlineData("C:\\subs\\show.ass", "C\\:/subs/show.ass")]
    [InlineData("/media/it's here.ass", "/media/it'\\\\\\''s here.ass")]
    [InlineData("/media/a:b.ass", "/media/a\\:b.ass")]
    public void FilterPaths_AreQuotedForFfmpegsTwoLevelsOfParsing(string path, string expected)
    {
        Assert.Equal(expected, FfmpegArgsBuilder.QuoteFilterPath(path));
    }

    [Fact]
    public void Extractor_NamesTheAssMuxer_BecauseTheTempFileHasNoAssExtension()
    {
        var args = SubtitleExtractor.Arguments("/media/anime/ep01.mkv", 2, "/cache/abc.ass.tmp");

        Assert.Equal(new[] { "-map", "0:s:2", "-c:s", "ass", "-f", "ass", "/cache/abc.ass.tmp" }, args.TakeLast(7));
        Assert.Contains("-y", args);
        Assert.Equal("/media/anime/ep01.mkv", args[args.IndexOf("-i") + 1]);
    }

    [Fact]
    public void PlayableItem_CarriesSubtitlesIntoItsSinglePart()
    {
        var item = Item(subtitlePath: "/media/anime/ep01.eng.srt");
        var part = item.SegmentAt(TimeSpan.Zero).Part;

        Assert.True(item.HasSubtitles);
        Assert.Equal("/media/anime/ep01.eng.srt", part.SubtitlePath);
        Assert.True(part.HasSubtitles);
        Assert.False(Item().HasSubtitles);
    }
}
