using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Tests.Fixtures;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Scheduling;

/// <summary>"Why this is playing": the reason line the dashboard shows for an entry.</summary>
public class ScheduleTraceTests
{
    private static readonly TimeZoneInfo NewYork = TimeZones.Resolve("America/New_York");

    private static DateTime LocalToUtc(int h, int min) => TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, 12, h, min, 0, DateTimeKind.Unspecified), NewYork);

    private static ScheduleEntry Program(Station station, PlayableItem item, DateTime start, string? label = null)
        => new(station.Id, 1, EntryKind.Program, item, start, start + item.Runtime, false, label);

    [Fact]
    public void PoolEntry_NamesTheOrderTheSourceAndTheFamilyHours()
    {
        var station = Items.PoolStation(new List<PlayableItem>(), PlayOrder.ShuffleEpisodesInOrder);
        station.Pool.DaypartRules.Add(new DaypartRule { StartLocal = "06:00", EndLocal = "20:00", MaxRating = "PG" });
        var item = Items.Episode("Bluey", 1, 1, 22);
        item.Source = "Genre \"Kids\"";
        item.OfficialRating = "TV-G";

        var daytime = ScheduleTrace.Why(Program(station, item, LocalToUtc(9, 0)), station, null);
        Assert.Equal("shuffled, episodes in order · from Genre \"Kids\" · family hours: up to PG until 20:00", daytime);

        var film = Items.Movie("Heat", 170);
        film.OfficialRating = "R";
        var night = ScheduleTrace.Why(Program(station, film, LocalToUtc(21, 0)), station, null);
        Assert.Equal("shuffled, episodes in order · R: outside family hours", night);

        // A mild film at night: outside the window, nothing to say about the rating.
        var mild = Items.Movie("Paddington", 95);
        mild.OfficialRating = "PG";
        Assert.Equal("shuffled, episodes in order", ScheduleTrace.Why(Program(station, mild, LocalToUtc(21, 0)), station, null));
    }

    [Fact]
    public void BlockFillerAndLineupEntries_SayWhereTheyComeFrom()
    {
        var station = new Station
        {
            Id = Items.StationA,
            Mode = ScheduleMode.Slots,
            TimeZoneId = "America/New_York",
            Slots = new SlotsConfig
            {
                Blocks = { new SlotBlock { Name = "Prime", Order = PlayOrder.Sequential } },
                Filler = new FillerConfig { Order = PlayOrder.Shuffle }
            }
        };
        var item = Items.Episode("Seinfeld", 3, 4, 22);
        Assert.Equal("Block “Prime”, in order", ScheduleTrace.Why(Program(station, item, Items.Anchor, "Prime"), station, null));
        Assert.Equal("Between the blocks, shuffled", ScheduleTrace.Why(Program(station, item, Items.Anchor, "Filler"), station, null));

        var lineup = new Station { Id = Items.StationA, Mode = ScheduleMode.Lineup, TimeZoneId = "UTC" };
        Assert.Equal("Fixed list", ScheduleTrace.Why(Program(lineup, item, Items.Anchor), lineup, null));

        var slate = ScheduleEntry.Slate(Items.PoolInput(new List<PlayableItem> { item }), Items.Anchor, Items.Anchor.AddMinutes(5), "Not allowed at this time");
        Assert.Equal("Not allowed at this time", ScheduleTrace.Why(slate, station, null));
    }

    [Fact]
    public void Explain_CountsMarathonRuns_AcrossBumpersCardsAndWeather()
    {
        var station = Items.PoolStation(new List<PlayableItem>(), PlayOrder.Shuffle);
        station.Pool.MarathonMin = 2;
        station.Pool.MarathonMax = 3;
        var input = Items.PoolInput(new List<PlayableItem> { Items.Movie("x", 1) });
        var a = Items.SeriesRun("Bluey", 3, 22);
        var b = Items.Episode("Frasier", 1, 1, 22);
        var t = Items.Anchor;
        var entries = new List<ScheduleEntry>
        {
            Program(station, a[0], t),
            ScheduleEntry.Slate(input, t.AddMinutes(22), t.AddMinutes(30)),
            Program(station, a[1], t.AddMinutes(30)),
            new ScheduleEntry(station.Id, 1, EntryKind.Weather, null, t.AddMinutes(52), t.AddMinutes(57), false, "Weather every 10"),
            Program(station, a[2], t.AddMinutes(60)),
            Program(station, b, t.AddMinutes(90))
        };

        var why = ScheduleTrace.Explain(entries, station, null);
        Assert.Equal("shuffled · marathon 1 of 3", why[0]);
        Assert.Null(why[1]);
        Assert.Equal("shuffled · marathon 2 of 3", why[2]);
        Assert.Equal("Weather block", why[3]);
        Assert.Equal("shuffled · marathon 3 of 3", why[4]);
        Assert.Equal("shuffled", why[5]);

        // A run longer than the station's marathons (the family hours left only this series) is not called one.
        station.Pool.MarathonMax = 2;
        Assert.All(ScheduleTrace.Explain(entries, station, null).Where(w => w is not null), w => Assert.DoesNotContain("marathon", w));

        // A station without marathons never calls two episodes in a row one.
        station.Pool.MarathonMax = 1;
        Assert.All(ScheduleTrace.Explain(entries, station, null).Where(w => w is not null), w => Assert.DoesNotContain("marathon", w));
    }

    [Fact]
    public void FollowingTheHousehold_IsSaidForSeriesWithAPosition()
    {
        var station = Items.PoolStation(new List<PlayableItem>(), PlayOrder.ShuffleEpisodesInOrder);
        station.Household.FollowProgress = true;
        var item = Items.Episode("Bluey", 1, 5, 22);
        var household = new HouseholdSnapshot { Progress = { [PoolOrdering.KeyOf(item)] = new HouseholdPosition { ItemId = Items.DeterministicGuid("Bluey-S1E4"), Season = 1, Episode = 4 } } };
        Assert.Equal("shuffled, episodes in order · following the household's progress", ScheduleTrace.Why(Program(station, item, Items.Anchor), station, household));
        Assert.Equal("shuffled, episodes in order", ScheduleTrace.Why(Program(station, Items.Episode("Frasier", 1, 1, 22), Items.Anchor), station, household));
    }
}
