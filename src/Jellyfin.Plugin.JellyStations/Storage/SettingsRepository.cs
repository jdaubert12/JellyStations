using Jellyfin.Plugin.JellyStations.Models;

namespace Jellyfin.Plugin.JellyStations.Storage;

/// <summary>Persists global settings (settings.json).</summary>
public sealed class SettingsRepository
{
    private readonly JellyStationsPaths _paths;
    private readonly object _lock = new();
    private GlobalSettings? _cached;

    public SettingsRepository(JellyStationsPaths paths)
    {
        _paths = paths;
    }

    public GlobalSettings Get()
    {
        lock (_lock)
        {
            _cached ??= JsonFileStore.Read<GlobalSettings>(_paths.SettingsFile) ?? new GlobalSettings();
            return _cached;
        }
    }

    public void Save(GlobalSettings settings)
    {
        lock (_lock)
        {
            _paths.EnsureDirectories();
            JsonFileStore.Write(_paths.SettingsFile, settings);
            _cached = settings;
        }
    }
}
