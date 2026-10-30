using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyStations.Weather.Radar;

/// <summary>A source of forecast radar: frames are forecast minutes of a model run rather than past clock times.</summary>
public interface IForecastRadarSource
{
    string Id { get; }

    string Name { get; }

    string Attribution { get; }

    /// <summary>The furthest forecast minute the model offers.</summary>
    int MaxMinutes { get; }

    bool Covers(double latitude, double longitude);

    /// <summary>The newest model run published now (UTC, on the hour), or null when none of the recent hours is up yet.</summary>
    Task<DateTime?> LatestRunAsync(DateTime nowUtc, CancellationToken ct);

    /// <summary>A transparent PNG of the window for one forecast minute of a run.</summary>
    Task<byte[]> FetchOverlayAsync(RadarViewport viewport, DateTime runUtc, int forecastMinute, CancellationToken ct);
}

/// <summary>
/// NOAA's HRRR model (hourly runs, 15-minute forecast reflectivity out to 18 hours) as the Iowa Environmental
/// Mesonet serves it: slippy-map tiles named by run and forecast minute. The tile service answers 503 for a run
/// it does not have yet, which is how the newest run is found. Contiguous United States only.
/// </summary>
public sealed class HrrrSource : IForecastRadarSource
{
    public const string SourceId = "hrrr";
    private const string TileBase = "https://mesonet.agron.iastate.edu/cache/tile.py/1.0.0/";

    /// <summary>A run is usually published about 50 minutes after its hour; this is how many hours back the probe looks.</summary>
    public const int ProbeHours = 4;

    private static readonly TimeSpan RunCacheTtl = TimeSpan.FromMinutes(10);

    private readonly RadarHttp _http;
    private readonly ILogger<HrrrSource> _logger;
    private readonly object _lock = new();
    private DateTime? _run;
    private DateTime _runCheckedUtc;

    public HrrrSource(RadarHttp http, ILogger<HrrrSource> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string Id => SourceId;

    public string Name => "NOAA HRRR via Iowa Environmental Mesonet";

    public string Attribution => "Forecast radar: NOAA HRRR, Iowa Environmental Mesonet";

    public int MaxMinutes => 1080;

    /// <summary>The HRRR grid: the contiguous United States and its margins.</summary>
    public bool Covers(double latitude, double longitude) => latitude is >= 21 and <= 52 && longitude is >= -134 and <= -60;

    public static string LayerName(DateTime runUtc, int forecastMinute)
        => "hrrr::REFD-F" + forecastMinute.ToString("0000", CultureInfo.InvariantCulture) + "-" + runUtc.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);

    public static string TileUrl(DateTime runUtc, int forecastMinute, int zoom, int x, int y)
        => TileBase + LayerName(runUtc, forecastMinute) + "/" + zoom.ToString(CultureInfo.InvariantCulture) + "/" + x.ToString(CultureInfo.InvariantCulture) + "/" + y.ToString(CultureInfo.InvariantCulture) + ".png";

    /// <summary>The run hours worth probing, newest first: the current hour and the few before it.</summary>
    public static IEnumerable<DateTime> RunCandidates(DateTime nowUtc)
    {
        var hour = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i <= ProbeHours; i++)
        {
            yield return hour.AddHours(-i);
        }
    }

    public async Task<DateTime?> LatestRunAsync(DateTime nowUtc, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_run is not null && nowUtc - _runCheckedUtc < RunCacheTtl)
            {
                return _run;
            }
        }

        DateTime? found = null;
        foreach (var run in RunCandidates(nowUtc))
        {
            // A low-zoom tile over the eastern half of the country: 200 when the run is up, 503 when it is not.
            if (await ExistsAsync(TileUrl(run, 0, 2, 1, 1), ct).ConfigureAwait(false))
            {
                found = run;
                break;
            }
        }

        lock (_lock)
        {
            if (found is not null)
            {
                _run = found;
            }

            _runCheckedUtc = nowUtc;
            return _run;
        }
    }

    public async Task<byte[]> FetchOverlayAsync(RadarViewport viewport, DateTime runUtc, int forecastMinute, CancellationToken ct)
    {
        var (png, lastError) = await _http.StitchTilesAsync(
            viewport,
            (x, y) => TileUrl(runUtc, forecastMinute, viewport.DataZoom, x, y),
            (ex, x, y) => _logger.LogDebug(ex, "JellyStations radar: HRRR tile {Layer} {Z}/{X}/{Y} failed", LayerName(runUtc, forecastMinute), viewport.DataZoom, x, y),
            ct).ConfigureAwait(false);
        return png ?? throw new InvalidOperationException("No HRRR tile could be fetched for forecast minute " + forecastMinute + (lastError is null ? string.Empty : ": " + lastError.Message));
    }

    private async Task<bool> ExistsAsync(string url, CancellationToken ct)
    {
        try
        {
            await _http.GetPngAsync(url, ct).ConfigureAwait(false);
            return true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable || ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
