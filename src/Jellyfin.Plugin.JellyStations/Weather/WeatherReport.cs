namespace Jellyfin.Plugin.JellyStations.Weather;

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

    /// <summary>Chance of any precipitation (rain, showers or snow) in the hour.</summary>
    public int PrecipitationProbabilityPercent { get; set; }

    /// <summary>Rain plus showers forecast for the hour, liquid millimetres.</summary>
    public double RainMm { get; set; }

    /// <summary>Snowfall forecast for the hour, centimetres of snow.</summary>
    public double SnowfallCm { get; set; }

    public int WeatherCode { get; set; }

    public bool IsDay { get; set; } = true;

    /// <summary>What the chance is a chance of.</summary>
    public PrecipitationKind PrecipitationKind => WmoWeather.PrecipitationType(WeatherCode, RainMm, SnowfallCm, TemperatureC);
}

public sealed class DayForecast
{
    /// <summary>Local date (unspecified kind).</summary>
    public DateTime Date { get; set; }

    public int WeatherCode { get; set; }

    public double HighC { get; set; }

    public double LowC { get; set; }

    /// <summary>Highest hourly chance of any precipitation in the day.</summary>
    public int PrecipitationProbabilityPercent { get; set; }

    /// <summary>Total precipitation for the day, liquid millimetres (snow counted as melt water).</summary>
    public double PrecipitationMm { get; set; }

    /// <summary>Rain plus showers for the day, liquid millimetres.</summary>
    public double RainMm { get; set; }

    /// <summary>Snowfall for the day, centimetres of snow.</summary>
    public double SnowfallCm { get; set; }

    public double WindMaxKmh { get; set; }

    /// <summary>What the chance is a chance of.</summary>
    public PrecipitationKind PrecipitationKind => WmoWeather.PrecipitationType(WeatherCode, RainMm, SnowfallCm, (HighC + LowC) / 2);

    /// <summary>Local sunrise, or null when the sun does not rise.</summary>
    public DateTime? Sunrise { get; set; }

    public DateTime? Sunset { get; set; }
}

/// <summary>
/// A watch, warning, advisory or statement in force for a place, as the alert source reports it. Times are UTC;
/// the screens show them in the place's zone.
/// </summary>
public sealed class WeatherAlert
{
    /// <summary>The source's id for the alert (an update of the same alert gets a new id).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>"Winter Storm Warning", "Dense Fog Advisory"...</summary>
    public string Event { get; set; } = string.Empty;

    public string Headline { get; set; } = string.Empty;

    /// <summary>The body, paragraphs separated by a single newline, lines unwrapped.</summary>
    public string? Description { get; set; }

    /// <summary>What to do about it, when the source says.</summary>
    public string? Instruction { get; set; }

    /// <summary>Minor, Moderate, Severe, Extreme or Unknown (as the source says).</summary>
    public string? Severity { get; set; }

    public string? Urgency { get; set; }

    public string? Certainty { get; set; }

    /// <summary>Who issued it ("NWS Boston/Norton MA").</summary>
    public string? Sender { get; set; }

    /// <summary>The areas it covers, as the source words them.</summary>
    public string? Area { get; set; }

    /// <summary>When this version of the alert was sent.</summary>
    public DateTime? SentUtc { get; set; }

    /// <summary>When the hazard begins (null = already, or unknown).</summary>
    public DateTime? StartUtc { get; set; }

    /// <summary>When the hazard ends (null = unknown; the alert then runs until it expires).</summary>
    public DateTime? EndUtc { get; set; }

    /// <summary>When the message itself lapses; a continuing hazard gets a fresh message before then.</summary>
    public DateTime? ExpiresUtc { get; set; }

    /// <summary>4 for Extreme down to 1 for Minor, 0 when unknown.</summary>
    public int SeverityRank => Severity?.ToLowerInvariant() switch
    {
        "extreme" => 4,
        "severe" => 3,
        "moderate" => 2,
        "minor" => 1,
        _ => 0
    };

    /// <summary>Still worth showing at <paramref name="nowUtc"/>: neither ended nor expired. A hazard that has not started yet counts (a watch for tomorrow is news today).</summary>
    public bool IsActive(DateTime nowUtc)
    {
        if (EndUtc is { } end && end <= nowUtc)
        {
            return false;
        }

        if (ExpiresUtc is { } expires && expires <= nowUtc)
        {
            return false;
        }

        return true;
    }
}

/// <summary>A place found by the geocoding search.</summary>
public sealed record GeocodeResult(string Name, string? Region, string? Country, double Latitude, double Longitude, string? TimeZoneId, long? Population);
