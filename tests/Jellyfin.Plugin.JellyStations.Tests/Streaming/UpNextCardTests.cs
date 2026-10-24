using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Streaming;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Streaming;

public class UpNextCardTests
{
    private static readonly Guid StationId = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);
    private static readonly EncodingProfile Hd = new() { Width = 1920, Height = 1080 };

    private static PlayableItem Episode(string? image = null) => new()
    {
        ItemId = Guid.NewGuid(),
        Title = "Frasier",
        EpisodeTitle = "The Good Son",
        SeasonNumber = 1,
        EpisodeNumber = 1,
        IsSeries = true,
        PrimaryImagePath = image
    };

    private static PlayableItem Movie() => new() { ItemId = Guid.NewGuid(), Title = "Dogma", IsMovie = true, ProductionYear = 1999 };

    private static ScheduleEntry Entry(EntryKind kind, PlayableItem? item, int startMinutes, int minutes)
        => new(StationId, 1, kind, item, T0.AddMinutes(startMinutes), T0.AddMinutes(startMinutes + minutes), false, null);

    [Fact]
    public void NextProgram_SkipsBumpersAndSlates()
    {
        var upcoming = new[]
        {
            Entry(EntryKind.Program, Movie(), -90, 120), // the one airing now, ends at T0+30
            Entry(EntryKind.Bumper, Movie(), 30, 1),
            Entry(EntryKind.Slate, null, 31, 4),
            Entry(EntryKind.Program, Episode(), 35, 25),
            Entry(EntryKind.Program, Movie(), 60, 90)
        };
        var next = UpNextCard.NextProgram(upcoming, T0.AddMinutes(30));
        Assert.NotNull(next);
        Assert.Equal("Frasier", next!.Item!.Title);
        Assert.Null(UpNextCard.NextProgram(upcoming.Take(3), T0.AddMinutes(30)));
    }

    [Fact]
    public void Windows_SitAtTheEndOfTheSegment()
    {
        var c = new UpNextCardConfig { LeadTimesSeconds = new List<int> { 30 }, ShowSeconds = 10 };
        Assert.Equal(new[] { (570.0, 580.0) }, UpNextCard.Windows(TimeSpan.FromSeconds(600), c));
        Assert.Empty(UpNextCard.Windows(TimeSpan.FromSeconds(30), c));

        var longer = new UpNextCardConfig { LeadTimesSeconds = new List<int> { 10 }, ShowSeconds = 60 };
        Assert.Equal(new[] { (590.0, 600.0) }, UpNextCard.Windows(TimeSpan.FromSeconds(600), longer));
    }

    [Fact]
    public void Windows_OnePerLeadTimeThatFitsTheSegment()
    {
        // An hour, half an hour and five minutes before the end of a two-hour film.
        var c = new UpNextCardConfig { LeadTimesSeconds = new List<int> { 300, 3600, 1800 }, ShowSeconds = 10 };
        Assert.Equal(new[] { (3600.0, 3610.0), (5400.0, 5410.0), (6900.0, 6910.0) }, UpNextCard.Windows(TimeSpan.FromSeconds(7200), c));

        // Joined 100 minutes in: only the five-minute showing is still ahead.
        Assert.Equal(new[] { (900.0, 910.0) }, UpNextCard.Windows(TimeSpan.FromSeconds(1200), c));
    }

    [Theory]
    [InlineData(20, 0, 21, 30, false, "8:00 – 9:30 PM")]
    [InlineData(23, 30, 25, 0, false, "11:30 PM – 1:00 AM")]
    [InlineData(9, 5, 10, 0, false, "9:05 – 10:00 AM")]
    [InlineData(20, 0, 21, 30, true, "20:00 – 21:30")]
    public void TimeRange_ReadsLikeAGuide(int h1, int m1, int h2, int m2, bool clock24, string expected)
    {
        var start = T0.AddHours(h1).AddMinutes(m1);
        var end = T0.AddHours(h2).AddMinutes(m2);
        Assert.Equal(expected, UpNextCard.TimeRange(start, end, TimeZoneInfo.Utc, clock24));
    }

    [Fact]
    public void TimeRange_UsesTheStationZone()
    {
        var minusFive = TimeZoneInfo.CreateCustomTimeZone("test-5", TimeSpan.FromHours(-5), "UTC-5", "UTC-5");
        Assert.Equal("7:00 – 8:00 PM", UpNextCard.TimeRange(T0.AddHours(24), T0.AddHours(25), minusFive, false));
        Assert.Equal(TimeZoneInfo.Utc, UpNextCard.Zone("No/Such_Zone"));
        Assert.Equal(TimeZoneInfo.Utc, UpNextCard.Zone(null));
    }

    [Fact]
    public void Spec_DescribesAnEpisode()
    {
        var c = new UpNextCardConfig { HeightPercent = 14 };
        var spec = UpNextCard.Spec(Entry(EntryKind.Program, Episode("/no/such/poster.jpg"), 60, 30), c, TimeZoneInfo.Utc, Hd);
        Assert.Equal("UP NEXT", spec.Label);
        Assert.Equal("Frasier", spec.Title);
        Assert.Equal("S1 E1 · The Good Son", spec.Subtitle);
        Assert.Equal("1:00 – 1:30 AM", spec.Times);
        Assert.Null(spec.ThumbnailPath); // the file does not exist
        Assert.Equal(151, spec.Height);
        Assert.Equal(634, spec.Width); // 4.2 × height, under 45% of the picture width
        Assert.Equal(85, spec.Opacity);
    }

    [Fact]
    public void Spec_DescribesAMovie_AndHonoursTheSwitches()
    {
        var c = new UpNextCardConfig { ShowTimes = false, ShowThumbnail = false, Opacity = 50 };
        var spec = UpNextCard.Spec(Entry(EntryKind.Program, Movie(), 60, 90), c, TimeZoneInfo.Utc, Hd);
        Assert.Equal("Dogma", spec.Title);
        Assert.Equal("1999", spec.Subtitle);
        Assert.Null(spec.Times);
        Assert.Null(spec.ThumbnailPath);
        Assert.Equal(50, spec.Opacity);
    }

    [Fact]
    public void Fit_CutsLongLinesWithAnEllipsis()
    {
        Assert.Equal("Short", UpNextCard.Fit("Short", 400, 30));
        var cut = UpNextCard.Fit("A very long title that will not fit on one line of the card", 300, 30);
        Assert.EndsWith("…", cut, StringComparison.Ordinal);
        Assert.True(cut.Length <= 19);
    }

    [Fact]
    public void Normalize_ClampsEverything()
    {
        var c = new UpNextCardConfig { LeadTimesSeconds = new List<int> { 2, 60, 60, 99999, 300 }, ShowSeconds = 99, HeightPercent = 90, Opacity = 1, Corner = (LogoCorner)42, Animation = (UpNextCardAnimation)9 };
        c.Normalize();
        Assert.Equal(new List<int> { 21600, 300, 60, 5 }, c.LeadTimesSeconds);
        Assert.Equal(5, c.ShowSeconds);

        var empty = new UpNextCardConfig { LeadTimesSeconds = new List<int>() };
        empty.Normalize();
        Assert.Equal(new List<int> { 30 }, empty.LeadTimesSeconds);
        Assert.Equal(30, c.HeightPercent);
        Assert.Equal(30, c.Opacity);
        Assert.Equal(LogoCorner.BottomRight, c.Corner);
        Assert.Equal(UpNextCardAnimation.Fade, c.Animation);
        Assert.Equal(UpNextCardAnimation.Fade, new UpNextCardConfig().Animation);
    }

    [Fact]
    public void CardArgs_DrawTheLinesOnATranslucentPanel()
    {
        var spec = new UpNextCardSpec("UP NEXT", "Frasier: Season 1", "S1 E1 · The Good Son", "8:00 – 8:30 PM", null, 634, 151, 85);
        var args = FfmpegArgsBuilder.ForUpNextCard(spec, "/data/cards/x.png");
        var joined = string.Join(' ', args);
        Assert.Contains("-f lavfi -i color=c=black@0.85:s=634x151:r=1:d=1", joined, StringComparison.Ordinal);
        Assert.Equal(1, args.Count(a => a == "-i")); // no artwork
        var graph = args[args.IndexOf("-filter_complex") + 1];
        Assert.Equal(4, graph.Split("drawtext=").Length - 1);
        Assert.Contains("text='Frasier\\: Season 1'", graph, StringComparison.Ordinal);
        Assert.Contains("text='8\\:00 – 8\\:30 PM'", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("[thumb]", graph, StringComparison.Ordinal);
        Assert.Equal("[c4]", args[args.IndexOf("-map") + 1]);
        Assert.Equal("1", args[args.IndexOf("-frames:v") + 1]);
        Assert.Equal("rgba", args[args.IndexOf("-pix_fmt") + 1]);
        Assert.Equal("/data/cards/x.png", args[^1]);
    }

    [Fact]
    public void CardArgs_WithArtwork_ScaleItIntoTheLeftBox()
    {
        var spec = new UpNextCardSpec("UP NEXT", "Dogma", "1999", null, "/library/Dogma/poster.jpg", 634, 151, 85);
        var args = FfmpegArgsBuilder.ForUpNextCard(spec, "/data/cards/x.png");
        Assert.Equal("/library/Dogma/poster.jpg", args[args.LastIndexOf("-i") + 1]);
        var graph = args[args.IndexOf("-filter_complex") + 1];
        Assert.Contains("[1:v]scale=181:131:force_original_aspect_ratio=decrease", graph, StringComparison.Ordinal);
        Assert.Equal(3, graph.Split("drawtext=").Length - 1);
        Assert.Equal("[c3]", args[args.IndexOf("-map") + 1]);
    }
}
