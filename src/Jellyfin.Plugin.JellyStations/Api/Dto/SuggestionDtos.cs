using Jellyfin.Plugin.JellyStations.Models;

namespace Jellyfin.Plugin.JellyStations.Api.Dto;

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

/// <summary>What the created stations share: numbering, time zone, a template for the look, a drawn logo.</summary>
public class SuggestionOptionsDto
{
    /// <summary>Time zone for the new stations; defaults to an existing station's zone, then UTC.</summary>
    public string? TimeZoneId { get; set; }

    /// <summary>Channel number for the first station; the rest follow, skipping numbers in use. Default: after the highest existing channel.</summary>
    public int? FirstChannelNumber { get; set; }

    /// <summary>A station template (built-in or saved) whose look and breaks every created station gets. Null = plain.</summary>
    public Guid? TemplateId { get; set; }

    /// <summary>Draw a logo for each station (the editor's generated card). Default true.</summary>
    public bool Logo { get; set; } = true;

    /// <summary>The same lowered thresholds the list was fetched with, so the keys resolve.</summary>
    public double? MinHours { get; set; }

    public int? MinEpisodes { get; set; }
}

public sealed class SuggestionCreateRequestDto : SuggestionOptionsDto
{
    public List<string> Keys { get; set; } = new();
}

/// <summary>One suggestion as an unsaved draft, for "Edit before creating".</summary>
public sealed class SuggestionDraftRequestDto : SuggestionOptionsDto
{
    public string Key { get; set; } = string.Empty;
}

public sealed class SuggestionCreateResultDto
{
    public List<string> Created { get; set; } = new();

    public List<string> Errors { get; set; } = new();

    /// <summary>Stations that were created but without some of the extras asked for (a logo that could not be drawn).</summary>
    public List<string> Warnings { get; set; } = new();
}
