namespace Jellyfin.Plugin.JellyStations.Models;

public sealed class GlobalSettings
{
    public EncodingProfile DefaultEncoding { get; set; } = new();

    /// <summary>The logo bug every station draws unless it sets its own (<see cref="Station.LogoOverlay"/>). Off by default.</summary>
    public LogoOverlayConfig LogoOverlay { get; set; } = new();

    /// <summary>The "Up next" card every station shows unless it sets its own (<see cref="Station.UpNextCard"/>). Off by default.</summary>
    public UpNextCardConfig UpNextCard { get; set; } = new();

    /// <summary>
    /// The padding a new station starts with: a blank one, one from a built-in preset, one made from "Not on a
    /// channel", and a suggested one created plain or with a built-in preset's look. Null = each of those keeps
    /// its own choice. A saved template always keeps the padding it was saved with.
    /// </summary>
    public PaddingDefault? NewStationPadding { get; set; }

    /// <summary>Seconds a station stream keeps running after the last viewer leaves.</summary>
    public int LingerSeconds { get; set; } = 30;

    /// <summary>
    /// Seconds a station stream may run with nobody reading it before it is stopped, whatever the server's viewer
    /// count says. Catches viewers that vanished without telling Jellyfin (an app killed, a TV switched off, a
    /// dropped connection). 0 turns it off; otherwise 30 to 3600.
    /// </summary>
    public int IdleStopSeconds { get; set; } = 120;

    /// <summary>Safety cap on how far ahead schedules are enumerated.</summary>
    public int MaxGuideDays { get; set; } = 21;

    public string? FfmpegPathOverride { get; set; }

    public int MaxConcurrentSessions { get; set; } = 4;

    public string SlateBackgroundColor { get; set; } = "#101010";

    public bool ShowTextOnSlate { get; set; } = true;

    public bool ShowLogoOnSlate { get; set; } = true;

    /// <summary>
    /// Library tags kept off every station unless the station opts out (for example "christmas" so holiday films
    /// only air on a seasonal station). Hand-picked items and Tag sources for the same tag are never affected.
    /// </summary>
    public List<string> ExcludedTags { get; set; } = new();

    /// <summary>
    /// Libraries left out of every station unless it opts out (<see cref="Station.UseGlobalExcludedLibraries"/>), the
    /// same way as <see cref="Station.ExcludedLibraryIds"/>, and out of station suggestions and "Not on a channel".
    /// </summary>
    public List<Guid> ExcludedLibraryIds { get; set; } = new();

    /// <summary>
    /// Shows and movies added to the Jellyfin library after this date (server time, the date itself still counts as
    /// in time) are left out of every station, station suggestions and "Not on a channel"; null = no cutoff. An
    /// episode goes by its show's date. Anything picked by name still plays. See <c>AddedCutoff</c>.
    /// </summary>
    public DateOnly? HideAddedAfter { get; set; }

    /// <summary>Saved time blocks (days, time, sources, order) that can be added to any station.</summary>
    public List<SlotBlock> BlockTemplates { get; set; } = new();

    /// <summary>Station templates saved from a station editor (everything but the content). The built-in presets are not stored.</summary>
    public List<StationTemplate> StationTemplates { get; set; } = new();

    /// <summary>Week layouts saved from a station's blocks (see <see cref="WeekLayout"/>). The built-in ones are not stored.</summary>
    public List<WeekLayout> WeekLayouts { get; set; } = new();

    /// <summary>How much content each suggestion rule needs before it proposes a station.</summary>
    public SuggestionThresholds Suggestions { get; set; } = new();

    /// <summary>
    /// "Our household": the Jellyfin users whose play history a station may read (<see cref="HouseholdConfig"/>).
    /// Empty = no station reads any history, whatever its own switches say.
    /// </summary>
    public List<Guid> HouseholdUserIds { get; set; } = new();
}
