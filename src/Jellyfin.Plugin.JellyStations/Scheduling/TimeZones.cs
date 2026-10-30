namespace Jellyfin.Plugin.JellyStations.Scheduling;

/// <summary>Looks up IANA/Windows time zone ids on this host.</summary>
public static class TimeZones
{
    /// <summary>The zone with this id, or null when the id is blank or unknown on this host.</summary>
    public static TimeZoneInfo? TryFind(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The zone with this id, or UTC when the id is blank or unknown on this host.</summary>
    public static TimeZoneInfo Resolve(string? id) => TryFind(id) ?? TimeZoneInfo.Utc;
}
