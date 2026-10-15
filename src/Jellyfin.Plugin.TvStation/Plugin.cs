using Jellyfin.Plugin.TvStation.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TvStation;

/// <summary>
/// TvStation plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The plugin id. Must match build.yaml.</summary>
    public static readonly Guid PluginId = Guid.Parse("b7f3c1d2-5e8a-4f0b-9c6d-2a1e7f4b8d31");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "TvStation";

    public override Guid Id => PluginId;

    public override string Description => "Curate always-on TV stations from your library, surfaced as native Live TV channels.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace;
        return new[]
        {
            new PluginPageInfo
            {
                Name = "TvStation",
                DisplayName = "TV Stations",
                EmbeddedResourcePath = $"{ns}.Web.tvstation.html",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "live_tv"
            },
            new PluginPageInfo
            {
                Name = "TvStationJs",
                EmbeddedResourcePath = $"{ns}.Web.tvstation.js"
            }
        };
    }
}
