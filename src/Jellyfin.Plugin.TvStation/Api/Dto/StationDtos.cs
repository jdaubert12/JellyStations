using Jellyfin.Plugin.TvStation.Models;

namespace Jellyfin.Plugin.TvStation.Api.Dto;

/// <summary>One compiled item of a station, for the content view.</summary>
public sealed class ContentItemDto
{
    public Guid ItemId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? EpisodeTitle { get; set; }

    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }

    public int? Year { get; set; }

    public string? Rating { get; set; }

    public int Minutes { get; set; }

    public List<string> Genres { get; set; } = new();

    /// <summary>"Pool", "Filler", "Lineup" or the slot block's name.</summary>
    public string Source { get; set; } = string.Empty;

    public bool IsSeries { get; set; }

    public bool IsMovie { get; set; }

    public bool Excluded { get; set; }

    /// <summary>True when the exclusion comes from a tag rule rather than the station's item list (not toggleable per item).</summary>
    public bool ExcludedByRule { get; set; }
}

/// <summary>Station content listing with totals.</summary>
public sealed class ContentListDto
{
    public int Total { get; set; }

    public int Offset { get; set; }

    public List<ContentItemDto> Items { get; set; } = new();

    /// <summary>Hours of distinct content (each item counted once, excluded items not counted).</summary>
    public double UniqueHours { get; set; }

    /// <summary>Hours per pass through the pool, counting weighted repeats (pool mode only).</summary>
    public double? CycleHours { get; set; }

    public int ExcludedCount { get; set; }

    public Dictionary<string, int> BySource { get; set; } = new();
}

/// <summary>Outcome of POST Stations/Import.</summary>
public sealed class ImportResultDto
{
    public List<string> Created { get; set; } = new();

    public List<string> Updated { get; set; } = new();

    public List<string> Errors { get; set; } = new();
}

public sealed class StationSummaryDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string ChannelNumber { get; set; } = string.Empty;

    public ScheduleMode Mode { get; set; }

    public bool Enabled { get; set; }

    public int ItemCount { get; set; }

    public int ExcludedCount { get; set; }

    public int Revision { get; set; }

    public bool HasLogo { get; set; }

    public ScheduleEntryDto? NowPlaying { get; set; }

    public bool Streaming { get; set; }

    /// <summary>True when the station has season windows.</summary>
    public bool Seasonal { get; set; }

    /// <summary>False when a seasonal station is currently outside all of its windows (hidden from the guide).</summary>
    public bool InSeason { get; set; } = true;

    /// <summary>Local date (yyyy-MM-dd) when the station next enters or leaves season, if seasonal.</summary>
    public string? NextSeasonChange { get; set; }
}

public sealed class ScheduleEntryDto
{
    public string ProgramId { get; set; } = string.Empty;

    public DateTime StartUtc { get; set; }

    public DateTime EndUtc { get; set; }

    public EntryKind Kind { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? EpisodeTitle { get; set; }

    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }

    public Guid? ItemId { get; set; }

    public bool IsTruncated { get; set; }

    public string? SourceLabel { get; set; }

    public double? OffsetSeconds { get; set; }

    public bool IsMovie { get; set; }

    public bool IsSeries { get; set; }
}

public sealed class CompileResultDto
{
    public int Revision { get; set; }

    public int ItemCount { get; set; }

    public List<ExcludedItem> Excluded { get; set; } = new();

    public DateTime CompiledUtc { get; set; }

    public bool ContentChanged { get; set; }
}

public sealed class StationDetailDto
{
    public Station Station { get; set; } = new();

    public CompileResultDto? Compiled { get; set; }

    public TimeSpan? CycleDuration { get; set; }
}

public sealed class StatusDto
{
    public string FfmpegPath { get; set; } = string.Empty;

    public bool FfmpegExists { get; set; }

    public string DataPath { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string ServerTimeUtc { get; set; } = string.Empty;

    public List<SessionStatusDto> Sessions { get; set; } = new();
}

public sealed class SessionStatusDto
{
    public Guid StationId { get; set; }

    public string StationName { get; set; } = string.Empty;

    public string UniqueId { get; set; } = string.Empty;

    public int Consumers { get; set; }

    public int Readers { get; set; }

    public DateTime StartedUtc { get; set; }

    public long BytesStreamed { get; set; }

    public int? EncoderPid { get; set; }

    public ScheduleEntryDto? Current { get; set; }

    public List<string> RecentLog { get; set; } = new();
}

public sealed class ValidationErrorDto
{
    public List<string> Errors { get; set; } = new();
}
