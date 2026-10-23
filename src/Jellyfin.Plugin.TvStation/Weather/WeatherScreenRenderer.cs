using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.TvStation.Scheduling;
using Jellyfin.Plugin.TvStation.Storage;
using Jellyfin.Plugin.TvStation.Weather.Screens;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvStation.Weather;

/// <summary>
/// Renders weather screens to PNG files and reuses them while nothing they show has changed. The file name is a
/// hash of everything that affects the picture (the reports' fetch times, the hour, the look, the size), so a new
/// report or a new hour yields a new file and the old ones are swept after a while.
/// </summary>
public sealed class WeatherScreenRenderer
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromHours(6);

    private readonly WeatherPainter _painter;
    private readonly TvStationPaths _paths;
    private readonly ILogger<WeatherScreenRenderer> _logger;
    private readonly object _lock = new();
    private DateTime _lastSweepUtc;

    public WeatherScreenRenderer(WeatherPainter painter, TvStationPaths paths, ILogger<WeatherScreenRenderer> logger)
    {
        _painter = painter;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>The PNG for a screen, drawn now if no current file exists.</summary>
    public string Render(Guid stationId, ScreenRequest request)
    {
        var dir = _paths.WeatherScreensDir(stationId);
        var path = Path.Combine(dir, FileName(request));
        lock (_lock)
        {
            if (!File.Exists(path))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _painter.Paint(request, path);
                _logger.LogDebug("TvStation weather: drew {Kind} screen for {Place} in {Ms} ms", request.Kind, request.Place?.Location.Name ?? "all", sw.ElapsedMilliseconds);
            }

            Sweep(dir);
        }

        return path;
    }

    private static readonly string PainterVersion = typeof(WeatherScreenRenderer).Assembly.GetName().Version?.ToString() ?? "0";

    /// <summary>A name that changes whenever the picture would.</summary>
    public static string FileName(ScreenRequest r)
    {
        var sb = new StringBuilder();
        // The plugin version first: an upgrade that changes the layout must not serve screens drawn by the old one.
        sb.Append(PainterVersion).Append('|');
        sb.Append(r.Kind).Append('|').Append(r.Width).Append('x').Append(r.Height).Append('|');
        sb.Append(r.Config.Theme).Append('|').Append(r.Config.Units).Append('|').Append(r.Config.TwentyFourHourClock).Append('|').Append(r.Config.ShowLogo).Append('|');
        sb.Append(r.Attribution).Append('|');
        sb.Append(r.NowUtc.ToString("yyyyMMddHH", CultureInfo.InvariantCulture)).Append('|');
        if (!string.IsNullOrEmpty(r.LogoPath))
        {
            sb.Append(r.LogoPath);
            try
            {
                sb.Append('@').Append(File.GetLastWriteTimeUtc(r.LogoPath).Ticks);
            }
            catch (Exception)
            {
                // Missing logo: drawn without.
            }

            sb.Append('|');
        }

        void Place(PlaceWeather p)
        {
            sb.Append(p.Location.Id.ToString("N")).Append(':').Append(p.Location.Name).Append(':').Append(p.Location.Region).Append(':');
            sb.Append(p.Report?.FetchedUtc.Ticks ?? 0).Append(':').Append(p.Stale ? 1 : 0).Append(';');
        }

        if (r.Place is not null)
        {
            Place(r.Place);
        }

        if (r.Kind == WeatherScreenKind.Travel)
        {
            foreach (var p in r.All)
            {
                Place(p);
            }
        }

        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();
        var who = r.Kind == WeatherScreenKind.Travel ? "travel" : r.Place?.Location.Id.ToString("N")[..8] ?? "none";
        return who + "-" + r.Kind.ToString().ToLowerInvariant() + "-" + hash + ".png";
    }

    private void Sweep(string dir)
    {
        var now = DateTime.UtcNow;
        if (now - _lastSweepUtc < TimeSpan.FromMinutes(10))
        {
            return;
        }

        _lastSweepUtc = now;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.png"))
            {
                if (now - File.GetLastWriteTimeUtc(file) > KeepFor)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TvStation weather: sweep of {Dir} failed", dir);
        }
    }
}
