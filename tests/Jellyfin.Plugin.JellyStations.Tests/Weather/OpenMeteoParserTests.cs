using Jellyfin.Plugin.JellyStations.Weather;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Weather;

public class OpenMeteoParserTests
{
    public static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Weather", name));

    [Fact]
    public void Parse_ReadsCurrentHourlyAndDaily()
    {
        var fetched = new DateTime(2026, 10, 23, 21, 40, 0, DateTimeKind.Utc);
        var r = OpenMeteoProvider.Parse(Fixture("open-meteo-boston.json"), fetched);

        Assert.Equal("America/New_York", r.TimeZoneId);
        Assert.Equal(-14400, r.UtcOffsetSeconds);
        Assert.Equal(fetched, r.FetchedUtc);
        // 17:30 local at UTC-4 is 21:30 UTC.
        Assert.Equal(new DateTime(2026, 10, 23, 21, 30, 0, DateTimeKind.Utc), r.ObservedUtc);
        Assert.Equal(DateTimeKind.Utc, r.ObservedUtc.Kind);

        Assert.Equal(10.3, r.Current.TemperatureC, 3);
        Assert.Equal(54, r.Current.HumidityPercent);
        Assert.InRange(r.Current.WeatherCode, 0, 99);
        Assert.InRange(r.Current.WindDirectionDegrees, 0, 360);

        Assert.Equal(24, r.Hourly.Count);
        Assert.Equal(DateTimeKind.Unspecified, r.Hourly[0].Local.Kind);
        Assert.Equal(new DateTime(2026, 10, 23, 17, 0, 0), r.Hourly[0].Local);
        Assert.Equal(TimeSpan.FromHours(1), r.Hourly[1].Local - r.Hourly[0].Local);

        Assert.Equal(0.3, r.Hourly[11].RainMm, 3);
        Assert.Equal(PrecipitationKind.Rain, r.Hourly[11].PrecipitationKind);
        Assert.Equal(0.7, r.Hourly[12].SnowfallCm, 3);
        Assert.Equal(PrecipitationKind.Snow, r.Hourly[12].PrecipitationKind);

        Assert.Equal(7, r.Daily.Count);
        Assert.Equal(1.6, r.Daily[4].RainMm, 3);
        Assert.Equal(PrecipitationKind.Rain, r.Daily[4].PrecipitationKind);
        Assert.Equal(2.1, r.Daily[6].SnowfallCm, 3);
        Assert.Equal(PrecipitationKind.Snow, r.Daily[6].PrecipitationKind);
        Assert.Equal(new DateTime(2026, 10, 23), r.Daily[0].Date);
        Assert.True(r.Daily[0].HighC >= r.Daily[0].LowC);
        Assert.NotNull(r.Daily[0].Sunrise);
        Assert.NotNull(r.Daily[0].Sunset);
        Assert.True(r.Daily[0].Sunset > r.Daily[0].Sunrise);
    }

    [Fact]
    public void Parse_ErrorReply_Throws()
    {
        var ex = Assert.Throws<InvalidDataException>(() => OpenMeteoProvider.Parse("{\"error\":true,\"reason\":\"Latitude must be in range\"}", DateTime.UtcNow));
        Assert.Contains("Latitude", ex.Message);
    }

