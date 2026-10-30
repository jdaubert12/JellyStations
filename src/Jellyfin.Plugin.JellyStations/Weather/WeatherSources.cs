using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;

namespace Jellyfin.Plugin.JellyStations.Weather;

/// <summary>
/// Which weather channel a station's weather comes from. A weather channel shows its own places; an ordinary
/// station with a weather time block or weather in the breaks shows the places, look and music of the weather
/// channel it names (<see cref="Station.WeatherSourceStationId"/>), or of the first weather channel when it
/// names none. Forecasts, alerts and radar are therefore fetched once per place however many stations air it.
/// </summary>
public static class WeatherSources
{
    public static bool IsChannel(Station station) => station.Mode == ScheduleMode.Weather;

    /// <summary>
    /// The weather channel whose places <paramref name="station"/> shows: itself for a weather channel, the named
    /// (or else the first) weather channel for an ordinary station that puts weather on the air; null when the
    /// station shows no weather or no weather channel exists.
    /// </summary>
    public static Station? ChannelFor(Station station, IReadOnlyList<Station> all)
    {
        if (IsChannel(station))
        {
            return station;
        }

        if (!station.UsesWeather)
        {
            return null;
        }

        if (station.WeatherSourceStationId is { } id && id != Guid.Empty)
        {
            var named = all.FirstOrDefault(s => s.Id == id && IsChannel(s));
            if (named is not null)
            {
                return named;
            }
        }

        return all.FirstOrDefault(IsChannel);
    }

    /// <summary>The weather settings <paramref name="station"/> airs, or null (see <see cref="ChannelFor"/>).</summary>
    public static WeatherConfig? ConfigFor(Station station, IReadOnlyList<Station> all) => ChannelFor(station, all)?.Weather;

    /// <summary>The ordinary stations that air <paramref name="channel"/>'s weather in a block or in their breaks.</summary>
    public static IReadOnlyList<Station> Borrowers(Station channel, IReadOnlyList<Station> all)
        => all.Where(s => !IsChannel(s) && s.UsesWeather && ChannelFor(s, all)?.Id == channel.Id).ToList();

    /// <summary>
    /// The weather channels whose places should be fetched now: those on the air themselves (enabled and in
    /// season) and those an ordinary station on the air borrows from, even while that channel is disabled or out
    /// of season.
    /// </summary>
    public static IReadOnlyList<Station> InUse(IReadOnlyList<Station> all, DateTime nowUtc)
    {
        var result = new List<Station>();
        foreach (var channel in all.Where(IsChannel))
        {
            if (OnAir(channel, nowUtc) || all.Any(s => !IsChannel(s) && s.UsesWeather && OnAir(s, nowUtc) && ChannelFor(s, all)?.Id == channel.Id))
            {
                result.Add(channel);
            }
        }

        return result;
    }

    private static bool OnAir(Station station, DateTime nowUtc) => station.Enabled && SeasonWindows.IsActive(station, nowUtc);
}
