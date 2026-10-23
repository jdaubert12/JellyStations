using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.TvStation.Storage;

/// <summary>Resolves the plugin's on-disk layout under the server data folder.</summary>
public sealed class TvStationPaths
{
    public TvStationPaths(IApplicationPaths appPaths)
    {
        Root = Path.Combine(appPaths.DataPath, "TvStation");
        StationsDir = Path.Combine(Root, "stations");
        CompiledDir = Path.Combine(Root, "compiled");
        LogosDir = Path.Combine(Root, "logos");
        CardsDir = Path.Combine(Root, "cards");
        WeatherDir = Path.Combine(Root, "weather");
        MusicDir = Path.Combine(Root, "music");
        AssetsDir = Path.Combine(Root, "assets");
        SettingsFile = Path.Combine(Root, "settings.json");
        MusicIndexFile = Path.Combine(Root, "music-index.json");
    }

    /// <summary>Cached weather reports and the rendered weather screens, per place and station.</summary>
    public string WeatherDir { get; }

    /// <summary>Audio files people drop in for weather channel music (one subfolder per station or shared).</summary>
    public string MusicDir { get; }

    /// <summary>Fonts and other files the plugin unpacks from its own assembly for ffmpeg to read.</summary>
    public string AssetsDir { get; }

    /// <summary>Probed durations and tags of music folder files, keyed by path.</summary>
    public string MusicIndexFile { get; }

    public string WeatherReportFile(Guid locationId) => Path.Combine(WeatherDir, "reports", locationId.ToString("N") + ".json");

    public string WeatherScreensDir(Guid stationId) => Path.Combine(WeatherDir, "screens", stationId.ToString("N"));

    /// <summary>A folder name under <see cref="MusicDir"/>, or an absolute path, as the person typed it.</summary>
    public string ResolveMusicFolder(string folder)
        => Path.IsPathRooted(folder) ? folder : Path.Combine(MusicDir, folder);

    /// <summary>Rendered "Up next" cards, one file per program as it is about to play; rewritten constantly.</summary>
    public string CardsDir { get; }

    public string Root { get; }

    public string StationsDir { get; }

    public string CompiledDir { get; }

    public string LogosDir { get; }

    public string SettingsFile { get; }

    public string StationFile(Guid id) => Path.Combine(StationsDir, id.ToString("N") + ".json");

    public string CompiledFile(Guid id) => Path.Combine(CompiledDir, id.ToString("N") + ".json");

    public string LogoFile(string fileName) => Path.Combine(LogosDir, fileName);

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(StationsDir);
        Directory.CreateDirectory(CompiledDir);
        Directory.CreateDirectory(LogosDir);
        Directory.CreateDirectory(CardsDir);
        Directory.CreateDirectory(WeatherDir);
        Directory.CreateDirectory(MusicDir);
        Directory.CreateDirectory(AssetsDir);
    }
}
