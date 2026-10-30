using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Tests.Fixtures;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Scheduling;

/// <summary>Weather on ordinary stations: in the breaks (a bumper) and as a time block.</summary>
public class WeatherOnStationsTests
{
    private static readonly Guid BlockId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly TimeZoneInfo NewYork = TimeZones.Resolve("America/New_York");

    private static WeatherConfig Places(int count = 1)
    {
        var w = new WeatherConfig { GuideTitle = "Weather now: {places}" };
        if (count > 0)
        {
            w.Locations.Add(new WeatherLocation { Id = Guid.NewGuid(), Name = "Boston", Region = "Massachusetts", Latitude = 42.36, Longitude = -71.06, TimeZoneId = "America/New_York" });
        }

        if (count > 1)
        {
            w.Locations.Add(new WeatherLocation { Id = Guid.NewGuid(), Name = "Denver", Region = "Colorado", Latitude = 39.74, Longitude = -104.99, TimeZoneId = "America/Denver" });
        }

        return w;
    }

    private static List<PlayableItem> Clips()
    {
        var ident = Items.Movie("Ident 30s", 1);
        ident.RuntimeTicks = TimeSpan.FromSeconds(30).Ticks;
        return new List<PlayableItem> { ident, Items.Movie("Promo 1m", 1) };
    }

    private static BumperFill.Clips WeatherClips(int minutes, bool first, IReadOnlyList<PlayableItem>? fill = null)
        => new(fill ?? Array.Empty<PlayableItem>(), Array.Empty<PlayableItem>(), Array.Empty<PlayableItem>(), TimeSpan.FromMinutes(minutes), first, "Weather now: Boston");

