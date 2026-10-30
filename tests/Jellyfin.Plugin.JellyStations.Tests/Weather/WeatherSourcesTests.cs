using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Weather;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Weather;

/// <summary>
/// An ordinary station's weather block and breaks show a weather channel's places; the pollers fetch for weather
/// channels only, including a disabled one that an enabled station borrows from.
/// </summary>
public class WeatherSourcesTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    private static Station Channel(string name, string number, bool enabled = true)
    {
        var s = new Station { Id = Guid.NewGuid(), Name = name, ChannelNumber = number, Mode = ScheduleMode.Weather, Enabled = enabled };
        s.Weather.Locations.Add(new WeatherLocation { Name = name + " place", Latitude = 40, Longitude = -100 });
        return s;
    }

    private static Station Borrower(string name, Guid? source = null, bool enabled = true, bool breaks = true, bool block = false)
    {
        var s = new Station { Id = Guid.NewGuid(), Name = name, ChannelNumber = "9", Mode = ScheduleMode.Pool, Enabled = enabled, WeatherSourceStationId = source };
        if (breaks)
        {
            s.Bumpers = new BumperConfig { Enabled = true, Weather = new WeatherBreakConfig { Enabled = true } };
        }

        if (block)
        {
            s.Slots.Blocks.Add(new SlotBlock { Kind = BlockKind.Weather, Days = new List<DayOfWeek> { DayOfWeek.Monday } });
        }

        return s;
    }

    [Fact]
    public void ChannelFor_AWeatherChannelIsItsOwnSource()
    {
        var channel = Channel("Weather", "1");
        var all = new List<Station> { channel, Borrower("Movies") };
        Assert.Same(channel, WeatherSources.ChannelFor(channel, all));
    }

    [Fact]
    public void ChannelFor_AnOrdinaryStation_TakesTheNamedChannel_ElseTheFirst_ElseNone()
    {
        var first = Channel("Weather A", "1");
        var second = Channel("Weather B", "2");
        var all = new List<Station> { first, second };

        Assert.Same(second, WeatherSources.ChannelFor(Borrower("Named", second.Id), all));
        Assert.Same(first, WeatherSources.ChannelFor(Borrower("Unnamed"), all));
        Assert.Same(first, WeatherSources.ChannelFor(Borrower("Dangling", Guid.NewGuid()), all)); // a deleted channel falls back
        Assert.Same(first, WeatherSources.ChannelFor(Borrower("Block only", breaks: false, block: true), all));

        // A station that puts no weather on the air has no source, whatever it names.
        Assert.Null(WeatherSources.ChannelFor(Borrower("Plain", first.Id, breaks: false), all));

        // No weather channel at all.
        Assert.Null(WeatherSources.ChannelFor(Borrower("Alone"), new List<Station> { Borrower("Other") }));
        Assert.Null(WeatherSources.ConfigFor(Borrower("Alone"), new List<Station>()));
    }

    [Fact]
    public void ChannelFor_NeverPicksAnOrdinaryStation_EvenWhenNamed()
    {
        var pool = Borrower("Pool");
        var all = new List<Station> { pool, Borrower("Named", pool.Id) };
        Assert.Null(WeatherSources.ChannelFor(all[1], all));
    }

    [Fact]
    public void InUse_FetchesForChannelsOnTheAir_AndForDisabledChannelsAnEnabledStationBorrows()
    {
        var onAir = Channel("On", "1");
        var off = Channel("Off", "2", enabled: false);
        var offButBorrowed = Channel("Borrowed", "3", enabled: false);
        var offSeason = Channel("Season", "4");
        offSeason.ActiveWindows.Add(new SeasonWindow { StartMonth = 12, StartDay = 1, EndMonth = 12, EndDay = 31 });
        var all = new List<Station>
        {
            onAir,
            off,
            offButBorrowed,
            offSeason,
            Borrower("Movies", offButBorrowed.Id),
            Borrower("Disabled borrower", off.Id, enabled: false)
        };

        var inUse = WeatherSources.InUse(all, Now).Select(s => s.Name).ToList();
        Assert.Equal(new[] { "On", "Borrowed" }, inUse);
        Assert.True(SeasonWindows.IsActive(onAir, Now));
        Assert.False(SeasonWindows.IsActive(offSeason, Now));
    }

    [Fact]
    public void Borrowers_ListsTheStationsThatAirTheChannelsWeather()
    {
        var a = Channel("A", "1");
        var b = Channel("B", "2");
        var named = Borrower("Named B", b.Id);
        var unnamed = Borrower("Unnamed");
        var plain = Borrower("Plain", breaks: false);
        var all = new List<Station> { a, b, named, unnamed, plain };

        Assert.Equal(new[] { "Unnamed" }, WeatherSources.Borrowers(a, all).Select(s => s.Name));
        Assert.Equal(new[] { "Named B" }, WeatherSources.Borrowers(b, all).Select(s => s.Name));
    }

    [Fact]
    public void ScheduleInput_UsesTheChannelsWeather_ForAnOrdinaryStation()
    {
        var channel = Channel("Weather", "1");
        channel.Weather.GuideTitle = "Weather now: {places}";
        var station = Borrower("Movies", channel.Id);
        station.Bumpers.Weather.Minutes = 3;
        var compiled = new CompiledStation { StationId = station.Id, Revision = 1 };

        // Nothing resolved: no weather in the breaks, no settings.
        var none = ScheduleInput.From(station, compiled);
        Assert.Null(none.Weather);
        Assert.Equal(TimeSpan.Zero, none.WeatherBreak);

        var input = ScheduleInput.From(station, compiled, WeatherSources.ConfigFor(station, new List<Station> { channel, station }));
        Assert.Same(channel.Weather, input.Weather);
        Assert.Equal(TimeSpan.FromMinutes(3), input.WeatherBreak);
        Assert.Equal("Weather now: Weather place", input.WeatherLabel);
        Assert.Empty(input.Music); // the channel's music plays, from the channel's compile
    }
}
