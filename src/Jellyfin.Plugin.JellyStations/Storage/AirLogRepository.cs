using Jellyfin.Plugin.JellyStations.Models;

namespace Jellyfin.Plugin.JellyStations.Storage;

/// <summary>Persists each station's air log (history/{id}.json). Not cached: the recorder keeps the live copy.</summary>
public sealed class AirLogRepository
{
    private readonly string _dir;

    public AirLogRepository(JellyStationsPaths paths)
        : this(paths.HistoryDir)
    {
    }

    /// <summary>For tests: a repository over any folder.</summary>
    internal AirLogRepository(string dir)
    {
        _dir = dir;
    }

    public string FileFor(Guid stationId) => Path.Combine(_dir, stationId.ToString("N") + ".json");

    /// <summary>The station's log, or an empty one when there is none yet or the file is of a format this build does not read.</summary>
    public AirLog Get(Guid stationId)
    {
        AirLog? loaded = null;
        try
        {
            loaded = JsonFileStore.Read<AirLog>(FileFor(stationId));
        }
        catch (Exception)
        {
            // A damaged file starts the log over rather than stopping the recorder.
        }

        if (loaded is null || loaded.Format != AirLog.CurrentFormat || loaded.StationId != stationId)
        {
            return new AirLog { StationId = stationId };
        }

        loaded.Keys = new Dictionary<string, AirLogKey>(loaded.Keys, StringComparer.Ordinal);
        return loaded;
    }

    public void Save(AirLog log)
    {
        Directory.CreateDirectory(_dir);
        JsonFileStore.Write(FileFor(log.StationId), log);
    }

    public void Delete(Guid stationId) => JsonFileStore.Delete(FileFor(stationId));
}
