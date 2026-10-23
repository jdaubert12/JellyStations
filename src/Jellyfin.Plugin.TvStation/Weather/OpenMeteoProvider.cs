using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.TvStation.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvStation.Weather;

/// <summary>
/// Open-Meteo (https://open-meteo.com): no account or key, global coverage, current conditions plus hourly and
/// daily forecasts in one call, and a geocoding endpoint. Free for non-commercial use under 10,000 calls a day;
/// the data is CC BY 4.0, which is why every screen carries <see cref="Attribution"/>.
/// </summary>
public sealed class OpenMeteoProvider : IWeatherProvider
{
    public const string ProviderId = "open-meteo";
    public const string ForecastUrl = "https://api.open-meteo.com/v1/forecast";
    public const string GeocodingUrl = "https://geocoding-api.open-meteo.com/v1/search";

    private const string CurrentFields = "temperature_2m,relative_humidity_2m,apparent_temperature,is_day,precipitation,weather_code,cloud_cover,pressure_msl,wind_speed_10m,wind_direction_10m,wind_gusts_10m";
    private const string HourlyFields = "temperature_2m,precipitation_probability,weather_code,is_day";
    private const string DailyFields = "weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,precipitation_sum,sunrise,sunset,wind_speed_10m_max";

    private readonly IHttpClientFactory _http;
    private readonly ILogger<OpenMeteoProvider> _logger;

    public OpenMeteoProvider(IHttpClientFactory http, ILogger<OpenMeteoProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string Id => ProviderId;

    public string Attribution => "Weather data by Open-Meteo.com";

    /// <summary>The request URL for a place: metric units, the place's own time zone, 7 days, the next 24 hours.</summary>
    public static string BuildForecastUrl(double latitude, double longitude)
    {
        var lat = latitude.ToString("0.####", CultureInfo.InvariantCulture);
        var lon = longitude.ToString("0.####", CultureInfo.InvariantCulture);
        return $"{ForecastUrl}?latitude={lat}&longitude={lon}&current={CurrentFields}&hourly={HourlyFields}&daily={DailyFields}&timezone=auto&forecast_days=7&forecast_hours=24";
    }

    public async Task<WeatherReport> FetchAsync(WeatherLocation location, CancellationToken ct)
    {
        var url = BuildForecastUrl(location.Latitude, location.Longitude);
        var json = await GetStringAsync(url, ct).ConfigureAwait(false);
        var report = Parse(json, DateTime.UtcNow);
        report.Provider = Id;
        report.Attribution = Attribution;
        return report;
    }

    public async Task<IReadOnlyList<GeocodeResult>> SearchAsync(string query, int count, CancellationToken ct)
    {
        var url = $"{GeocodingUrl}?name={Uri.EscapeDataString(query.Trim())}&count={Math.Clamp(count, 1, 20)}&language=en&format=json";
        var json = await GetStringAsync(url, ct).ConfigureAwait(false);
        return ParseGeocode(json);
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var client = _http.CreateClient("TvStation.Weather");
        client.Timeout = TimeSpan.FromSeconds(20);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Jellyfin-TvStation", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/jdaubert12/TvStation)"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var reason = TryReadReason(body) ?? response.ReasonPhrase ?? string.Empty;
            throw new HttpRequestException($"Open-Meteo answered {(int)response.StatusCode} {reason}".Trim(), null, response.StatusCode);
        }

        return body;
    }

    private static string? TryReadReason(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Turns a forecast response into a report. Pure, so it is tested against recorded replies.</summary>
    public static WeatherReport Parse(string json, DateTime fetchedUtc)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.True)
        {
            throw new InvalidDataException("Open-Meteo error: " + (root.TryGetProperty("reason", out var r) ? r.GetString() : "unknown"));
        }

        var report = new WeatherReport
        {
            FetchedUtc = fetchedUtc,
            TimeZoneId = root.TryGetProperty("timezone", out var tz) ? tz.GetString() : null,
            UtcOffsetSeconds = root.TryGetProperty("utc_offset_seconds", out var off) ? off.GetInt32() : 0
        };

