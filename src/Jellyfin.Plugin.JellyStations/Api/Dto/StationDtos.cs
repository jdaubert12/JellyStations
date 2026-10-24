using Jellyfin.Plugin.JellyStations.Models;

namespace Jellyfin.Plugin.JellyStations.Api.Dto;

/// <summary>One compiled item of a station, for the content view.</summary>
public sealed class ContentItemDto
{
    /// <summary>A music track of a weather channel (the title is the track, the episode title the artist).</summary>
    public bool IsMusic { get; set; }

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

/// <summary>POST Schedule/CoveredBy: which of a section's existing sources already play the given items.</summary>
public sealed class CoveredByRequestDto
{
    /// <summary>The station being edited, read only for its excluded-tag settings; may be omitted.</summary>
    public Station? Draft { get; set; }

    /// <summary>The sources of the section (pool, block, filler, bumpers) the items would be added to.</summary>
    public List<ContentSource> Sources { get; set; } = new();

    public List<Guid> ItemIds { get; set; } = new();
}

/// <summary>One item that an existing source already plays.</summary>
public sealed class CoveredItemDto
{
    public Guid ItemId { get; set; }

    public SourceKind Kind { get; set; }

    /// <summary>The covering source's display name (genre, tag, series, collection, playlist or library name).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>'Genre "Comedy"' style label, including any rating or year filter.</summary>
    public string Label { get; set; } = string.Empty;
}

/// <summary>Outcome of POST Stations/Import.</summary>
public sealed class ImportResultDto
{
    public List<string> Created { get; set; } = new();

    public List<string> Updated { get; set; } = new();

    public List<string> Errors { get; set; } = new();

    /// <summary>Item references whose id was unknown on this server and that were matched by name.</summary>
    public List<string> Resolved { get; set; } = new();

    /// <summary>Item references that could not be matched and were dropped from the imported station.</summary>
    public List<string> Unmatched { get; set; } = new();
}

public sealed class StationSummaryDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string ChannelNumber { get; set; } = string.Empty;

    public ScheduleMode Mode { get; set; }

    public bool Enabled { get; set; }

    public int ItemCount { get; set; }

    /// <summary>Weather channel: music tracks compiled.</summary>
    public int MusicCount { get; set; }

    /// <summary>Weather channel: places configured.</summary>
    public int LocationCount { get; set; }

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

    /// <summary>The next programs after the current one (up to two; bumper clips skipped).</summary>
    public List<ScheduleEntryDto> Next { get; set; } = new();

    /// <summary>The last streaming fault for this station (missing file, encoder exit), kept until dismissed or replaced.</summary>
    public string? LastError { get; set; }

    public DateTime? LastErrorUtc { get; set; }

    public StationHealthDto? Health { get; set; }
}

/// <summary>What the last compile says about a station's content.</summary>
public sealed class StationHealthDto
{
    public Guid StationId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string ChannelNumber { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public DateTime? CompiledUtc { get; set; }

    /// <summary>Hours of distinct programming (each item once; bumper clips not counted).</summary>
    public double UniqueHours { get; set; }

    /// <summary>Sources that contributed no items, as "Section: Kind \"name\"".</summary>
    public List<string> EmptySources { get; set; } = new();

    /// <summary>Excluded item counts keyed by <see cref="Models.ExclusionReason"/> name.</summary>
    public Dictionary<string, int> ExcludedByReason { get; set; } = new();

    /// <summary>Human-readable problems; empty when the station is healthy.</summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>Weather channel: hours of music before it repeats; null for other stations.</summary>
    public double? MusicHours { get; set; }

    public bool Healthy => Warnings.Count == 0;
}

public sealed class HealthReportDto
{
    public string GeneratedUtc { get; set; } = string.Empty;

    public List<StationHealthDto> Stations { get; set; } = new();

