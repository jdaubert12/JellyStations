using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Tests.Fixtures;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Library;

public class StationHealthTests
{
    private static Station Station() => new() { Id = Items.StationA, Name = "Test", ChannelNumber = "5", Enabled = true };

    private static CompiledStation Compiled(params PlayableItem[] pool) => new()
    {
        StationId = Items.StationA,
        Revision = 1,
        CompiledUtc = new DateTime(2026, 10, 19, 4, 0, 0, DateTimeKind.Utc),
        Pool = pool.ToList()
    };

    [Fact]
    public void NotCompiled_WarnsOnce()
    {
        var h = StationHealth.Build(Station(), null);

        Assert.False(h.Healthy);
        Assert.Single(h.Warnings);
        Assert.Contains("Not compiled", h.Warnings[0]);
        Assert.Null(h.CompiledUtc);
    }

    [Fact]
    public void HealthyStation_HasNoWarnings()
    {
        var compiled = Compiled(Items.SeriesRun("Show", 30, 60).ToArray());
        compiled.Sources.Add(new SourceExpansion { Section = "Pool", Label = "Series \"Show\"", Kind = SourceKind.Series, Found = 30, Kept = 30 });

        var h = StationHealth.Build(Station(), compiled);

        Assert.True(h.Healthy);
        Assert.Equal(30, h.UniqueHours);
        Assert.Empty(h.EmptySources);
        Assert.Equal(compiled.CompiledUtc, h.CompiledUtc);
    }

    [Fact]
    public void EmptySources_AreListed_WithTheRightReason()
    {
        var compiled = Compiled(Items.SeriesRun("Show", 30, 60).ToArray());
        compiled.Sources.Add(new SourceExpansion { Section = "Pool", Label = "Genre \"Western\"", Kind = SourceKind.Genre, Found = 0, Kept = 0 });
        compiled.Sources.Add(new SourceExpansion { Section = "Movie Night", Label = "BoxSet \"Trilogy\"", Kind = SourceKind.BoxSet, Found = 3, Kept = 0 });
        compiled.Sources.Add(new SourceExpansion { Section = "Pool", Label = "Series \"Show\"", Kind = SourceKind.Series, Found = 30, Kept = 30 });
        // A hand-picked movie that an earlier source already contributed is on the station, so it is not "empty".
        compiled.Sources.Add(new SourceExpansion { Section = "Pool", Label = "Movie \"Elf\"", Kind = SourceKind.Movie, Found = 1, Kept = 0, AlreadyListed = 1 });
        // But a box set where one item was already listed and the other two were unusable still is.
        compiled.Sources.Add(new SourceExpansion { Section = "Pool", Label = "BoxSet \"Mixed\"", Kind = SourceKind.BoxSet, Found = 3, Kept = 0, AlreadyListed = 1 });

        var h = StationHealth.Build(Station(), compiled);

        Assert.Equal(new[] { "Pool: Genre \"Western\"", "Movie Night: BoxSet \"Trilogy\"", "Pool: BoxSet \"Mixed\"" }, h.EmptySources);
        Assert.Equal(3, h.Warnings.Count);
        Assert.Contains("matched nothing", h.Warnings[0]);
        Assert.Contains("found 3 item(s) but none were usable", h.Warnings[1]);
        Assert.DoesNotContain(h.Warnings, w => w.Contains("Elf"));
    }

    [Fact]
    public void UnderADayOfUniqueContent_Warns()
    {
        var h = StationHealth.Build(Station(), Compiled(Items.Movie("A", 90), Items.Movie("B", 120)));

        Assert.Equal(3.5, h.UniqueHours);
        var warning = Assert.Single(h.Warnings);
        Assert.Contains("3.5 h of unique content", warning);
        Assert.Contains("under a day", warning);
    }

    [Fact]
    public void NoItems_WarnsAboutTheCard_NotAboutHours()
    {
        var h = StationHealth.Build(Station(), Compiled());

        var warning = Assert.Single(h.Warnings);
        Assert.Contains("No playable items", warning);
    }

