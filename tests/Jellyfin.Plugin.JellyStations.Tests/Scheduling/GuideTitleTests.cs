using Jellyfin.Plugin.JellyStations.LiveTv;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Tests.Fixtures;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Scheduling;

/// <summary>A time block's guide title rides on its programs; the Live TV guide shows it and the New / Repeat / Kids flags.</summary>
public class GuideTitleTests
{
    private static readonly Guid BlockId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly TimeZoneInfo NewYork = TimeZones.Resolve("America/New_York");

    private static DateTime LocalToUtc(int y, int m, int d, int h, int min)
        => TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, m, d, h, min, 0, DateTimeKind.Unspecified), NewYork);

    private static ScheduleInput Input(string? guideTitle)
    {
        var block = new SlotBlock
        {
            Id = BlockId,
            Name = "Toons",
            GuideTitle = guideTitle,
            Days = new List<DayOfWeek> { DayOfWeek.Saturday },
            StartLocal = "07:00",
            DurationMinutes = 60,
            Order = PlayOrder.Sequential,
            Overflow = SlotOverflow.FillerRemainder
        };
        // Two 22-minute episodes fill 44 minutes; the filler takes the remaining 16.
        var blockItems = new Dictionary<Guid, List<PlayableItem>> { [BlockId] = Items.SeriesRun("Bluey", 2, 22) };
        return Items.SlotsInput(new List<SlotBlock> { block }, blockItems, Items.SeriesRun("Cartoons", 30, 11));
    }

    [Fact]
    public void BlockPrograms_CarryTheGuideTitle_FillerKeepsItsOwn()
    {
        var start = LocalToUtc(2026, 10, 17, 7, 0);
        var entries = ScheduleResolver.EnumerateInput(Input("  Saturday Morning Cartoons "), start, start.AddHours(1))
            .Where(e => e.Kind == EntryKind.Program && e.StartUtc >= start)
            .ToList();

        var inBlock = entries.Where(e => e.SourceLabel == "Toons").ToList();
        Assert.Equal(2, inBlock.Count);
        Assert.All(inBlock, e => Assert.Equal("Saturday Morning Cartoons", e.GuideTitle));
        Assert.All(inBlock, e => Assert.Equal("Bluey", e.Title));

        var filler = entries.Where(e => e.SourceLabel != "Toons").ToList();
        Assert.NotEmpty(filler);
        Assert.All(filler, e => Assert.Null(e.GuideTitle));
    }

    [Fact]
    public void WithoutAGuideTitle_NothingChanges()
    {
        var start = LocalToUtc(2026, 10, 17, 7, 0);
        var entries = ScheduleResolver.EnumerateInput(Input(null), start, start.AddHours(1)).ToList();
        Assert.All(entries, e => Assert.Null(e.GuideTitle));
    }

    [Fact]
    public void GuideEntry_ShowsTheBlockTitle_WithTheProgramOnTheEpisodeLine()
    {
        var item = Items.Episode("Bluey", 1, 3, 22);
        var entry = new ScheduleEntry(Items.StationA, 1, EntryKind.Program, item, Items.Anchor, Items.Anchor.AddMinutes(22), false, "Toons", GuideTitle: "Saturday Morning Cartoons");
        var p = JellyStationsLiveTvService.ToProgram("ch", entry, item);
        Assert.Equal("Saturday Morning Cartoons", p.Name);
        Assert.Equal("Bluey – Episode 3", p.EpisodeTitle);

        var plain = JellyStationsLiveTvService.ToProgram("ch", entry with { GuideTitle = null }, item);
        Assert.Equal("Bluey", plain.Name);
        Assert.Equal("Episode 3", plain.EpisodeTitle);

        var movie = Items.Movie("Jaws", 124);
        var movieEntry = entry with { Item = movie, GuideTitle = "The Late Movie" };
        var m = JellyStationsLiveTvService.ToProgram("ch", movieEntry, movie);
        Assert.Equal("The Late Movie", m.Name);
        Assert.Equal("Jaws", m.EpisodeTitle);
        Assert.Null(JellyStationsLiveTvService.ToProgram("ch", movieEntry with { GuideTitle = null }, movie).EpisodeTitle);
    }

    [Fact]
    public void NewAndRepeat_ComeFromTheAirLog()
    {
        var item = Items.Episode("Bluey", 1, 3, 22);
        var start = Items.Anchor.AddHours(10);
        var entry = new ScheduleEntry(Items.StationA, 1, EntryKind.Program, item, start, start.AddMinutes(22), false, "Toons");

        // No log, or no record of the item: new.
        Assert.True(JellyStationsLiveTvService.ToProgram("ch", entry, item).IsPremiere);
        Assert.False(JellyStationsLiveTvService.ToProgram("ch", entry, item, new AirLog()).IsRepeat);

        // Aired once, earlier: a repeat.
        var earlier = new AirLog();
        earlier.Items[item.ItemId] = new AirLogItem { LastStartUtc = start.AddDays(-1), Count = 1 };
        var repeat = JellyStationsLiveTvService.ToProgram("ch", entry, item, earlier);
        Assert.True(repeat.IsRepeat);
        Assert.False(repeat.IsPremiere);

        // The log's only airing is this very one (it records a program when it starts): still new.
        var current = new AirLog();
        current.Items[item.ItemId] = new AirLogItem { LastStartUtc = start, Count = 1 };
        Assert.True(JellyStationsLiveTvService.ToProgram("ch", entry, item, current).IsPremiere);

        // Aired before and airing now: a repeat.
        current.Items[item.ItemId].Count = 2;
        Assert.True(JellyStationsLiveTvService.ToProgram("ch", entry, item, current).IsRepeat);
    }

    [Theory]
    [InlineData("G", true)]
    [InlineData("TV-Y7", true)]
    [InlineData("TV-G", true)]
    [InlineData("PG", false)]
    [InlineData("TV-14", false)]
    [InlineData(null, false)]
    public void Kids_IsAnythingRatedTvGOrBelow(string? rating, bool kids)
    {
        var item = Items.Episode("Bluey", 1, 3, 22);
        item.OfficialRating = rating;
        var entry = new ScheduleEntry(Items.StationA, 1, EntryKind.Program, item, Items.Anchor, Items.Anchor.AddMinutes(22), false, null);
        Assert.Equal(kids, JellyStationsLiveTvService.ToProgram("ch", entry, item).IsKids);
    }
}
