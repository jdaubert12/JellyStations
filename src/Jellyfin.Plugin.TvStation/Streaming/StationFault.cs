namespace Jellyfin.Plugin.TvStation.Streaming;

/// <summary>The last thing that went wrong while streaming a station; kept after the session ends so the dashboard can show it.</summary>
public sealed record StationFault(DateTime Utc, string Message);
