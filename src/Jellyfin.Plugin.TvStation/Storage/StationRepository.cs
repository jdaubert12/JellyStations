using System.Collections.Concurrent;
using Jellyfin.Plugin.TvStation.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvStation.Storage;

/// <summary>CRUD over stations/{id}.json with an in-memory cache.</summary>
public sealed class StationRepository
{
    private readonly TvStationPaths _paths;
    private readonly ILogger<StationRepository> _logger;
    private readonly ConcurrentDictionary<Guid, Station> _cache = new();
    private volatile bool _loaded;
    private readonly object _loadLock = new();

    public StationRepository(TvStationPaths paths, ILogger<StationRepository> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <summary>Raised after a station is saved or deleted.</summary>
    public event EventHandler<Guid>? Changed;

    public IReadOnlyList<Station> GetAll()
    {
        EnsureLoaded();
        return _cache.Values.OrderBy(s => SortKey(s.ChannelNumber)).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Station? Get(Guid id)
    {
        EnsureLoaded();
        return _cache.TryGetValue(id, out var s) ? s : null;
    }

    public void Save(Station station)
    {
        EnsureLoaded();
        _paths.EnsureDirectories();
        station.UpdatedUtc = DateTime.UtcNow;
        JsonFileStore.Write(_paths.StationFile(station.Id), station);
        _cache[station.Id] = station;
        Changed?.Invoke(this, station.Id);
    }

    public bool Delete(Guid id)
    {
        EnsureLoaded();
        var removed = _cache.TryRemove(id, out _);
        JsonFileStore.Delete(_paths.StationFile(id));
        if (removed)
        {
            Changed?.Invoke(this, id);
        }

        return removed;
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (_loadLock)
        {
            if (_loaded)
            {
                return;
            }

            _paths.EnsureDirectories();
            foreach (var file in Directory.EnumerateFiles(_paths.StationsDir, "*.json"))
            {
                try
                {
                    var station = JsonFileStore.Read<Station>(file);
                    if (station is not null && station.Id != Guid.Empty)
                    {
                        _cache[station.Id] = station;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "TvStation: failed to load station file {File}", file);
                }
            }

            _loaded = true;
            _logger.LogInformation("TvStation: loaded {Count} station(s)", _cache.Count);
        }
    }

    private static (int, string) SortKey(string number)
    {
        return int.TryParse(number, out var n) ? (n, string.Empty) : (int.MaxValue, number ?? string.Empty);
    }
}
