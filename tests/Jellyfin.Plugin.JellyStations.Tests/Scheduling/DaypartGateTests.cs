using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Tests.Fixtures;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Scheduling;

public class DaypartGateTests
{
    private static readonly TimeZoneInfo NewYork = TimeZones.Resolve("America/New_York");

    private static DaypartRule Rule(string start, string end, string max, bool unrated = false)
        => new() { StartLocal = start, EndLocal = end, MaxRating = max, AllowUnrated = unrated };

    private static List<PlayableItem> MixedPool()
    {
        var pool = new List<PlayableItem>();
        foreach (var (title, rating, minutes) in new[] { ("Kids A", "G", 50), ("Kids B", "PG", 60), ("Teen", "PG-13", 90), ("Adult A", "R", 100), ("Adult B", "R", 95), ("Unrated", (string?)null, 45) })
        {
            var m = Items.Movie(title, minutes);
            m.OfficialRating = rating;
            pool.Add(m);
        }

        pool.AddRange(Items.SeriesRun("Cartoon", 6, 22).Select(e => { e.OfficialRating = "TV-Y7"; return e; }));
        pool.AddRange(Items.SeriesRun("Crime", 5, 44).Select(e => { e.OfficialRating = "TV-MA"; return e; }));
        return pool;
    }

    private static ScheduleInput Input(List<PlayableItem> pool, PlayOrder order, params DaypartRule[] rules)
    {
        var station = Items.PoolStation(pool, order);
        station.Pool.DaypartRules = rules.ToList();
        station.Pool.MarathonMin = order == PlayOrder.Sequential ? 1 : 2;
        station.Pool.MarathonMax = order == PlayOrder.Sequential ? 1 : 3;
        return ScheduleInput.From(station, new CompiledStation { StationId = station.Id, Revision = 1, Pool = pool });
    }

