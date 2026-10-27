using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Weather;

public class WeatherCycleTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static WeatherConfig Config(int screenSeconds = 10, bool eights = false, bool hourly = true, bool daily = true, bool travel = true, bool radar = false, bool forecastRadar = false, bool titleCards = false, int titleCardSeconds = 3, bool alerts = true)
        => new() { ScreenSeconds = screenSeconds, OnTheEights = eights, ShowHourly = hourly, ShowDaily = daily, ShowTravelCities = travel, ShowRadar = radar, ShowForecastRadar = forecastRadar, ShowTitleCards = titleCards, TitleCardSeconds = titleCardSeconds, ShowAlerts = alerts };

    [Fact]
    public void Pass_ShowsEveryScreenOfEveryPlace_ThenTravel()
    {
        var pass = WeatherCycle.Pass(Config(), 3);

        Assert.Equal(10, pass.Count);
        Assert.Equal(new CycleScreen(0, WeatherScreenKind.Current), pass[0]);
        Assert.Equal(new CycleScreen(0, WeatherScreenKind.Hourly), pass[1]);
        Assert.Equal(new CycleScreen(0, WeatherScreenKind.Daily), pass[2]);
        Assert.Equal(new CycleScreen(2, WeatherScreenKind.Daily), pass[8]);
        Assert.Equal(new CycleScreen(-1, WeatherScreenKind.Travel), pass[9]);
        Assert.Equal(TimeSpan.FromSeconds(100), WeatherCycle.PassLength(Config(), 3));
    }

    [Fact]
    public void Pass_OnePlace_HasNoTravelScreen()
    {
        var pass = WeatherCycle.Pass(Config(), 1);
        Assert.Equal(3, pass.Count);
        Assert.DoesNotContain(pass, s => s.Kind == WeatherScreenKind.Travel);
    }

    [Fact]
    public void Pass_RespectsScreenToggles()
    {
        var pass = WeatherCycle.Pass(Config(hourly: false, daily: false, travel: false), 2);
        Assert.Equal(2, pass.Count);
        Assert.All(pass, s => Assert.Equal(WeatherScreenKind.Current, s.Kind));
    }

    [Fact]
    public void Pass_TitleCardOpensEachPlace_AndAlertsFollowIt_OnlyWherePresent()
    {
        var available = new CycleAvailability(HasAlerts: i => i == 1);
        var pass = WeatherCycle.Pass(Config(titleCards: true), 2, available);

        Assert.Equal(new[]
        {
            new CycleScreen(0, WeatherScreenKind.TitleCard), new CycleScreen(0, WeatherScreenKind.Current), new CycleScreen(0, WeatherScreenKind.Hourly), new CycleScreen(0, WeatherScreenKind.Daily),
            new CycleScreen(1, WeatherScreenKind.TitleCard), new CycleScreen(1, WeatherScreenKind.Alerts), new CycleScreen(1, WeatherScreenKind.Current), new CycleScreen(1, WeatherScreenKind.Hourly), new CycleScreen(1, WeatherScreenKind.Daily),
            new CycleScreen(-1, WeatherScreenKind.Travel)
        }, pass);

        // Alerts default to "none" when nothing says otherwise, radar to "all".
        Assert.DoesNotContain(WeatherCycle.Pass(Config(), 2), s => s.Kind == WeatherScreenKind.Alerts);
        Assert.DoesNotContain(WeatherCycle.Pass(Config(alerts: false), 2, available), s => s.Kind == WeatherScreenKind.Alerts);
        Assert.Contains(WeatherCycle.Pass(Config(radar: true), 1), s => s.Kind == WeatherScreenKind.Radar);
    }

    [Fact]
    public void ShortPass_CarriesAlerts_ButNoTitleCards()
    {
        var available = new CycleAvailability(HasAlerts: i => i == 0);
        var pass = WeatherCycle.ShortPass(Config(titleCards: true), 2, available);
        Assert.Equal(new[] { new CycleScreen(0, WeatherScreenKind.Alerts), new CycleScreen(0, WeatherScreenKind.Current), new CycleScreen(1, WeatherScreenKind.Current), new CycleScreen(-1, WeatherScreenKind.Travel) }, pass);
    }

    [Fact]
    public void TitleCard_HasItsOwnShorterLength_AndThePassAddsUp()
    {
        var c = Config(titleCards: true, titleCardSeconds: 3);
        Assert.Equal(TimeSpan.FromSeconds(3), WeatherCycle.ScreenLength(c, WeatherScreenKind.TitleCard));
        Assert.Equal(TimeSpan.FromSeconds(10), WeatherCycle.ScreenLength(c, WeatherScreenKind.Alerts));
        // Two places: (3 + 10 + 10 + 10) × 2 + travel 10 = 76 s.
        Assert.Equal(TimeSpan.FromSeconds(76), WeatherCycle.PassLength(c, 2));
        Assert.Equal(TimeSpan.FromSeconds(1), WeatherCycle.ScreenLength(Config(titleCardSeconds: 0), WeatherScreenKind.TitleCard));
    }

    [Fact]
    public void Timeline_WithTitleCards_TilesTheWindow_AndRepeatsAtThePassLength()
    {
        var start = new DateTime(2026, 10, 27, 14, 0, 0, DateTimeKind.Utc);
        var c = Config(titleCards: true, titleCardSeconds: 3);
        var screens = WeatherCycle.Timeline(c, 1, start, start.AddSeconds(66), start, TimeZoneInfo.Utc);

        // One place, no travel: 3 + 10 + 10 + 10 = 33 s per pass, so two whole passes in 66 s.
        Assert.Equal(8, screens.Count);
        Assert.Equal(WeatherScreenKind.TitleCard, screens[0].Screen.Kind);
        Assert.Equal(TimeSpan.FromSeconds(3), screens[0].Duration);
        Assert.Equal(TimeSpan.FromSeconds(10), screens[1].Duration);
        Assert.True(screens[0].StartsPass);
        Assert.True(screens[4].StartsPass);
        Assert.Equal(start.AddSeconds(33), screens[4].StartUtc);
        Assert.Equal(WeatherScreenKind.TitleCard, screens[4].Screen.Kind);
        for (var i = 1; i < screens.Count; i++)
        {
            Assert.Equal(screens[i - 1].EndUtc, screens[i].StartUtc);
        }

        // Tuning in during the second pass's title card lands on its tail.
        var late = WeatherCycle.Timeline(c, 1, start.AddSeconds(34), start.AddSeconds(40), start, TimeZoneInfo.Utc);
        Assert.Equal(WeatherScreenKind.TitleCard, late[0].Screen.Kind);
        Assert.Equal(TimeSpan.FromSeconds(2), late[0].Duration);
        Assert.False(late[0].StartsPass);
    }

    [Fact]
    public void Timeline_OnTheEights_FillsTheWindowByLength_WithTitleCards()
    {
        var eight = new DateTime(2026, 10, 27, 14, 8, 0, DateTimeKind.Utc);
        var available = new CycleAvailability(HasAlerts: i => i == 0);
        var screens = WeatherCycle.Timeline(Config(eights: true, titleCards: true, titleCardSeconds: 4), 2, eight, eight.AddMinutes(10), eight.AddHours(-1), TimeZoneInfo.Utc, available);

        // Full pass: (4 + 10 + 10 + 10 + 10) + (4 + 10 + 10 + 10) + 10 = 88 s over ten screens; then short passes of 40 s (alerts, current, current, travel).
        Assert.Equal(eight, screens[0].StartUtc);
        Assert.Equal(eight.AddMinutes(10), screens[^1].EndUtc);
        Assert.Equal(WeatherScreenKind.TitleCard, screens[0].Screen.Kind);
        Assert.Equal(WeatherScreenKind.Alerts, screens[1].Screen.Kind);
        Assert.Equal(eight.AddSeconds(88), screens[10].StartUtc);
        Assert.Equal(new CycleScreen(0, WeatherScreenKind.Alerts), screens[10].Screen);
        Assert.Equal(new CycleScreen(0, WeatherScreenKind.Current), screens[11].Screen);
        Assert.DoesNotContain(screens.Skip(10), s => s.Kind() == WeatherScreenKind.TitleCard);
        for (var i = 1; i < screens.Count; i++)
        {
            Assert.Equal(screens[i - 1].EndUtc, screens[i].StartUtc);
        }
    }

    [Fact]
    public void Timeline_TilesTheWindow_WithNoGaps()
    {
        var start = new DateTime(2026, 10, 23, 14, 0, 0, DateTimeKind.Utc);
        var from = start.AddSeconds(37); // tuned in mid-screen
        var to = from.AddMinutes(5);

        var screens = WeatherCycle.Timeline(Config(), 2, from, to, start, TimeZoneInfo.Utc);

        Assert.Equal(from, screens[0].StartUtc);
        Assert.Equal(to, screens[^1].EndUtc);
        for (var i = 1; i < screens.Count; i++)
        {
            Assert.Equal(screens[i - 1].EndUtc, screens[i].StartUtc);
        }

        // Screens are 10 s on the clock counted from the cycle start: the first is the 3 s tail of the fourth screen.
        Assert.Equal(TimeSpan.FromSeconds(3), screens[0].Duration);
        Assert.Equal(new CycleScreen(1, WeatherScreenKind.Current), screens[0].Screen);
        Assert.All(screens.Skip(1).Take(screens.Count - 2), s => Assert.Equal(TimeSpan.FromSeconds(10), s.Duration));
    }

    [Fact]
    public void Timeline_IsAFunctionOfTheClock()
    {
        var start = new DateTime(2026, 10, 23, 14, 0, 0, DateTimeKind.Utc);
        var a = WeatherCycle.Timeline(Config(), 3, start.AddMinutes(1), start.AddMinutes(3), start, TimeZoneInfo.Utc);
        var b = WeatherCycle.Timeline(Config(), 3, start.AddMinutes(2), start.AddMinutes(3), start, TimeZoneInfo.Utc);

        // Whatever b shows at 2:00 is what a shows at 2:00.
        var atTwo = start.AddMinutes(2);
        var fromA = a.Single(s => s.StartUtc <= atTwo && s.EndUtc > atTwo);
        Assert.Equal(fromA.Screen, b[0].Screen);
        Assert.Equal(fromA.EndUtc, b[0].EndUtc);
    }

    [Fact]
    public void Timeline_PassesRepeatBackToBack()
    {
        var start = new DateTime(2026, 10, 23, 14, 0, 0, DateTimeKind.Utc);
        var screens = WeatherCycle.Timeline(Config(), 1, start, start.AddSeconds(60), start, TimeZoneInfo.Utc);
        Assert.Equal(6, screens.Count);
        Assert.Equal(WeatherScreenKind.Current, screens[0].Screen.Kind);
        Assert.Equal(WeatherScreenKind.Daily, screens[2].Screen.Kind);
        Assert.Equal(WeatherScreenKind.Current, screens[3].Screen.Kind);
        Assert.True(screens[0].StartsPass);
        Assert.True(screens[3].StartsPass);
        Assert.False(screens[1].StartsPass);
    }

    [Theory]
    [InlineData("2026-10-23T14:07:59Z", "2026-10-23T13:58:00Z")]
    [InlineData("2026-10-23T14:08:00Z", "2026-10-23T14:08:00Z")]
    [InlineData("2026-10-23T14:17:59Z", "2026-10-23T14:08:00Z")]
    [InlineData("2026-10-23T14:18:00Z", "2026-10-23T14:18:00Z")]
    [InlineData("2026-10-23T14:59:00Z", "2026-10-23T14:58:00Z")]
    [InlineData("2026-10-23T15:03:00Z", "2026-10-23T14:58:00Z")]
    public void WindowStart_FallsOnTheEights(string utc, string expected)
    {
        var at = DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        var want = DateTime.Parse(expected, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        Assert.Equal(want, WeatherCycle.WindowStart(at, TimeZoneInfo.Utc));
    }

    [Fact]
    public void WindowStart_UsesStationLocalMinutes()
    {
        // 14:08 UTC is 10:08 in New York (EDT): still on the 8s. But 14:38 UTC with a half-hour zone would not be;
        // for a whole-hour zone the minute hand is what matters.
        var at = new DateTime(2026, 10, 23, 14, 12, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 10, 23, 14, 8, 0, DateTimeKind.Utc), WeatherCycle.WindowStart(at, NewYork));

        var kolkata = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); // UTC+5:30
        var local = new DateTime(2026, 10, 23, 19, 50, 0); // 19:50 local = 14:20 UTC; window started at 19:48 local = 14:18 UTC
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, kolkata);
        Assert.Equal(new DateTime(2026, 10, 23, 14, 18, 0, DateTimeKind.Utc), WeatherCycle.WindowStart(utc, kolkata));
    }

    [Fact]
    public void Timeline_OnTheEights_StartsAFullPassAtEight_ThenRepeatsCurrentConditions()
    {
        var eight = new DateTime(2026, 10, 23, 14, 8, 0, DateTimeKind.Utc);
        var screens = WeatherCycle.Timeline(Config(eights: true), 2, eight, eight.AddMinutes(10), eight.AddHours(-1), TimeZoneInfo.Utc);

        // One full pass (7 screens × 10 s = 70 s), then the short pass (current, current, travel) until :18.
        Assert.Equal(60, screens.Count);
        Assert.Equal(eight, screens[0].StartUtc);
        Assert.Equal(eight.AddMinutes(10), screens[^1].EndUtc);
        Assert.Equal(WeatherScreenKind.Hourly, screens[1].Screen.Kind);
        Assert.Equal(WeatherScreenKind.Travel, screens[6].Screen.Kind);
        Assert.Equal(new CycleScreen(0, WeatherScreenKind.Current), screens[7].Screen);
        Assert.Equal(new CycleScreen(1, WeatherScreenKind.Current), screens[8].Screen);
        Assert.Equal(WeatherScreenKind.Travel, screens[9].Screen.Kind);
        Assert.DoesNotContain(screens.Skip(7), s => s.Kind() is WeatherScreenKind.Hourly or WeatherScreenKind.Daily);
        for (var i = 1; i < screens.Count; i++)
        {
            Assert.Equal(screens[i - 1].EndUtc, screens[i].StartUtc);
        }
    }

    [Fact]
    public void Timeline_OnTheEights_CrossesIntoTheNextWindow()
    {
        var from = new DateTime(2026, 10, 23, 14, 17, 55, DateTimeKind.Utc);
        var screens = WeatherCycle.Timeline(Config(eights: true), 1, from, from.AddSeconds(15), from, TimeZoneInfo.Utc);

        Assert.Equal(2, screens.Count);
        Assert.Equal(TimeSpan.FromSeconds(5), screens[0].Duration);
        Assert.Equal(new DateTime(2026, 10, 23, 14, 18, 0, DateTimeKind.Utc), screens[1].StartUtc);
        Assert.Equal(new CycleScreen(0, WeatherScreenKind.Current), screens[1].Screen);
        Assert.True(screens[1].StartsPass);
    }
}

internal static class TimedScreenExtensions
{
    public static WeatherScreenKind Kind(this TimedScreen s) => s.Screen.Kind;
}
