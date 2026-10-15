namespace Jellyfin.Plugin.TvStation.Models;

public sealed class GlobalSettings
{
    public EncodingProfile DefaultEncoding { get; set; } = new();

    /// <summary>Seconds a station stream keeps running after the last viewer leaves.</summary>
    public int LingerSeconds { get; set; } = 30;

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
}