    private static DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, NewYork);

    private static bool InDaytime(ScheduleEntry e)
    {
        // Any overlap with 06:00-20:00 local on any day the entry touches.
        var rule = new DaypartRuleInput { Start = TimeSpan.FromHours(6), End = TimeSpan.FromHours(20), MaxRank = 0 };
        return DaypartGate.Overlaps(rule, Local(e.StartUtc), Local(e.EndUtc));
    }

    [Theory]
    [InlineData(PlayOrder.Shuffle)]
    [InlineData(PlayOrder.ShuffleEpisodesInOrder)]
    [InlineData(PlayOrder.Sequential)]
    public void NothingAbovePgInDaytime_AcrossAWeek(PlayOrder order)
    {
        var input = Input(MixedPool(), order, Rule("06:00", "20:00", "PG"));
        var entries = ScheduleResolver.EnumerateInput(input, Items.Anchor, Items.Anchor.AddDays(7));

        var violations = entries.Where(e => e.Kind == EntryKind.Program && InDaytime(e) && !Jellyfin.Plugin.JellyStations.Library.RatingLadder.IsAtMost(e.Item!.OfficialRating, "PG")).ToList();
        Assert.Empty(violations);

        // The gate did not remove content: adult items still air (at night).
        Assert.Contains(entries, e => e.Kind == EntryKind.Program && e.Item!.OfficialRating == "R");
        Assert.Contains(entries, e => e.Kind == EntryKind.Program && e.Item!.OfficialRating == "TV-MA");

        for (var i = 1; i < entries.Count; i++)
        {
            Assert.Equal(entries[i - 1].EndUtc, entries[i].StartUtc);
        }
    }

    [Fact]
    public void Unrated_IsBlockedUnlessAllowed()
    {
        var pool = MixedPool();
        var strict = Input(pool, PlayOrder.Shuffle, Rule("06:00", "20:00", "PG"));
        var lenient = Input(pool, PlayOrder.Shuffle, Rule("06:00", "20:00", "PG", unrated: true));
        var day = Items.Anchor.AddDays(3);

        Assert.DoesNotContain(ScheduleResolver.EnumerateInput(strict, day, day.AddDays(2)), e => e.Kind == EntryKind.Program && e.Item!.OfficialRating is null && InDaytime(e));
        Assert.Contains(ScheduleResolver.EnumerateInput(lenient, day, day.AddDays(7)), e => e.Kind == EntryKind.Program && e.Item!.OfficialRating is null && InDaytime(e));
    }

    [Fact]
    public void AllDayRule_NothingFits_SlotsBecomeSlates_AndCycleLengthIsKept()
    {
        var pool = new List<PlayableItem>();
        foreach (var t in new[] { "A", "B", "C" })
        {
            var m = Items.Movie(t, 100);
            m.OfficialRating = "R";
            pool.Add(m);
        }

        var input = Input(pool, PlayOrder.Shuffle, Rule("00:00", "00:00", "PG"));
        var entries = ScheduleResolver.EnumerateInput(input, Items.Anchor, Items.Anchor.AddMinutes(300));
        Assert.All(entries, e => Assert.Equal(EntryKind.Slate, e.Kind));
        Assert.Equal(3, entries.Count(e => e.StartUtc >= Items.Anchor && e.StartUtc < Items.Anchor.AddMinutes(300)));
        Assert.Equal(TimeSpan.FromMinutes(100), entries[0].Duration);
        Assert.Equal("Not allowed at this time", entries[0].SourceLabel);
    }

    [Fact]
    public void Resolve_MatchesEnumerate_WithGate()
    {
        var input = Input(MixedPool(), PlayOrder.ShuffleEpisodesInOrder, Rule("06:00", "20:00", "PG"), Rule("22:00", "06:00", "R"));
        var from = Items.Anchor;
        var to = Items.Anchor.AddDays(4);
        var entries = ScheduleResolver.EnumerateInput(input, from, to);
        var rng = new Random(11);
        for (var n = 0; n < 300; n++)
        {
            var t = from.AddSeconds(rng.NextDouble() * (to - from).TotalSeconds);
            var r = ScheduleResolver.ResolveInput(input, t)!;
            Assert.Equal(entries.Single(e => e.Contains(t)), r.Entry);
        }
    }

    [Fact]
    public void Lineup_IsNotReordered_ButDisallowedSlotsBecomeSlates()
    {
        var a = Items.Movie("Kids", 60);
        a.OfficialRating = "G";
        var b = Items.Movie("Adult", 60);
        b.OfficialRating = "R";
        var station = Items.PoolStation(new[] { a, b }, PlayOrder.Sequential);
        station.Mode = ScheduleMode.Lineup;
        station.Pool.DaypartRules = new List<DaypartRule> { Rule("00:00", "00:00", "PG") };
        var input = ScheduleInput.From(station, new CompiledStation { StationId = station.Id, Revision = 1, Lineup = new List<PlayableItem> { a, b } });

        var entries = ScheduleResolver.EnumerateInput(input, Items.Anchor, Items.Anchor.AddMinutes(120));
        Assert.Equal("Kids", entries[0].Item!.Title);
        Assert.Equal(EntryKind.Slate, entries[1].Kind);
    }

    [Theory]
    [InlineData("06:00", "20:00", "2026-10-14T05:00", 30, false)]
    [InlineData("06:00", "20:00", "2026-10-14T05:45", 30, true)]
    [InlineData("06:00", "20:00", "2026-10-14T19:59", 1, true)]
    [InlineData("06:00", "20:00", "2026-10-14T20:00", 60, false)]
    [InlineData("22:00", "06:00", "2026-10-14T23:00", 60, true)]
    [InlineData("22:00", "06:00", "2026-10-15T05:30", 60, true)]
    [InlineData("22:00", "06:00", "2026-10-15T12:00", 60, false)]
    [InlineData("06:00", "20:00", "2026-10-14T04:00", 1200, true)]
    public void Overlaps_HandlesWrapAndLongItems(string start, string end, string localStart, int minutes, bool expected)
    {
        var rule = new DaypartRuleInput { Start = TimeSpan.Parse(start), End = TimeSpan.Parse(end), MaxRank = 0 };
        var s = DateTime.Parse(localStart);
        Assert.Equal(expected, DaypartGate.Overlaps(rule, s, s.AddMinutes(minutes)));
    }
}
