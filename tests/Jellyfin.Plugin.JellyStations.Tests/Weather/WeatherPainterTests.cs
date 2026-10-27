using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Weather;
using Jellyfin.Plugin.JellyStations.Weather.Alerts;
using Jellyfin.Plugin.JellyStations.Weather.Screens;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Weather;

/// <summary>Draws every screen kind from the recorded Boston reply; the PNGs land in the test output for a look.</summary>
public class WeatherPainterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "JellyStationsTests", "weather-" + Guid.NewGuid().ToString("N"));
    private readonly WeatherAssets _assets = new(Path.Combine(Path.GetTempPath(), "JellyStationsTests", "assets"));

    private static readonly DateTime Now = new(2026, 10, 23, 21, 40, 0, DateTimeKind.Utc);

    private static WeatherReport Boston()
    {
        var r = OpenMeteoProvider.Parse(OpenMeteoParserTests.Fixture("open-meteo-boston.json"), Now);
        r.Attribution = "Weather data by Open-Meteo.com";
        return r;
    }

    private static List<PlaceWeather> Places()
    {
        var boston = Boston();
        var denver = Boston();
        denver.UtcOffsetSeconds = -21600;
        denver.TimeZoneId = "America/Denver";
        denver.Current.TemperatureC = 24.5;
        denver.Current.WeatherCode = 95;
        denver.Current.IsDay = true;
        var london = Boston();
        london.UtcOffsetSeconds = 3600;
        london.Current.TemperatureC = -3;
        london.Current.WeatherCode = 73;
        london.Current.IsDay = false;
        var alerts = NwsAlertSource.Parse(OpenMeteoParserTests.Fixture("nws-alerts.json"));
        return new List<PlaceWeather>
        {
            new(new WeatherLocation { Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), Name = "Boston", Region = "Massachusetts, United States", Latitude = 42.36, Longitude = -71.06 }, boston, false, new[] { alerts[1] }, "Alerts: US National Weather Service"),
            new(new WeatherLocation { Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), Name = "Denver", Region = "Colorado, United States", Latitude = 39.74, Longitude = -104.99 }, denver, false, alerts, "Alerts: US National Weather Service"),
            new(new WeatherLocation { Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003"), Name = "London", Region = "England, United Kingdom", Latitude = 51.5, Longitude = -0.12 }, london, true),
            new(new WeatherLocation { Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004"), Name = "Nome", Region = "Alaska", Latitude = 64.5, Longitude = -165.4 }, null, true)
        };
    }

    private ScreenRequest Request(WeatherScreenKind kind, int place, WeatherTheme theme = WeatherTheme.Classic, WeatherUnits units = WeatherUnits.Imperial, int width = 1920)
    {
        var places = Places();
        var config = new WeatherConfig { Theme = theme, Units = units, ShowLogo = false };
        return new ScreenRequest(kind, kind == WeatherScreenKind.Travel ? null : places[place], places, config, Now, TimeZoneInfo.Utc, null, width, width * 9 / 16, "Weather data by Open-Meteo.com");
    }

    [Theory]
    [InlineData(WeatherScreenKind.Current, 0, WeatherTheme.Classic)]
    [InlineData(WeatherScreenKind.Hourly, 0, WeatherTheme.Classic)]
    [InlineData(WeatherScreenKind.Daily, 0, WeatherTheme.Classic)]
    [InlineData(WeatherScreenKind.Travel, 0, WeatherTheme.Classic)]
    [InlineData(WeatherScreenKind.Current, 1, WeatherTheme.Dark)]
    [InlineData(WeatherScreenKind.Current, 2, WeatherTheme.Light)]
    [InlineData(WeatherScreenKind.Daily, 2, WeatherTheme.Light)]
    [InlineData(WeatherScreenKind.Current, 3, WeatherTheme.Classic)]
    [InlineData(WeatherScreenKind.TitleCard, 0, WeatherTheme.Classic)]
    [InlineData(WeatherScreenKind.TitleCard, 2, WeatherTheme.Light)]
    [InlineData(WeatherScreenKind.TitleCard, 3, WeatherTheme.Dark)]
    [InlineData(WeatherScreenKind.Alerts, 0, WeatherTheme.Classic)]
    [InlineData(WeatherScreenKind.Alerts, 1, WeatherTheme.Dark)]
    [InlineData(WeatherScreenKind.Alerts, 1, WeatherTheme.Light)]
    [InlineData(WeatherScreenKind.Alerts, 2, WeatherTheme.Classic)]
    [InlineData(WeatherScreenKind.Hourly, 1, WeatherTheme.Classic)]
    [InlineData(WeatherScreenKind.Daily, 1, WeatherTheme.Classic)]
    public void Paints_EveryScreenKind(WeatherScreenKind kind, int place, WeatherTheme theme)
    {
        var painter = new WeatherPainter(_assets);
        var request = Request(kind, place, theme);
        var path = Path.Combine(_dir, $"{kind}-{place}-{theme}.png".ToLowerInvariant());

        painter.Paint(request, path);

        var info = new FileInfo(path);
        Assert.True(info.Exists);
        Assert.True(info.Length > 20_000, "a drawn screen is more than a flat colour: " + info.Length + " bytes");
        var header = File.ReadAllBytes(path).Take(8).ToArray();
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, header);
        Keep(path);
    }

    [Fact]
    public void FileName_ChangesWithTheAlerts()
    {
        var places = Places();
        var config = new WeatherConfig { ShowLogo = false };
        string Name(PlaceWeather p, WeatherScreenKind kind) => WeatherScreenRenderer.FileName(new ScreenRequest(kind, p, places, config, Now, TimeZoneInfo.Utc, null, 1920, 1080, "x"));

        var with = Name(places[1], WeatherScreenKind.Current);
        var without = Name(places[1] with { Alerts = null }, WeatherScreenKind.Current);
        var fewer = Name(places[1] with { Alerts = places[1].ActiveAlerts.Take(1).ToList() }, WeatherScreenKind.Current);
        Assert.NotEqual(with, without);
        Assert.NotEqual(with, fewer);
        Assert.Equal(with, Name(places[1], WeatherScreenKind.Current));
        Assert.StartsWith("aaaaaaaa-titlecard-", Name(places[0], WeatherScreenKind.TitleCard), StringComparison.Ordinal);
    }

    [Fact]
    public void Paints_At720p_Too()
    {
        var painter = new WeatherPainter(_assets);
        var path = Path.Combine(_dir, "current-720.png");
        painter.Paint(Request(WeatherScreenKind.Current, 0, width: 1280), path);
        Assert.True(File.Exists(path));
        Keep(path);
    }

    [Fact]
    public void FileName_ChangesWithReportAndHour_NotWithTheMinute()
    {
        var a = WeatherScreenRenderer.FileName(Request(WeatherScreenKind.Current, 0));
        var b = WeatherScreenRenderer.FileName(Request(WeatherScreenKind.Current, 0) with { NowUtc = Now.AddMinutes(5) });
        var c = WeatherScreenRenderer.FileName(Request(WeatherScreenKind.Current, 0) with { NowUtc = Now.AddHours(1) });
        var d = WeatherScreenRenderer.FileName(Request(WeatherScreenKind.Current, 1));
        var e = WeatherScreenRenderer.FileName(Request(WeatherScreenKind.Current, 0, WeatherTheme.Dark));

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
        Assert.NotEqual(a, e);
        Assert.EndsWith(".png", a);
        Assert.StartsWith("aaaaaaaa-current-", a);
    }

    [Fact]
    public void Fonts_UnpackForFfmpeg()
    {
        var path = _assets.FontFile(FontWeight.SemiBold);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 100_000);
        Assert.EndsWith("Inter-SemiBold.ttf", path);
    }

    /// <summary>Copies a rendered screen next to the test results so a person can look at it (JELLYSTATIONS_KEEP_SCREENS=dir).</summary>
    private static void Keep(string path)
    {
        var keep = Environment.GetEnvironmentVariable("JELLYSTATIONS_KEEP_SCREENS");
        if (string.IsNullOrEmpty(keep))
        {
            return;
        }

        Directory.CreateDirectory(keep);
        File.Copy(path, Path.Combine(keep, Path.GetFileName(path)), overwrite: true);
    }

    public void Dispose()
    {
        _assets.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception)
        {
            // Left for inspection.
        }
    }
}
