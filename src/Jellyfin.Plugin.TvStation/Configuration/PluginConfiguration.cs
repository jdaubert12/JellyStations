using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TvStation.Configuration;

/// <summary>
/// Minimal XML-backed configuration. Station data lives in JSON files under the data folder.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the on-disk data schema version, used for future migrations.</summary>
    public int DataSchemaVersion { get; set; } = 1;
}
