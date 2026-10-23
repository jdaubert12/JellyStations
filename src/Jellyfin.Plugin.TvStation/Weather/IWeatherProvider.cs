using Jellyfin.Plugin.TvStation.Models;

namespace Jellyfin.Plugin.TvStation.Weather;

/// <summary>A forecast source. The plugin sends it a latitude and longitude and nothing else.</summary>
public interface IWeatherProvider
{
    /// <summary>Provider id as stored in <see cref="WeatherConfig.Provider"/>.</summary>
    string Id { get; }

    /// <summary>Attribution line the provider's terms ask for.</summary>
    string Attribution { get; }

    Task<WeatherReport> FetchAsync(WeatherLocation location, CancellationToken ct);

    /// <summary>Places matching a typed name, best first.</summary>
    Task<IReadOnlyList<GeocodeResult>> SearchAsync(string query, int count, CancellationToken ct);
}
