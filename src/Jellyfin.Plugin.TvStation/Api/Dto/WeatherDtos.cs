namespace Jellyfin.Plugin.TvStation.Api.Dto;

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
    public Guid StationId { get; set; }

    public string Provider { get; set; } = string.Empty;

    public string Attribution { get; set; } = string.Empty;

    public List<WeatherLocationStatusDto> Locations { get; set; } = new();
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
}