    [Fact]
    public void Parse_MissingSections_YieldsEmptyLists()
    {
        var r = OpenMeteoProvider.Parse("{\"utc_offset_seconds\":0,\"timezone\":\"UTC\"}", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Empty(r.Hourly);
        Assert.Empty(r.Daily);
        Assert.Equal(r.FetchedUtc, r.ObservedUtc);
    }

    [Fact]
    public void ParseGeocode_ReadsPlaces()
    {
        var places = OpenMeteoProvider.ParseGeocode(Fixture("open-meteo-geocode-boston.json"));

        Assert.NotEmpty(places);
        var boston = places[0];
        Assert.Equal("Boston", boston.Name);
        Assert.Equal("Massachusetts", boston.Region);
        Assert.Equal("United States", boston.Country);
        Assert.InRange(boston.Latitude, 42.0, 43.0);
        Assert.InRange(boston.Longitude, -72.0, -70.0);
        Assert.Equal("America/New_York", boston.TimeZoneId);
        Assert.True(boston.Population > 100000);
    }

    [Fact]
    public void ParseGeocode_NoResults_IsEmpty()
    {
        Assert.Empty(OpenMeteoProvider.ParseGeocode("{\"generationtime_ms\":0.1}"));
    }

    [Fact]
    public void ForecastUrl_SendsOnlyCoordinates()
    {
        var url = OpenMeteoProvider.BuildForecastUrl(42.3601, -71.0589);
        Assert.StartsWith("https://api.open-meteo.com/v1/forecast?latitude=42.3601&longitude=-71.0589&", url);
        Assert.Contains("timezone=auto", url);
        Assert.Contains("forecast_days=7", url);
        Assert.Contains("rain,showers,snowfall", url);
        Assert.Contains("rain_sum,showers_sum,snowfall_sum", url);
        Assert.DoesNotContain("key", url);
    }

    [Theory]
    [InlineData(0, "Clear", WeatherIcon.Clear)]
    [InlineData(2, "Partly cloudy", WeatherIcon.PartlyCloudy)]
    [InlineData(3, "Overcast", WeatherIcon.Cloudy)]
    [InlineData(61, "Light rain", WeatherIcon.Rain)]
    [InlineData(75, "Heavy snow", WeatherIcon.HeavySnow)]
    [InlineData(95, "Thunderstorm", WeatherIcon.Thunderstorm)]
    [InlineData(123, "Unknown", WeatherIcon.Unknown)]
    public void WmoCodes_MapToWordsAndIcons(int code, string words, WeatherIcon icon)
    {
        Assert.Equal(words, WmoWeather.Describe(code));
        Assert.Equal(icon, WmoWeather.Icon(code));
    }

    [Theory]
    [InlineData(73, 0, 0, 5, PrecipitationKind.Snow)]        // code says snow
    [InlineData(63, 0, 0, -5, PrecipitationKind.Rain)]       // code says rain even below freezing
    [InlineData(66, 0, 0, -1, PrecipitationKind.Ice)]        // freezing rain
    [InlineData(3, 0, 0, 8, PrecipitationKind.Rain)]         // cloudy, 20% chance: warm, so rain
    [InlineData(3, 0, 0, -3, PrecipitationKind.Snow)]        // cloudy, 20% chance: freezing, so snow
    [InlineData(3, 1.2, 0, 2, PrecipitationKind.Rain)]       // amounts beat the code
    [InlineData(61, 0, 0.5, 0, PrecipitationKind.Snow)]
    [InlineData(73, 0.8, 1.5, 0, PrecipitationKind.Mix)]     // both kinds forecast
    [InlineData(56, 0.3, 0, -1, PrecipitationKind.Ice)]      // rain amount under a freezing-drizzle code stays ice
    [InlineData(80, 0.05, 0.05, 1, PrecipitationKind.Rain)]  // traces below 0.1 do not count
    public void PrecipitationType_FromAmountsThenCodeThenTemperature(int code, double rainMm, double snowCm, double tempC, PrecipitationKind kind)
        => Assert.Equal(kind, WmoWeather.PrecipitationType(code, rainMm, snowCm, tempC));

    [Fact]
    public void PrecipitationWord_IsLowercaseAndShort()
    {
        Assert.Equal("rain", WmoWeather.PrecipitationWord(PrecipitationKind.Rain));
        Assert.Equal("snow", WmoWeather.PrecipitationWord(PrecipitationKind.Snow));
        Assert.Equal("mix", WmoWeather.PrecipitationWord(PrecipitationKind.Mix));
        Assert.Equal("ice", WmoWeather.PrecipitationWord(PrecipitationKind.Ice));
    }

    [Theory]
    [InlineData(0, "N")]
    [InlineData(45, "NE")]
    [InlineData(90, "E")]
    [InlineData(225, "SW")]
    [InlineData(350, "N")]
    [InlineData(337, "NNW")]
    public void Compass_Names16Points(int degrees, string name) => Assert.Equal(name, WmoWeather.Compass(degrees));

    [Fact]
    public void Units_ConvertAndFormat()
    {
        Assert.Equal("72°", Units.Temperature(22.2, Models.WeatherUnits.Imperial));
        Assert.Equal("22°", Units.Temperature(22.2, Models.WeatherUnits.Metric));
        Assert.Equal("12 mph", Units.Speed(19.3, Models.WeatherUnits.Imperial));
        Assert.Equal("19 km/h", Units.Speed(19.3, Models.WeatherUnits.Metric));
        Assert.Equal("30.12 in", Units.Pressure(1020, Models.WeatherUnits.Imperial));
        Assert.Equal("1020 hPa", Units.Pressure(1020, Models.WeatherUnits.Metric));
        Assert.Equal("2.5 in snow", Units.Snowfall(6.35, Models.WeatherUnits.Imperial));
        Assert.Equal("6 cm snow", Units.Snowfall(6.35, Models.WeatherUnits.Metric));
        Assert.Equal("0.4 cm snow", Units.Snowfall(0.4, Models.WeatherUnits.Metric));
        Assert.Equal("3:47 PM", Units.Clock(new DateTime(2026, 10, 23, 15, 47, 0), false));
        Assert.Equal("15:47", Units.Clock(new DateTime(2026, 10, 23, 15, 47, 0), true));
        Assert.Equal("Today", Units.DayName(new DateTime(2026, 10, 23), new DateTime(2026, 10, 23, 9, 0, 0)));
        Assert.Equal("Tomorrow", Units.DayName(new DateTime(2026, 10, 24), new DateTime(2026, 10, 23, 9, 0, 0)));
        Assert.Equal("Sunday", Units.DayName(new DateTime(2026, 10, 25), new DateTime(2026, 10, 23, 9, 0, 0)));
    }

    [Fact]
    public void DrawtextClockFormat_EscapesTheColonTwice()
    {
        // The filter parser eats one level of escaping, the text expansion the second, leaving ":" for strftime.
        Assert.Equal(@"%H\\\:%M", Units.DrawtextClockFormat(true));
        Assert.Equal(@"%-I\\\:%M %p", Units.DrawtextClockFormat(false));
    }
}