    [Fact]
    public void A4KProfile_WarnsAboutH264AndALowBitrate_AndNothingElseDoes()
    {
        var compiled = Compiled(Items.SeriesRun("Show", 30, 60).ToArray());
        EncodingProfile Uhd(string encoder, int kbps) => new() { Width = 3840, Height = 2160, VideoEncoder = encoder, VideoBitrateKbps = kbps };

        Assert.True(StationHealth.Build(Station(), compiled, profile: new EncodingProfile { Width = 1920, Height = 1080 }).Healthy);
        Assert.True(StationHealth.Build(Station(), compiled, profile: Uhd("hevc_qsv", 20000)).Healthy);

        var weak = StationHealth.Build(Station(), compiled, profile: Uhd("libx264", 3500));
        Assert.Equal(2, weak.Warnings.Count);
        Assert.Contains("libx264 is H.264", weak.Warnings[0]);
        Assert.Contains("3500 kbps", weak.Warnings[1]);

        var starved = Assert.Single(StationHealth.Build(Station(), compiled, profile: Uhd("libx265", 8000)).Warnings);
        Assert.Contains("8000 kbps", starved);
        Assert.Contains("H.264", Assert.Single(StationHealth.Build(Station(), compiled, profile: Uhd("h264_qsv", 25000)).Warnings));
    }

    [Fact]
    public void TheShippedDefaultProfile_AndTheUsualLadder_RaiseNothing()
    {
        Assert.Empty(new EncodingProfile().Concerns());
        Assert.Empty(new EncodingProfile { Width = 1280, Height = 720, VideoBitrateKbps = 3500 }.Concerns());
        Assert.Empty(new EncodingProfile { Width = 1920, Height = 1080, VideoBitrateKbps = 8000 }.Concerns());
        Assert.Empty(new EncodingProfile { Width = 1920, Height = 1080, VideoBitrateKbps = 5000, VideoEncoder = "hevc_qsv" }.Concerns());
        Assert.Empty(new EncodingProfile { Width = 854, Height = 480, VideoBitrateKbps = 1500 }.Concerns());
        Assert.Empty(new EncodingProfile { Width = 3840, Height = 2160, VideoBitrateKbps = 20000, VideoEncoder = "hevc_nvenc" }.Concerns());
    }

    [Theory]
    [InlineData(1920, 1080, 30, "libx264", 2500, "1080p at 2500 kbps will look soft and blocky. Around 8000 kbps suits 1080p.")]
    [InlineData(1920, 1080, 30, "libx264", 3000, null)]
    [InlineData(1920, 1080, 30, "hevc_qsv", 1800, "1080p at 1800 kbps will look soft and blocky. Around 5000 kbps suits 1080p HEVC.")]
    [InlineData(1920, 1080, 60, "libx264", 3500, "1080p60 at 3500 kbps will look soft and blocky. Around 12000 kbps suits 1080p60.")]
    [InlineData(1280, 720, 30, "libx264", 1000, "720p at 1000 kbps will look soft and blocky. Around 3500 kbps suits 720p.")]
    [InlineData(1280, 720, 30, "h264_nvenc", 1500, null)]
    [InlineData(2560, 1440, 30, "libx264", 3500, "1440p at 3500 kbps will look soft and blocky. Around 12000 kbps suits 1440p.")]
    [InlineData(1600, 900, 30, "libx264", 2000, "900p at 2000 kbps will look soft and blocky. Around 8000 kbps suits 900p.")]
    [InlineData(854, 480, 30, "libx264", 400, "480p at 400 kbps will look soft and blocky. Around 1500 kbps suits 480p.")]
    [InlineData(3840, 2160, 60, "hevc_qsv", 12000, "4K60 at 12000 kbps will look soft and blocky. Around 30000 kbps suits 4K60 HEVC.")]
    [InlineData(3840, 2160, 30, "libx264", 3500, "4K at 3500 kbps will look soft and blocky. Around 20000 kbps suits 4K HEVC.")]
    public void ABitrateTooLowForThePicture_IsNamedWithTheFigureThatSuitsIt(int width, int height, int fps, string encoder, int kbps, string? expected)
    {
        var concerns = new EncodingProfile { Width = width, Height = height, Fps = fps, VideoEncoder = encoder, VideoBitrateKbps = kbps }.Concerns();

        if (expected is null)
        {
            Assert.Empty(concerns);
        }
        else
        {
            Assert.Contains(expected, concerns);
        }
    }

