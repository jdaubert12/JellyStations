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

    /// <summary>
    /// Hours of this suggestion's content that no enabled station plays yet. Equal to its content when nothing
    /// overlaps, 0 when the channels already play all of it.
    /// </summary>
    public double NewHours { get; set; }

    /// <summary>True when enabled stations already play everything this suggestion would: the page hides it.</summary>
    public bool Covered { get; set; }

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

    /// <summary>
    /// A library of short clips to use as the bumpers of every created station (bumpers are switched on and this
    /// library replaces the template's clips). Null = whatever the template brings.
    /// </summary>
    public Guid? BumperLibraryId { get; set; }

    /// <summary>
    /// Overrides for the thresholds in Settings (every hours rule, and the episodes a series needs), for a caller
    /// that listed with the same query. The page leaves them out and the Settings values apply.
    /// </summary>
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

/// <summary>A rule that came close: "Horror: 6.5 h, needs 8".</summary>
public sealed class NearMissDto
{
    public string Category { get; set; } = string.Empty;

    /// <summary>The station the rule would have proposed.</summary>
    public string Name { get; set; } = string.Empty;

    public double Have { get; set; }

    public double Need { get; set; }

    /// <summary>"h", "episodes", "titles" or "half-hour comedies".</summary>
    public string Unit { get; set; } = string.Empty;
}

/// <summary>Everything the suggestions page shows.</summary>
public sealed class SuggestionReportDto
{
    /// <summary>Every suggestion, ones the channels already cover included (<see cref="StationSuggestionDto.Covered"/>).</summary>
    public List<StationSuggestionDto> Suggestions { get; set; } = new();

    public List<NearMissDto> NearMisses { get; set; } = new();

    /// <summary>Libraries of short clips, offered as a bumper source for the created stations.</summary>
    public List<LibraryStatDto> ClipLibraries { get; set; } = new();

    /// <summary>The thresholds the rules ran with (from Settings).</summary>
    public SuggestionThresholds Thresholds { get; set; } = new();

    /// <summary>Enabled stations whose content was counted as already on a channel.</summary>
    public int Channels { get; set; }
}

public sealed class SuggestionCreateResultDto
{
    public List<string> Created { get; set; } = new();

    public List<string> Errors { get; set; } = new();

    /// <summary>Stations that were created but without some of the extras asked for (a logo that could not be drawn).</summary>
    public List<string> Warnings { get; set; } = new();
}