        if (root.TryGetProperty("current", out var cur))
        {
            report.Current = new CurrentConditions
            {
                TemperatureC = Num(cur, "temperature_2m"),
                FeelsLikeC = cur.TryGetProperty("apparent_temperature", out _) ? Num(cur, "apparent_temperature") : Num(cur, "temperature_2m"),
                HumidityPercent = (int)Math.Round(Num(cur, "relative_humidity_2m")),
                WindKmh = Num(cur, "wind_speed_10m"),
                GustKmh = Num(cur, "wind_gusts_10m"),
                WindDirectionDegrees = (int)Math.Round(Num(cur, "wind_direction_10m")),
                PressureHpa = Num(cur, "pressure_msl"),
                PrecipitationMm = Num(cur, "precipitation"),
                CloudCoverPercent = (int)Math.Round(Num(cur, "cloud_cover")),
                WeatherCode = (int)Math.Round(Num(cur, "weather_code")),
                IsDay = !cur.TryGetProperty("is_day", out var isDay) || isDay.ValueKind != JsonValueKind.Number || isDay.GetInt32() != 0
            };
            var observedLocal = LocalTime(cur, "time");
            report.ObservedUtc = observedLocal.HasValue ? DateTime.SpecifyKind(observedLocal.Value.AddSeconds(-report.UtcOffsetSeconds), DateTimeKind.Utc) : fetchedUtc;
        }
        else
        {
            report.ObservedUtc = fetchedUtc;
        }

        if (root.TryGetProperty("hourly", out var hourly) && hourly.TryGetProperty("time", out var hTimes))
        {
            var temps = Array(hourly, "temperature_2m");
            var probs = Array(hourly, "precipitation_probability");
            var codes = Array(hourly, "weather_code");
            var days = Array(hourly, "is_day");
            var i = 0;
            foreach (var t in hTimes.EnumerateArray())
            {
                var local = ParseLocal(t.GetString());
                if (local.HasValue)
                {
                    report.Hourly.Add(new HourForecast
                    {
                        Local = local.Value,
                        TemperatureC = At(temps, i),
                        PrecipitationProbabilityPercent = (int)Math.Round(At(probs, i)),
                        WeatherCode = (int)Math.Round(At(codes, i)),
                        IsDay = days is null || At(days, i, 1) != 0
                    });
                }

                i++;
            }
        }

        if (root.TryGetProperty("daily", out var daily) && daily.TryGetProperty("time", out var dTimes))
        {
            var codes = Array(daily, "weather_code");
            var highs = Array(daily, "temperature_2m_max");
            var lows = Array(daily, "temperature_2m_min");
            var probs = Array(daily, "precipitation_probability_max");
            var sums = Array(daily, "precipitation_sum");
            var winds = Array(daily, "wind_speed_10m_max");
            var rises = StringArray(daily, "sunrise");
            var sets = StringArray(daily, "sunset");
            var i = 0;
            foreach (var t in dTimes.EnumerateArray())
            {
                var date = ParseLocal(t.GetString());
                if (date.HasValue)
                {
                    report.Daily.Add(new DayForecast
                    {
                        Date = date.Value.Date,
                        WeatherCode = (int)Math.Round(At(codes, i)),
                        HighC = At(highs, i),
                        LowC = At(lows, i),
                        PrecipitationProbabilityPercent = (int)Math.Round(At(probs, i)),
                        PrecipitationMm = At(sums, i),
                        WindMaxKmh = At(winds, i),
                        Sunrise = ParseLocal(rises is not null && i < rises.Count ? rises[i] : null),
                        Sunset = ParseLocal(sets is not null && i < sets.Count ? sets[i] : null)
                    });
                }

                i++;
            }
        }

        return report;
    }

    public static IReadOnlyList<GeocodeResult> ParseGeocode(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<GeocodeResult>();
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var r in results.EnumerateArray())
        {
            var name = r.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            list.Add(new GeocodeResult(
                name,
                r.TryGetProperty("admin1", out var a1) ? a1.GetString() : null,
                r.TryGetProperty("country", out var c) ? c.GetString() : null,
                Num(r, "latitude"),
                Num(r, "longitude"),
                r.TryGetProperty("timezone", out var tz) ? tz.GetString() : null,
                r.TryGetProperty("population", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : null));
        }

        return list;
    }

    private static double Num(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static List<double?>? Array(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return arr.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : (double?)null).ToList();
    }

    private static List<string?>? StringArray(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return arr.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).ToList();
    }

    private static double At(List<double?>? list, int i, double fallback = 0)
        => list is not null && i < list.Count && list[i].HasValue ? list[i]!.Value : fallback;

    private static DateTime? LocalTime(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? ParseLocal(v.GetString()) : null;

    /// <summary>Open-Meteo's local times are "2026-10-23T17:30" or "2026-10-23", no offset.</summary>
    public static DateTime? ParseLocal(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return DateTime.TryParseExact(text, new[] { "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? DateTime.SpecifyKind(dt, DateTimeKind.Unspecified)
            : null;
    }
}