    private static DateTime LocalToUtc(int y, int m, int d, int h, int min)
        => TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, m, d, h, min, 0, DateTimeKind.Unspecified), NewYork);

    // ---- BumperFill ----------------------------------------------------------------------------

    [Fact]
    public void Plan_WeatherTakesTheTailOfTheBreak_AndStretchesIntoWhatTheClipsLeave()
    {
        // 4-minute gap, 2 minutes of weather at the tail, clips of 30 s and 60 s: clips first, then the weather
        // absorbs the 30 s the clips could not use, up to its 2 minutes... here 4:00 - 1:30 = 2:30 > 2:00, so a
        // 30 s card sits between the clips and the weather.
        var pieces = BumperFill.Plan(TimeSpan.FromMinutes(4).Ticks, WeatherClips(2, first: false, Clips()), 11UL);

        Assert.Equal(0, pieces[0].StartOffset);
        for (var i = 1; i < pieces.Count; i++)
        {
            Assert.Equal(pieces[i - 1].EndOffset, pieces[i].StartOffset);
        }

        Assert.Equal(TimeSpan.FromMinutes(4).Ticks, pieces[^1].EndOffset);
        Assert.True(pieces[^1].IsWeather);
        Assert.Equal(TimeSpan.FromMinutes(2).Ticks, pieces[^1].EndOffset - pieces[^1].StartOffset);
        Assert.Equal(2, pieces.Count(p => p.Clip is not null));
        Assert.Single(pieces, p => p.IsCard);
        Assert.Equal(TimeSpan.FromSeconds(30).Ticks, pieces.Single(p => p.IsCard).EndOffset - pieces.Single(p => p.IsCard).StartOffset);

        // A 3-minute gap: the weather keeps its 2 minutes, so only the 30 s clip fits in the minute left; the
        // weather is capped at its length, so the 30 s the clips could not use is a card.
        var tight = BumperFill.Plan(TimeSpan.FromMinutes(3).Ticks, WeatherClips(2, first: false, Clips()), 11UL);
        Assert.Equal(3, tight.Count);
        Assert.Equal("Ident 30s", tight[0].Clip!.Title);
        Assert.True(tight[1].IsCard);
        Assert.True(tight[^1].IsWeather);
        Assert.Equal(TimeSpan.FromMinutes(2).Ticks, tight[^1].EndOffset - tight[^1].StartOffset);

        // A 2:30 gap: 30 s for clips (the ident), then the weather takes all 2 minutes; no card.
        var exact = BumperFill.Plan(TimeSpan.FromSeconds(150).Ticks, WeatherClips(2, first: false, Clips()), 11UL);
        Assert.Equal(2, exact.Count);
        Assert.DoesNotContain(exact, p => p.IsCard);
        Assert.True(exact[^1].IsWeather);
    }

    [Fact]
    public void Plan_WeatherAtTheHead_ComesFirst_ThenClips_ThenTheCard()
    {
        var pieces = BumperFill.Plan(TimeSpan.FromMinutes(4).Ticks, WeatherClips(2, first: true, Clips()), 11UL);
        Assert.True(pieces[0].IsWeather);
        Assert.Equal(TimeSpan.FromMinutes(2).Ticks, pieces[0].EndOffset);
        Assert.Equal(2, pieces.Count(p => p.Clip is not null));
        Assert.True(pieces[^1].IsCard);
        Assert.Equal(TimeSpan.FromMinutes(4).Ticks, pieces[^1].EndOffset);
    }

    [Fact]
    public void Plan_WeatherNeedsAMinimum_AndStaysInsideThePrePostRolls()
    {
        // 40 s of room is under the minimum: the card stays.
        var small = BumperFill.Plan(TimeSpan.FromSeconds(40).Ticks, WeatherClips(2, first: false), 1UL);
        Assert.Single(small);
        Assert.True(small[0].IsCard);

        // 50 s is enough: the whole gap becomes weather.
        var enough = BumperFill.Plan(TimeSpan.FromSeconds(50).Ticks, WeatherClips(2, first: false), 1UL);
        Assert.Single(enough);
        Assert.True(enough[0].IsWeather);

        // With a pre-roll ident the weather sits right before it.
        var ident = Items.Movie("Ident", 1);
        ident.RuntimeTicks = TimeSpan.FromSeconds(20).Ticks;
        var clips = new BumperFill.Clips(Array.Empty<PlayableItem>(), new[] { ident }, Array.Empty<PlayableItem>(), TimeSpan.FromMinutes(1), false, "W");
        var pieces = BumperFill.Plan(TimeSpan.FromMinutes(3).Ticks, clips, 1UL);
        Assert.Equal(BumperRole.PreRoll, pieces[^1].Role);
        Assert.True(pieces[^2].IsWeather);
        Assert.Equal(pieces[^1].StartOffset, pieces[^2].EndOffset);
        Assert.Equal(TimeSpan.FromMinutes(1).Ticks, pieces[^2].EndOffset - pieces[^2].StartOffset);
        Assert.True(pieces[0].IsCard);
    }

    [Fact]
    public void ToEntry_MakesWeatherInABreak_WithItsTitle()
    {
        var slate = new ScheduleEntry(Items.StationA, 1, EntryKind.Slate, null, Items.Anchor, Items.Anchor.AddMinutes(3), false, null);
        var pieces = BumperFill.Plan(slate.Duration.Ticks, WeatherClips(2, first: false), 3UL);
        var entries = pieces.Select(p => BumperFill.ToEntry(slate, p, "Weather now: Boston")).ToList();

        var weather = Assert.Single(entries, e => e.Kind == EntryKind.Weather);
        Assert.True(weather.InBreak);
        Assert.Equal("Weather now: Boston", weather.Title);
        Assert.Null(weather.Item);
        Assert.Equal(Items.Anchor.AddMinutes(1), weather.StartUtc);
        Assert.Equal(Items.Anchor.AddMinutes(3), weather.EndUtc);
        Assert.Equal(EntryKind.Slate, entries[0].Kind);
        Assert.False(entries[0].InBreak);
    }

    // ---- ScheduleInput / Station ---------------------------------------------------------------

    [Fact]
    public void UsesWeather_IsOn_ForAWeatherChannel_AWeatherBlock_OrWeatherBreaks()
    {
        var station = Items.PoolStation(new List<PlayableItem>());
        Assert.False(station.UsesWeather);

        station.Bumpers = new BumperConfig { Enabled = true, Weather = new WeatherBreakConfig { Enabled = true } };
        Assert.True(station.UsesWeather);
        Assert.True(station.HasWeatherBreaks);

        station.Bumpers.Enabled = false;
        Assert.False(station.UsesWeather);

        station.Slots.Blocks.Add(new SlotBlock { Kind = BlockKind.Weather, Days = new List<DayOfWeek> { DayOfWeek.Monday } });
        Assert.True(station.UsesWeather);
        Assert.True(station.HasWeatherBlocks);

        station.Mode = ScheduleMode.Lineup;
        Assert.False(station.UsesWeather); // blocks do not apply to a lineup

        station.Mode = ScheduleMode.Weather;
        station.Slots.Blocks.Clear();
        Assert.True(station.UsesWeather);
        Assert.False(station.HasWeatherBreaks);
    }

    [Fact]
    public void ScheduleInput_ReservesWeatherInBreaks_OnlyWithAPlace()
    {
        var pool = new List<PlayableItem> { Items.Movie("A", 47) };
        var station = Items.PoolStation(pool, PlayOrder.Sequential, grid: 15);
        station.Bumpers = new BumperConfig { Enabled = true, Weather = new WeatherBreakConfig { Enabled = true, Minutes = 3, Position = BreakPosition.AfterProgram } };
        var compiled = new CompiledStation { StationId = station.Id, Revision = 1, Pool = pool };

        var noPlace = ScheduleInput.From(station, compiled, station.Weather);
        Assert.Equal(TimeSpan.Zero, noPlace.WeatherBreak);
        Assert.False(noPlace.HasBumpers);
        Assert.NotNull(noPlace.Weather); // the station still "uses" weather, so the settings travel

        station.Weather = Places();
        var input = ScheduleInput.From(station, compiled, station.Weather);
        Assert.Equal(TimeSpan.FromMinutes(3), input.WeatherBreak);
        Assert.True(input.WeatherBreakFirst);
        Assert.Equal("Weather now: Boston", input.WeatherLabel);
        Assert.True(input.HasBumpers);

        station.Bumpers.Enabled = false;
        Assert.Equal(TimeSpan.Zero, ScheduleInput.From(station, compiled, station.Weather).WeatherBreak);
    }

    [Fact]
    public void StartsOfDay_RepeatsUntilTheLimit_InclusiveAndBeforeMidnight()
    {
        var block = new SlotBlockInput { StartLocal = TimeSpan.FromHours(6), Duration = TimeSpan.FromMinutes(5), RepeatEvery = TimeSpan.FromHours(1), RepeatUntil = TimeSpan.FromHours(9) };
        Assert.Equal(new[] { TimeSpan.FromHours(6), TimeSpan.FromHours(7), TimeSpan.FromHours(8), TimeSpan.FromHours(9) }, block.StartsOfDay());

        var untilMidnight = new SlotBlockInput { StartLocal = TimeSpan.FromHours(22), Duration = TimeSpan.FromMinutes(5), RepeatEvery = TimeSpan.FromMinutes(30), RepeatUntil = null };
        Assert.Equal(new[] { new TimeSpan(22, 0, 0), new TimeSpan(22, 30, 0), new TimeSpan(23, 0, 0), new TimeSpan(23, 30, 0) }, untilMidnight.StartsOfDay());

        // A limit at or before the start means "until midnight" too.
        var wrapped = new SlotBlockInput { StartLocal = TimeSpan.FromHours(23), Duration = TimeSpan.FromMinutes(5), RepeatEvery = TimeSpan.FromMinutes(20), RepeatUntil = TimeSpan.FromHours(2) };
        Assert.Equal(3, wrapped.StartsOfDay().Count());

        Assert.Single(new SlotBlockInput { StartLocal = TimeSpan.FromHours(6) }.StartsOfDay());
    }

    [Fact]
    public void Plan_PlacesNoWeather_NextToAWeatherBlock()
    {
        var pieces = BumperFill.Plan(TimeSpan.FromMinutes(3).Ticks, WeatherClips(2, first: false, Clips()), 11UL, weatherAdjacent: true);
        Assert.DoesNotContain(pieces, p => p.IsWeather);
        Assert.True(pieces[^1].IsCard);

        // In a laid-out week: the gap before a weather block gets clips and a card, the gap after a program gets the weather.
        var ident = Clips()[0];
        var clips = new BumperFill.Clips(new[] { ident }, Array.Empty<PlayableItem>(), Array.Empty<PlayableItem>(), TimeSpan.FromMinutes(2), false, "W");
        var t = Items.Anchor;
        ScheduleEntry Slate(DateTime s, DateTime e) => new(Items.StationA, 1, EntryKind.Slate, null, s, e, false, null);
        var entries = new List<ScheduleEntry>
        {
            new(Items.StationA, 1, EntryKind.Program, Items.Movie("A", 20), t, t.AddMinutes(20), false, null),
            Slate(t.AddMinutes(20), t.AddMinutes(25)),
            new(Items.StationA, 1, EntryKind.Weather, null, t.AddMinutes(25), t.AddMinutes(30), false, "Block"),
            Slate(t.AddMinutes(30), t.AddMinutes(35)),
            new(Items.StationA, 1, EntryKind.Program, Items.Movie("B", 20), t.AddMinutes(35), t.AddMinutes(55), false, null),
            Slate(t.AddMinutes(55), t.AddMinutes(60))
        };
        var expanded = BumperFill.Expand(entries, clips, e => 5UL);
        var breaks = expanded.Where(e => e.Kind == EntryKind.Weather && e.InBreak).ToList();
        Assert.Single(breaks);
        Assert.Equal(t.AddMinutes(58), breaks[0].StartUtc);
        Assert.Equal(3, expanded.Count(e => e.Kind == EntryKind.Bumper)); // the ident in every gap
        Assert.DoesNotContain(expanded, e => e.InBreak && (e.EndUtc == t.AddMinutes(25) || e.StartUtc == t.AddMinutes(30)));
    }

    // ---- Pool with weather in the breaks -------------------------------------------------------

    [Fact]
    public void Pool_WeatherFillsTheBreaks_AsBumperEntries_ResolvableAndContiguous()
    {
        var pool = new List<PlayableItem> { Items.Movie("A", 47), Items.Movie("B", 61), Items.Movie("C", 22) };
        var station = Items.PoolStation(pool, PlayOrder.Sequential, grid: 15);
        station.Weather = Places();
        station.Bumpers = new BumperConfig { Enabled = true, BreakMinutes = 0, Weather = new WeatherBreakConfig { Enabled = true, Minutes = 2 } };
        var input = ScheduleInput.From(station, new CompiledStation { StationId = station.Id, Revision = 1, Pool = pool, Bumpers = Clips() }, station.Weather);

        var entries = ScheduleResolver.EnumerateInput(input, Items.Anchor, Items.Anchor.AddHours(6));
        for (var i = 1; i < entries.Count; i++)
        {
            Assert.Equal(entries[i - 1].EndUtc, entries[i].StartUtc);
        }

        var weather = entries.Where(e => e.Kind == EntryKind.Weather).ToList();
        Assert.NotEmpty(weather);
        Assert.All(weather, e => Assert.True(e.InBreak));
        Assert.All(weather, e => Assert.Equal("Weather now: Boston", e.Title));
        Assert.All(weather, e => Assert.True(e.Duration <= TimeSpan.FromMinutes(2) && e.Duration >= TimeSpan.FromSeconds(BumperFill.MinWeatherSeconds)));

        // A (47 min) on a 15-minute grid leaves a 13-minute gap: clips, a card and 2 minutes of weather right before B.
        var b = entries.First(e => e.Kind == EntryKind.Program && e.Item!.Title == "B");
        var before = entries[entries.ToList().IndexOf(b) - 1];
        Assert.Equal(EntryKind.Weather, before.Kind);
        Assert.Equal(TimeSpan.FromMinutes(2), before.Duration);

        var rng = new Random(9);
        for (var n = 0; n < 200; n++)
        {
            var t = Items.Anchor.AddSeconds(rng.NextDouble() * TimeSpan.FromHours(6).TotalSeconds);
            Assert.Equal(entries.Single(e => e.Contains(t)), ScheduleResolver.ResolveInput(input, t)!.Entry);
        }
    }

    [Fact]
    public void Pool_WeatherAloneInTheBreaks_WorksWithoutAnyClip()
    {
        var pool = new List<PlayableItem> { Items.Movie("A", 50), Items.Movie("B", 55) };
        var station = Items.PoolStation(pool, PlayOrder.Sequential, grid: 0);
        station.Weather = Places(2);
        station.Bumpers = new BumperConfig { Enabled = true, BreakMinutes = 3, Weather = new WeatherBreakConfig { Enabled = true, Minutes = 5, Position = BreakPosition.AfterProgram } };
        var input = ScheduleInput.From(station, new CompiledStation { StationId = station.Id, Revision = 1, Pool = pool }, station.Weather);
        Assert.True(input.HasBumpers);

        var entries = ScheduleResolver.EnumerateInput(input, Items.Anchor, Items.Anchor.AddHours(4));
        Assert.Equal(EntryKind.Program, entries[0].Kind);
        Assert.Equal(EntryKind.Weather, entries[1].Kind);
        Assert.Equal(Items.Anchor.AddMinutes(50), entries[1].StartUtc);
        Assert.Equal(TimeSpan.FromMinutes(3), entries[1].Duration); // the whole 3-minute break, under the 5-minute cap
        Assert.Equal("Weather now: Boston, Denver", entries[1].Title);
        Assert.Equal(EntryKind.Program, entries[2].Kind);
        Assert.DoesNotContain(entries, e => e.Kind == EntryKind.Slate);
    }

    // ---- Weather time block --------------------------------------------------------------------

    private static ScheduleInput SlotsWithWeatherBlock(string name, int minutes = 5, int repeatEvery = 0, string? until = null, int? screenOverride = null)
    {
        var block = new SlotBlock
        {
            Id = BlockId,
            Name = name,
            Kind = BlockKind.Weather,
            Days = new List<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday },
            StartLocal = "06:00",
            DurationMinutes = minutes,
            RepeatEveryMinutes = repeatEvery,
            RepeatUntilLocal = until
        };
        var station = new Station
        {
            Id = Items.StationA,
            Name = "Slots",
            ChannelNumber = "101",
            AnchorUtc = Items.Anchor,
            Revision = 1,
            Mode = ScheduleMode.Slots,
            TimeZoneId = "America/New_York",
            Slots = new SlotsConfig { Blocks = new List<SlotBlock> { block }, Filler = new FillerConfig { Order = PlayOrder.Sequential } },
            Weather = Places()
        };
        var compiled = new CompiledStation { StationId = station.Id, Revision = 1, Filler = Items.SeriesRun("Cartoons", 30, 11) };
        return ScheduleInput.From(station, compiled, station.Weather);
    }

    [Fact]
    public void WeatherBlock_IsOneWeatherEntry_ForTheOccurrence_TitledFromTheBlock()
    {
        var input = SlotsWithWeatherBlock("Morning weather");
        Assert.True(input.Blocks[0].IsWeather);
        Assert.NotNull(input.Weather);

        var start = LocalToUtc(2026, 10, 12, 6, 0);
        var entries = ScheduleResolver.EnumerateInput(input, start.AddMinutes(-30), start.AddMinutes(30));
        var weather = Assert.Single(entries, e => e.Kind == EntryKind.Weather);
        Assert.Equal(start, weather.StartUtc);
        Assert.Equal(start.AddMinutes(5), weather.EndUtc);
        Assert.Equal("Morning weather", weather.Title);
        Assert.False(weather.InBreak); // a real guide entry
        Assert.Null(weather.Item);

        // Filler surrounds it without gaps, and the instant resolves to it.
        for (var i = 1; i < entries.Count; i++)
        {
            Assert.Equal(entries[i - 1].EndUtc, entries[i].StartUtc);
        }

        Assert.Equal(weather, ScheduleResolver.ResolveInput(input, start.AddMinutes(2))!.Entry);

        // No name: the weather title from the Weather section.
        var unnamed = SlotsWithWeatherBlock(string.Empty);
        Assert.Equal("Weather now: Boston", unnamed.Blocks[0].Name);
    }

    [Fact]
    public void WeatherBlock_RepeatsAtTheTopOfEveryHour_UntilTheLimit()
    {
        var input = SlotsWithWeatherBlock("Weather", minutes: 5, repeatEvery: 60, until: "09:00");
        var dayStart = LocalToUtc(2026, 10, 12, 0, 0);
        var entries = ScheduleResolver.EnumerateInput(input, dayStart, dayStart.AddDays(1));
        var weather = entries.Where(e => e.Kind == EntryKind.Weather).ToList();

        Assert.Equal(4, weather.Count);
        Assert.Equal(new[] { 6, 7, 8, 9 }, weather.Select(w => TimeZoneInfo.ConvertTimeFromUtc(w.StartUtc, NewYork).Hour));
        Assert.All(weather, w => Assert.Equal(TimeSpan.FromMinutes(5), w.Duration));

        // Tuesday too, Wednesday not.
        var tuesday = ScheduleResolver.EnumerateInput(input, dayStart.AddDays(1), dayStart.AddDays(2)).Count(e => e.Kind == EntryKind.Weather);
        var wednesday = ScheduleResolver.EnumerateInput(input, dayStart.AddDays(2), dayStart.AddDays(3)).Count(e => e.Kind == EntryKind.Weather);
        Assert.Equal(4, tuesday);
        Assert.Equal(0, wednesday);

        // Without a limit the repeats run to midnight: 06:00 .. 23:00 = 18 occurrences.
        var allDay = SlotsWithWeatherBlock("Weather", minutes: 5, repeatEvery: 60);
        Assert.Equal(18, ScheduleResolver.EnumerateInput(allDay, dayStart, dayStart.AddDays(1)).Count(e => e.Kind == EntryKind.Weather));
    }
}
