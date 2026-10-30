namespace Jellyfin.Plugin.JellyStations.Models;

/// <summary>Small string clean-ups shared by normalisation code.</summary>
public static class Text
{
    /// <summary>The value trimmed, or null when it is null, empty or only whitespace.</summary>
    public static string? TrimOrNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
