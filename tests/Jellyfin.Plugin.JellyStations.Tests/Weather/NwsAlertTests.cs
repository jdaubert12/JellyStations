using Jellyfin.Plugin.JellyStations.Weather;
using Jellyfin.Plugin.JellyStations.Weather.Alerts;
using Jellyfin.Plugin.JellyStations.Weather.Screens;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Weather;

/// <summary>The National Weather Service alerts source: the request, the parse of a recorded reply, and what counts as in force.</summary>
public class NwsAlertTests
{
    // The fixture was recorded on 2026-10-27 and has a Winter Storm Warning (Severe), a Flood Warning (Severe), a High Wind
    // Watch (Severe), a Dense Fog Advisory (Moderate), a Special Weather Statement (Moderate, no "ends"), plus a Cancel
    // message and a Test message that must be dropped.
    private static readonly DateTime Now = new(2026, 10, 27, 18, 30, 0, DateTimeKind.Utc);

    private static IReadOnlyList<WeatherAlert> Alerts() => NwsAlertSource.Parse(OpenMeteoParserTests.Fixture("nws-alerts.json"));

    [Fact]
    public void Url_PutsThePointInTheQuery_WithFourDecimals()
    {
        Assert.Equal("https://api.weather.gov/alerts/active?point=42.3601,-71.0589", NwsAlertSource.BuildUrl(42.36012, -71.05888));
        Assert.Equal("https://api.weather.gov/alerts/active?point=-14.3,-170.7", NwsAlertSource.BuildUrl(-14.3, -170.7));
    }

    [Theory]
    [InlineData(42.36, -71.06, true)]   // Boston
    [InlineData(64.84, -147.72, true)]  // Fairbanks
    [InlineData(21.3, -157.86, true)]   // Honolulu
    [InlineData(18.47, -66.1, true)]    // San Juan
    [InlineData(13.47, 144.75, true)]   // Guam
    [InlineData(51.5, -0.12, false)]    // London
    [InlineData(35.68, 139.69, false)]  // Tokyo
    [InlineData(-33.87, 151.21, false)] // Sydney
    public void Covers_TheStatesAndTerritories(double lat, double lon, bool expected)
    {
        var source = new NwsAlertSource(new NoHttp(), Microsoft.Extensions.Logging.Abstractions.NullLogger<NwsAlertSource>.Instance);
        Assert.Equal(expected, source.Covers(lat, lon));
    }

    [Fact]
    public void Parse_KeepsRealAlerts_DropsTestsAndCancellations_StrongestFirst()
    {
        var alerts = Alerts();

        Assert.Equal(5, alerts.Count);
        Assert.DoesNotContain(alerts, a => a.Event == "Tornado Warning"); // status Test
        Assert.DoesNotContain(alerts, a => a.Event == "Wind Advisory");   // messageType Cancel
        Assert.All(alerts.Take(3), a => Assert.Equal("Severe", a.Severity));
        Assert.Equal("Flood Warning", alerts[0].Event);        // Severe + Warning, ends soonest
        Assert.Equal("Winter Storm Warning", alerts[1].Event); // Severe + Warning
        Assert.Equal("High Wind Watch", alerts[2].Event);      // Severe + Watch
        Assert.Equal("Moderate", alerts[3].Severity);
        Assert.Equal("Moderate", alerts[4].Severity);
        Assert.Equal("Dense Fog Advisory", alerts[3].Event);   // Advisory outranks a Statement
    }

