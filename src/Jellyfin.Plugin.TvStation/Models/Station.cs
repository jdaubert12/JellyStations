namespace Jellyfin.Plugin.TvStation.Models;

/// <summary>A curated TV station. Root aggregate persisted as stations/{id}.json.</summary>
public sealed class Station
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string ChannelNumber { get; set; } = string.Empty;

    public string? CallSign { get; set; }

    public bool Enabled { get; set; } = true;

    public string? LogoFileName { get; set; }

    /// <summary>Optional logo bug drawn over programs. Off by default.</summary>
    public LogoOverlayConfig LogoOverlay { get; set; } = new();

    /// <summary>IANA or Windows time zone id used for slot times.</summary>
    public string TimeZoneId { get; set; } = "UTC";

    /// <summary>Schedule epoch. All cycles/weeks are counted from here.</summary>
    public DateTime AnchorUtc { get; set; }

    /// <summary>Bumped on every schedule-affecting change; part of every seed.</summary>
    public int Revision { get; set; } = 1;

    /// <summary>0 = programs start back-to-back; otherwise each program is padded with a slate to this grid.</summary>
    public int PaddingGridMinutes { get; set; }

    /// <summary>
    /// Pad by program length instead of a fixed grid: programs up to an hour (including the bumper break) fill a
    /// half-hour slot, longer programs fill whole hours. PaddingGridMinutes is then only used to align the anchor.
    /// </summary>
    public bool PaddingByLength { get; set; }

    public ScheduleMode Mode { get; set; } = ScheduleMode.Pool;

    public PoolConfig Pool { get; set; } = new();

    /// <summary>Weekly time blocks. In Slots mode they are the schedule; in Pool mode they optionally take over the pool at fixed times.</summary>
    public SlotsConfig Slots { get; set; } = new();

    public LineupConfig Lineup { get; set; } = new();

    /// <summary>Per-station encoding override; null uses the global default.</summary>
    public EncodingProfile? Encoding { get; set; }

    public bool AutoRecompileNightly { get; set; } = true;

    /// <summary>
    /// Recurring yearly date ranges (station time zone, inclusive) during which the channel is listed in the guide.
    /// Empty = all year. Ranges may wrap the year end, e.g. Dec 1 - Jan 6.
    /// </summary>
    public List<SeasonWindow> ActiveWindows { get; set; } = new();

    /// <summary>Items never to play on this station, whatever the sources expand to. Lineups ignore it.</summary>
    public List<Guid> ExcludedItemIds { get; set; } = new();

    /// <summary>
    /// Library tags that keep an item off this station when a source expands to it (for example "christmas").
    /// Hand-picked movies and episodes, lineups and a Tag source for the same tag are never affected.
    /// </summary>
    public List<string> ExcludedTags { get; set; } = new();

    /// <summary>Also apply the plugin-wide excluded tags from Settings (default on).</summary>
    public bool UseGlobalExcludedTags { get; set; } = true;

    public BumperConfig Bumpers { get; set; } = new();

    /// <summary>Text on the station card shown during gaps. Null = "We'll be right back".</summary>
    public string? CardText { get; set; }

    public const string DefaultCardText = "We'll be right back";

    public string EffectiveCardText => string.IsNullOrWhiteSpace(CardText) ? DefaultCardText : CardText!;

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