    [Fact]
    public void ABitrateLeftFromALargerChannel_IsCalledWasteful_AndThinAudioIsNamed()
    {
        // 20000 kbps left behind when a 4K station went back to 1080p.
        var left = Assert.Single(new EncodingProfile { Width = 1920, Height = 1080, VideoBitrateKbps = 20000 }.Concerns());
        Assert.Equal("1080p at 20000 kbps is more than the picture can use; it only makes every client's download and Jellyfin's transcodes larger. Around 8000 kbps suits 1080p.", left);
        Assert.Empty(new EncodingProfile { Width = 1920, Height = 1080, VideoBitrateKbps = 16000 }.Concerns());
        Assert.Empty(new EncodingProfile { Width = 1920, Height = 1080, Fps = 60, VideoBitrateKbps = 20000 }.Concerns());
        // An H.264 4K channel is sent to HEVC, never told its bitrate is too much.
        Assert.DoesNotContain(new EncodingProfile { Width = 3840, Height = 2160, VideoEncoder = "libx264", VideoBitrateKbps = 50000 }.Concerns(), c => c.Contains("more than", StringComparison.Ordinal));

        var thin = Assert.Single(new EncodingProfile { AudioBitrateKbps = 64 }.Concerns());
        Assert.Equal("Audio at 64 kbps will sound thin. 128 kbps or more suits AAC stereo.", thin);
        Assert.Empty(new EncodingProfile { AudioBitrateKbps = 96 }.Concerns());
    }

    [Fact]
    public void EncodingWarnings_AreTheStationsOwn_AndListedApart()
    {
        var compiled = Compiled(Items.SeriesRun("Show", 30, 60).ToArray());

        // No profile handed in: the station follows the default, whose concerns the report states once.
        var following = StationHealth.Build(Station(), compiled);
        Assert.True(following.Healthy);
        Assert.Empty(following.EncodingWarnings);

        var own = StationHealth.Build(Station(), compiled, profile: new EncodingProfile { Width = 1920, Height = 1080, VideoBitrateKbps = 1500 });
        Assert.Equal(own.EncodingWarnings, own.Warnings);
        Assert.Contains("1080p at 1500 kbps", Assert.Single(own.EncodingWarnings));
    }

    [Fact]
    public void UniqueHours_CountEachItemOnce_AcrossSections_IgnoringBumpers()
    {
        var a = Items.Movie("A", 60);
        var b = Items.Movie("B", 60);
        var compiled = Compiled(a, a, b);
        compiled.Blocks[Guid.NewGuid()] = new List<PlayableItem> { a, Items.Movie("C", 60) };
        compiled.Filler = new List<PlayableItem> { b };
        compiled.Bumpers = new List<PlayableItem> { Items.Movie("Ident", 1) };

        Assert.Equal(3, StationHealth.UniqueHours(compiled));
    }

    [Fact]
    public void SkippedItems_Warn_ButUserChoicesDoNot()
    {
        var compiled = Compiled(Items.SeriesRun("Show", 30, 60).ToArray());
        compiled.Excluded.Add(new ExcludedItem { ItemId = Guid.NewGuid(), Name = "x", Reason = ExclusionReason.NoRuntime });
        compiled.Excluded.Add(new ExcludedItem { ItemId = Guid.NewGuid(), Name = "y", Reason = ExclusionReason.NoRuntime });
        compiled.Excluded.Add(new ExcludedItem { ItemId = Guid.NewGuid(), Name = "z", Reason = ExclusionReason.NotFound });
        compiled.Excluded.Add(new ExcludedItem { ItemId = Guid.NewGuid(), Name = "w", Reason = ExclusionReason.UserExcluded });
        compiled.Excluded.Add(new ExcludedItem { ItemId = Guid.NewGuid(), Name = "v", Reason = ExclusionReason.TooShort });

        var h = StationHealth.Build(Station(), compiled);

        Assert.Equal(2, h.ExcludedByReason["NoRuntime"]);
        Assert.Equal(1, h.ExcludedByReason["UserExcluded"]);
        Assert.Equal(1, h.ExcludedByReason["TooShort"]);
        Assert.Equal(2, h.Warnings.Count);
        Assert.Contains("2 items skipped: no runtime", h.Warnings[0]);
        Assert.Contains("1 item skipped: no longer in the library", h.Warnings[1]);
    }
}
