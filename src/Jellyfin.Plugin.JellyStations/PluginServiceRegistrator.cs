using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.LiveTv;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Services;
using Jellyfin.Plugin.JellyStations.Storage;
using Jellyfin.Plugin.JellyStations.Streaming;
using Jellyfin.Plugin.JellyStations.Weather;
using Jellyfin.Plugin.JellyStations.Weather.Screens;
using MediaBrowser.Controller;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyStations;

/// <summary>Registers plugin services with the server's DI container.</summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<JellyStationsPaths>();
        serviceCollection.AddSingleton<StationRepository>();
        serviceCollection.AddSingleton<CompiledStationRepository>();
        serviceCollection.AddSingleton<SettingsRepository>();

        serviceCollection.AddSingleton<LibraryContentResolver>();
        serviceCollection.AddSingleton<MusicFolderScanner>();
        serviceCollection.AddSingleton<MusicLoudnessService>();
        serviceCollection.AddSingleton<MusicResolver>();
        serviceCollection.AddSingleton<StationCompiler>();

        serviceCollection.AddHttpClient();

        // Weather and radar talk to fixed https hosts that answer directly. A redirect would be a hijack and an
        // oversized body a fault, so neither is followed into memory; the callers report the 3xx as a failed fetch.
        // The music pack client keeps redirects: its hosts bounce to their file servers, and every download is
        // checked against the catalogue's length and SHA-256 anyway.
        serviceCollection.AddHttpClient("JellyStations.Weather", c => c.MaxResponseContentBufferSize = 4 * 1024 * 1024)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        serviceCollection.AddHttpClient("JellyStations.Radar", c => c.MaxResponseContentBufferSize = 16 * 1024 * 1024)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        serviceCollection.AddSingleton<IWeatherProvider, OpenMeteoProvider>();
        serviceCollection.AddSingleton<WeatherService>();
        serviceCollection.AddSingleton<Weather.Radar.RadarHttp>();
        serviceCollection.AddSingleton<Weather.Radar.IRadarSource, Weather.Radar.NexradSource>();
        serviceCollection.AddSingleton<Weather.Radar.IRadarSource, Weather.Radar.RainViewerSource>();
        serviceCollection.AddSingleton<Weather.Radar.IForecastRadarSource, Weather.Radar.HrrrSource>();
        serviceCollection.AddSingleton<Weather.Radar.RadarService>();
        serviceCollection.AddSingleton<Weather.Alerts.IAlertSource, Weather.Alerts.NwsAlertSource>();
        serviceCollection.AddSingleton<Weather.Alerts.AlertService>();
        serviceCollection.AddSingleton(sp => new WeatherAssets(sp.GetRequiredService<JellyStationsPaths>().AssetsDir));
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
            return new ScheduleResolver(id => ScheduleInput.ForStation(id, stations, compiled));
        });

        serviceCollection.AddSingleton<GuideRefreshService>();
        serviceCollection.AddSingleton<StreamSessionManager>();
        serviceCollection.AddSingleton<StationService>();
        serviceCollection.AddSingleton<StationSuggestionService>();
        serviceCollection.AddSingleton<ILiveTvService, JellyStationsLiveTvService>();
        serviceCollection.AddHostedService<JellyStationsHostedService>();
    }
}
