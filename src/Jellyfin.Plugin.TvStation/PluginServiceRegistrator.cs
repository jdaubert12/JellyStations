using Jellyfin.Plugin.TvStation.Library;
using Jellyfin.Plugin.TvStation.LiveTv;
using Jellyfin.Plugin.TvStation.Scheduling;
using Jellyfin.Plugin.TvStation.Services;
using Jellyfin.Plugin.TvStation.Storage;
using Jellyfin.Plugin.TvStation.Streaming;
using Jellyfin.Plugin.TvStation.Weather;
using Jellyfin.Plugin.TvStation.Weather.Screens;
using MediaBrowser.Controller;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.TvStation;

/// <summary>Registers plugin services with the server's DI container.</summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<TvStationPaths>();
        serviceCollection.AddSingleton<StationRepository>();
        serviceCollection.AddSingleton<CompiledStationRepository>();
        serviceCollection.AddSingleton<SettingsRepository>();

        serviceCollection.AddSingleton<LibraryContentResolver>();
        serviceCollection.AddSingleton<MusicFolderScanner>();
        serviceCollection.AddSingleton<MusicResolver>();
        serviceCollection.AddSingleton<StationCompiler>();

        serviceCollection.AddHttpClient();
        serviceCollection.AddSingleton<IWeatherProvider, OpenMeteoProvider>();
        serviceCollection.AddSingleton<WeatherService>();
        serviceCollection.AddSingleton(sp => new WeatherAssets(sp.GetRequiredService<TvStationPaths>().AssetsDir));
        serviceCollection.AddSingleton<WeatherPainter>();
        serviceCollection.AddSingleton<WeatherScreenRenderer>();
        serviceCollection.AddSingleton<WeatherChannelService>();
        serviceCollection.AddSingleton<Music.MusicPackService>();
        serviceCollection.AddSingleton<LibrarySearchService>();
        serviceCollection.AddSingleton<StationImportResolver>();
        serviceCollection.AddSingleton<LibraryAnalysisService>();

        serviceCollection.AddSingleton<IScheduleResolver>(sp =>
        {
            var stations = sp.GetRequiredService<StationRepository>();
            var compiled = sp.GetRequiredService<CompiledStationRepository>();
            return new ScheduleResolver(id =>
            {
                var station = stations.Get(id);
                if (station is null)
                {
                    return null;
                }

                var snapshot = compiled.Get(id) ?? new Models.CompiledStation { StationId = id, Revision = station.Revision };
                return ScheduleInput.From(station, snapshot);
            });
        });

        serviceCollection.AddSingleton<GuideRefreshService>();
        serviceCollection.AddSingleton<StreamSessionManager>();
        serviceCollection.AddSingleton<StationService>();
        serviceCollection.AddSingleton<StationSuggestionService>();
        serviceCollection.AddSingleton<ILiveTvService, TvStationLiveTvService>();
        serviceCollection.AddHostedService<TvStationHostedService>();
    }
}
