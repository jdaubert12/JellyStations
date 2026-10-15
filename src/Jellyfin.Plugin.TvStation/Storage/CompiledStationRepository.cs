using System.Collections.Concurrent;
using Jellyfin.Plugin.TvStation.Models;

namespace Jellyfin.Plugin.TvStation.Storage;

/// <summary>Persists compiled station snapshots (compiled/{id}.json).</summary>
public sealed class CompiledStationRepository
{
    private readonly TvStationPaths _paths;
    private readonly ConcurrentDictionary<Guid, CompiledStation> _cache = new();

    public CompiledStationRepository(TvStationPaths paths)
    {
        _paths = paths;
    }

    public CompiledStation? Get(Guid stationId)
    {
        if (_cache.TryGetValue(stationId, out var c))
        {
            return c;
        }

        var loaded = JsonFileStore.Read<CompiledStation>(_paths.CompiledFile(stationId));
        if (loaded is not null)
        {
            _cache[stationId] = loaded;
        }

        return loaded;
    }

    public void Save(CompiledStation compiled)
    {
        _paths.EnsureDirectories();
        JsonFileStore.Write(_paths.CompiledFile(compiled.StationId), compiled);
        _cache[compiled.StationId] = compiled;
    }

    public void Delete(Guid stationId)
    {
        _cache.TryRemove(stationId, out _);
        JsonFileStore.Delete(_paths.CompiledFile(stationId));
    }

    public void Evict(Guid stationId) => _cache.TryRemove(stationId, out _);
}