    public int StationsWithWarnings { get; set; }
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

/// <summary>Outcome of recompiling every station at once.</summary>
public sealed class RecompileAllResultDto
{
    public int ChangedCount { get; set; }

    public int FailedCount { get; set; }

    public List<StationCompileResultDto> Stations { get; set; } = new();
}

public sealed class StationCompileResultDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string ChannelNumber { get; set; } = string.Empty;

    public int ItemCount { get; set; }

    public int ExcludedCount { get; set; }

    public bool ContentChanged { get; set; }

    /// <summary>Set when this station failed to compile; the others were still processed.</summary>
    public string? Error { get; set; }
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

    public EncoderStatusDto? Encoder { get; set; }
}

/// <summary>How a station's encoder is doing right now: the decoder it really uses, its CPU, and whether it keeps real time.</summary>
public sealed class EncoderStatusDto
{
    public bool ProcessRunning { get; set; }

    public bool IsSlate { get; set; }

    public DateTime? SegmentStartedUtc { get; set; }

    /// <summary>Hardware decoder the profile asks for after "Follow Jellyfin" is resolved; empty for software.</summary>
    public string RequestedDecoder { get; set; } = string.Empty;

    /// <summary>hardware, software, unverified, fallback or disabled.</summary>
    public string Decoder { get; set; } = string.Empty;

    public string? DecoderDetail { get; set; }

    public string? GpuDevice { get; set; }

    public string VideoEncoder { get; set; } = string.Empty;

    public bool EncoderIsHardware { get; set; }

    public bool ToneMapping { get; set; }

    public bool SourceIsHdr { get; set; }

    public bool Overlay { get; set; }

    public double? CpuCores { get; set; }

    public double? AheadSeconds { get; set; }

    public double? MinAheadSeconds { get; set; }

    public double? SecondsSinceOutput { get; set; }

    public int Warnings { get; set; }

    public string? LastWarning { get; set; }

    /// <summary>ok, warn or bad.</summary>
    public string State { get; set; } = "ok";

    public List<string> Problems { get; set; } = new();
}

/// <summary>The hardware picture: Jellyfin's setting, what the default profile resolves to, and what ffmpeg and the host offer.</summary>
public sealed class HardwareInfoDto
{
    public string JellyfinAcceleration { get; set; } = string.Empty;

    public string? JellyfinQsvDevice { get; set; }

    public string? JellyfinVaapiDevice { get; set; }

    /// <summary>What the default encoding profile's "Follow Jellyfin" (or explicit choice) comes out as.</summary>
    public string DefaultDecoder { get; set; } = string.Empty;

    public string DefaultDevice { get; set; } = string.Empty;

    public string DefaultEncoder { get; set; } = string.Empty;

    public string Os { get; set; } = string.Empty;

    public int CpuCores { get; set; }

    public bool CanInspectProcesses { get; set; }

    public List<string> DrmNodes { get; set; } = new();

    public string FfmpegPath { get; set; } = string.Empty;

    public string FfmpegVersion { get; set; } = string.Empty;

    public List<string> Hwaccels { get; set; } = new();

    public List<string> H264Encoders { get; set; } = new();

    public bool HasTonemap { get; set; }

    public string? FfmpegError { get; set; }

    public List<string> Notes { get; set; } = new();
}

public sealed class HardwareTestRequestDto
{
    /// <summary>Station whose profile and current program to test; the first station with a playable file when omitted.</summary>
    public Guid? StationId { get; set; }

    /// <summary>Overrides the profile's hardware decoding for this run ("auto", "", qsv, vaapi, cuda, videotoolbox, d3d11va).</summary>
    public string? HardwareDecode { get; set; }

    public string? HardwareDevice { get; set; }

    public string? VideoEncoder { get; set; }

    /// <summary>Seconds of video to run through (2-15, default 5).</summary>
    public int? Seconds { get; set; }
}

public sealed class ValidationErrorDto
{
    public List<string> Errors { get; set; } = new();
}
