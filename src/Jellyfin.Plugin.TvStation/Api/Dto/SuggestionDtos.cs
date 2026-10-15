using Jellyfin.Plugin.TvStation.Models;

namespace Jellyfin.Plugin.TvStation.Api.Dto;

/// <summary>A station the library could support, ready to create.</summary>
public sealed class StationSuggestionDto
{
    /// <summary>Stable key, e.g. "genre:Comedy", "series:{id}", "kids", "franchise-nights".</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>"Kids & family", "Genre", "Series", "Franchise", "Collection", "Decade" or "Seasonal".</summary>
    public string Category { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public double Hours { get; set; }

    /// <summary>True when a station with this name already exists.</summary>
    public bool Exists { get; set; }

    /// <summary>The station definition that would be created (no channel number or time zone yet).</summary>
    public Station Station { get; set; } = new();
}

public sealed class SuggestionCreateRequestDto
{
    public List<string> Keys { get; set; } = new();

    /// <summary>Time zone for the new stations; defaults to an existing station's zone, then UTC.</summary>
    public string? TimeZoneId { get; set; }

    /// <summary>The same lowered thresholds the list was fetched with, so the keys resolve.</summary>
    public double? MinHours { get; set; }

    public int? MinEpisodes { get; set; }
}

public sealed class SuggestionCreateResultDto
{
    public List<string> Created { get; set; } = new();

    public List<string> Errors { get; set; } = new();
}