    [Fact]
    public void Parse_ReadsTimesAsUtc_AndTheWords()
    {
        var storm = Alerts().Single(a => a.Event == "Winter Storm Warning");

        Assert.Equal(new DateTime(2026, 10, 27, 18, 0, 0, DateTimeKind.Utc), storm.StartUtc);  // 10:00 AKDT (UTC-8)
        Assert.Equal(new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc), storm.EndUtc);     // 29th 16:00 AKDT
        Assert.Equal(new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc), storm.ExpiresUtc); // 27th 16:00 AKDT
        Assert.Equal(new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc), storm.SentUtc);
        Assert.Equal("NWS Fairbanks AK", storm.Sender);
        Assert.StartsWith("Winter Storm Warning issued October 27", storm.Headline, StringComparison.Ordinal);
        Assert.Contains("Noatak Valley", storm.Area, StringComparison.Ordinal);
        Assert.StartsWith("urn:oid:", storm.Id, StringComparison.Ordinal);
        Assert.Equal(4 - 1, storm.SeverityRank);
    }

    [Fact]
    public void Parse_UnwrapsTheText_AndTurnsBulletsIntoHeadings()
    {
        var storm = Alerts().Single(a => a.Event == "Winter Storm Warning");
        var paragraphs = storm.Description!.Split('\n');

        Assert.StartsWith("WHAT: Heavy snow expected. Total snow accumulations between 6 and 18 inches.", paragraphs[0], StringComparison.Ordinal);
        Assert.StartsWith("WHERE: ", paragraphs[1], StringComparison.Ordinal);
        Assert.StartsWith("WHEN: ", paragraphs[2], StringComparison.Ordinal);
        Assert.DoesNotContain("\n6 and", storm.Description, StringComparison.Ordinal);
        Assert.Equal("If you must travel, keep an extra flashlight, food, and water in your vehicle in case of an emergency.", storm.Instruction);
    }

    [Fact]
    public void Tidy_HandlesEmptyAndPlainText()
    {
        Assert.Null(NwsAlertSource.Tidy(null));
        Assert.Null(NwsAlertSource.Tidy("  \n "));
        Assert.Equal("One line.", NwsAlertSource.Tidy("One\nline."));
        Assert.Equal("First.\nSecond.", NwsAlertSource.Tidy("First.\n\n\nSecond."));
        Assert.Equal("IMPACTS: Travel could be very difficult.", NwsAlertSource.Tidy("* IMPACTS...Travel could be\nvery difficult."));
    }

    [Fact]
    public void IsActive_EndsWhenTheHazardOrTheMessageDoes()
    {
        var a = new WeatherAlert { EndUtc = Now.AddHours(2), ExpiresUtc = Now.AddHours(1) };
        Assert.True(a.IsActive(Now));
        Assert.False(a.IsActive(Now.AddMinutes(61)));   // the message lapsed; a continuing hazard gets a new one

        var noEnd = new WeatherAlert { ExpiresUtc = Now.AddHours(3) };
        Assert.True(noEnd.IsActive(Now));
        Assert.False(noEnd.IsActive(Now.AddHours(3)));

        var future = new WeatherAlert { StartUtc = Now.AddDays(2), EndUtc = Now.AddDays(3), ExpiresUtc = Now.AddHours(6) };
        Assert.True(future.IsActive(Now)); // a watch for the day after tomorrow is news today

        Assert.True(new WeatherAlert().IsActive(Now));
    }

    [Fact]
    public void AlertSet_Active_FiltersAndKeepsOrder()
    {
        var set = new AlertSet { Alerts = Alerts().ToList(), FetchedUtc = Now };
        // At 20:30 UTC on the 27th the Flood Warning (ends 20:00 UTC) and the Dense Fog Advisory (20:00 UTC) are over.
        var later = new DateTime(2026, 10, 27, 20, 30, 0, DateTimeKind.Utc);
        var active = set.Active(later);

        Assert.Equal(new[] { "Winter Storm Warning", "Special Weather Statement" }, active.Select(a => a.Event).ToArray());
        Assert.Equal(5, set.Active(Now.AddHours(-1)).Count);
    }

    [Fact]
    public void Summary_NamesTheStrongestAlert_WhenItEnds_AndTheRest()
    {
        var alerts = Alerts();
        // Alaska daylight time, UTC-8; the Flood Warning ends 20:00 UTC today = 12:00 PM.
        Assert.Equal("Flood Warning until 12:00 PM · 4 more", WeatherPainter.Summary(alerts, Now, -8 * 3600, false));
        Assert.Equal("Winter Storm Warning until Thu 16:00", WeatherPainter.Summary(new[] { alerts[1] }, Now, -8 * 3600, true));
        Assert.Equal(string.Empty, WeatherPainter.Summary(Array.Empty<WeatherAlert>(), Now, 0, false));
    }

    [Fact]
    public void KindRank_WarningsOverWatchesOverAdvisories()
    {
        Assert.True(NwsAlertSource.KindRank("Tornado Warning") > NwsAlertSource.KindRank("Tornado Watch"));
        Assert.True(NwsAlertSource.KindRank("Wind Advisory") > NwsAlertSource.KindRank("Special Weather Statement"));
        Assert.True(NwsAlertSource.KindRank("Flood Watch") > NwsAlertSource.KindRank("Wind Advisory"));
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("no network in tests");
    }
}
