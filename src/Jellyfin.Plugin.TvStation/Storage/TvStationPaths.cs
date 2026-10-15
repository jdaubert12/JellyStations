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
        SettingsFile = Path.Combine(Root, "settings.json");
    }

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
    }
}
