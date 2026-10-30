using Jellyfin.Plugin.JellyStations.Models;

namespace Jellyfin.Plugin.JellyStations.Api.Dto;

/// <summary>A place the geocoding search found.</summary>
public sealed class WeatherPlaceDto
{
    public string Name { get; set; } = string.Empty;

    /// <summary>"Massachusetts, United States".</summary>
    public string Region { get; set; } = string.Empty;

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    public string? TimeZoneId { get; set; }

    public long? Population { get; set; }
}

public sealed class WeatherStatusDto
{
    /// <summary>The weather channel the places belong to (the station itself, or the channel an ordinary station shows).</summary>
    public Guid StationId { get; set; }

    public string StationName { get; set; } = string.Empty;

    public WeatherUnits Units { get; set; }

    public bool ShowAlerts { get; set; }

    public bool ShowRadar { get; set; }

    public bool ShowForecastRadar { get; set; }

    public string Provider { get; set; } = string.Empty;

    public string Attribution { get; set; } = string.Empty;

    public List<WeatherLocationStatusDto> Locations { get; set; } = new();

    /// <summary>Music tracks the station plays under the weather.</summary>
    public int MusicTracks { get; set; }

    /// <summary>Of those, how many have been measured for loudness levelling.</summary>
    public int MusicLevelled { get; set; }

    /// <summary>How many could not be measured (unreadable files play as recorded).</summary>
    public int MusicUnmeasurable { get; set; }

    /// <summary>True when the station levels its music.</summary>
    public bool MusicLevelling { get; set; }
}

public sealed class WeatherLocationStatusDto
{
    public Guid LocationId { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Valid { get; set; }

    public DateTime? LastAttemptUtc { get; set; }

    public DateTime? LastSuccessUtc { get; set; }

    public string? LastError { get; set; }

    public int Failures { get; set; }

    /// <summary>True when the screens say the forecast is not current.</summary>
    public bool Stale { get; set; }

    public double? TemperatureC { get; set; }

    public int? WeatherCode { get; set; }

    public string? Conditions { get; set; }

    public string? TimeZoneId { get; set; }

    /// <summary>The radar source covering the place; null when none does or nothing was fetched yet.</summary>
    public string? RadarSource { get; set; }

    public int RadarFrames { get; set; }

    public DateTime? RadarLatestUtc { get; set; }

    public string? RadarError { get; set; }

    /// <summary>The forecast radar source covering the place; null when none does or nothing was fetched yet.</summary>
    public string? ForecastRadarSource { get; set; }

    /// <summary>Forecast frames on hand from the current model run.</summary>
    public int ForecastRadarFrames { get; set; }

    /// <summary>The model run the forecast frames come from.</summary>
    public DateTime? ForecastRadarRunUtc { get; set; }

    /// <summary>The furthest forecast frame on hand.</summary>
    public DateTime? ForecastRadarLatestUtc { get; set; }

    public string? ForecastRadarError { get; set; }

    /// <summary>The alert source asked for the place; null when none covers it or nothing was fetched yet.</summary>
    public string? AlertSource { get; set; }

    /// <summary>Alerts in force now.</summary>
    public int AlertCount { get; set; }

    /// <summary>"Winter Storm Warning until Thu 4:00 PM · 1 more", or null when there is none.</summary>
    public string? AlertSummary { get; set; }

    public DateTime? AlertsCheckedUtc { get; set; }

    public string? AlertError { get; set; }

    /// <summary>The source said it does not cover the place, so the alerts screen is skipped for it.</summary>
    public bool AlertsNotCovered { get; set; }
}
