namespace Jellyfin.Plugin.TvStation.Weather;

/// <summary>
/// One place's weather as the plugin understands it, whatever provider it came from. Everything is metric and
/// in the place's local time; the screens convert. Serialized to disk so a restart has something to show.
/// </summary>
public sealed class WeatherReport
{
    /// <summary>When the provider says the current conditions were observed (UTC).</summary>
    public DateTime ObservedUtc { get; set; }

    /// <summary>When the plugin fetched it (UTC).</summary>
    public DateTime FetchedUtc { get; set; }

    /// <summary>The place's IANA time zone as the provider reports it.</summary>
    public string? TimeZoneId { get; set; }

    /// <summary>The place's UTC offset at fetch time, seconds east of UTC.</summary>
    public int UtcOffsetSeconds { get; set; }

    public CurrentConditions Current { get; set; } = new();

    /// <summary>Hourly forecast from the current hour, usually 24 entries.</summary>
    public List<HourForecast> Hourly { get; set; } = new();

    /// <summary>Daily forecast from today, usually 7 entries.</summary>
    public List<DayForecast> Daily { get; set; } = new();

    public List<WeatherAlert> Alerts { get; set; } = new();

    public string Provider { get; set; } = string.Empty;

    /// <summary>Attribution the provider's licence asks for, shown in small type on every screen.</summary>
    public string Attribution { get; set; } = string.Empty;
}

public sealed class CurrentConditions
{
    public double TemperatureC { get; set; }

    public double FeelsLikeC { get; set; }

    public int HumidityPercent { get; set; }

    public double WindKmh { get; set; }

    public double GustKmh { get; set; }

    public int WindDirectionDegrees { get; set; }

    public double PressureHpa { get; set; }

    public double PrecipitationMm { get; set; }

    public int CloudCoverPercent { get; set; }

    /// <summary>WMO weather interpretation code (0 clear ... 99 thunderstorm with hail).</summary>
    public int WeatherCode { get; set; }

    public bool IsDay { get; set; } = true;
}

public sealed class HourForecast
{
    /// <summary>Local wall-clock time of the hour (unspecified kind, in the place's zone).</summary>
    public DateTime Local { get; set; }

    public double TemperatureC { get; set; }

    public int PrecipitationProbabilityPercent { get; set; }

    public int WeatherCode { get; set; }

    public bool IsDay { get; set; } = true;
}

public sealed class DayForecast
{
    /// <summary>Local date (unspecified kind).</summary>
    public DateTime Date { get; set; }

    public int WeatherCode { get; set; }

    public double HighC { get; set; }

    public double LowC { get; set; }

    public int PrecipitationProbabilityPercent { get; set; }

    public double PrecipitationMm { get; set; }

    public double WindMaxKmh { get; set; }

    /// <summary>Local sunrise, or null when the sun does not rise.</summary>
    public DateTime? Sunrise { get; set; }

    public DateTime? Sunset { get; set; }
}

public sealed class WeatherAlert
{
    public string Headline { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Minor, Moderate, Severe, Extreme (as the provider says).</summary>
    public string? Severity { get; set; }

    public DateTime? StartUtc { get; set; }

    public DateTime? EndUtc { get; set; }
}

/// <summary>A place found by the geocoding search.</summary>
public sealed record GeocodeResult(string Name, string? Region, string? Country, double Latitude, double Longitude, string? TimeZoneId, long? Population);
