namespace Jellyfin.Plugin.JellyStations.Models;

/// <summary>Small string clean-ups shared by normalisation code.</summary>
public static class Text
{
    /// <summary>The value trimmed, or null when it is null, empty or only whitespace.</summary>
    public static string? TrimOrNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// A value as it may be written to the log: line breaks and every other control character become spaces, so a
    /// station name, place name or folder typed into the dashboard (or sent to the API) cannot forge extra log
    /// lines (CodeQL cs/log-forging). Null reads as empty. Nothing is truncated, so it is safe at the point a name
    /// is stored for later log lines too.
    /// </summary>
    public static string ForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var cleaned = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        if (cleaned.Any(char.IsControl))
        {
            cleaned = new string(cleaned.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        }

        return cleaned;
    }
}
