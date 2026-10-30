using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyStations.Weather.Radar;

/// <summary>A RainViewer frame: when, and the path segment its tiles live under.</summary>
public sealed record RainViewerFrame(DateTime TimeUtc, string Path);

/// <summary>RainViewer's public map index: the tile host and the past frames (oldest first).</summary>
public sealed record RainViewerIndex(string Host, IReadOnlyList<RainViewerFrame> Past);

/// <summary>
/// RainViewer's worldwide radar composite (free for non-commercial use with attribution): a public index lists the
/// last two hours of frames every ten minutes, each as slippy-map tiles that are stitched into the window here.
/// The fallback for places NEXRAD does not cover.
/// </summary>
public sealed class RainViewerSource : IRadarSource
{
    public const string SourceId = "rainviewer";
    public const string IndexUrl = "https://api.rainviewer.com/public/weather-maps.json";

    /// <summary>RainViewer colour scheme 6, "NEXRAD Level III", so both sources read against the same legend.</summary>
    public const int ColorScheme = 6;

    private static readonly TimeSpan IndexTtl = TimeSpan.FromSeconds(90);

    private readonly RadarHttp _http;
    private readonly ILogger<RainViewerSource> _logger;
    private readonly object _lock = new();
    private RainViewerIndex? _index;
    private DateTime _indexFetchedUtc;

    public RainViewerSource(RadarHttp http, ILogger<RainViewerSource> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string Id => SourceId;

    public string Name => "RainViewer";

    public string Attribution => "Radar: RainViewer.com";

    public TimeSpan FrameStep => TimeSpan.FromMinutes(10);

    public bool Covers(double latitude, double longitude) => true;

    public const string DefaultHost = "https://tilecache.rainviewer.com";

    /// <summary>
    /// The index names the tile host. Only an https rainviewer.com address is followed: the index is fetched from a
    /// third party, and must not be able to point this server at an internal address.
    /// </summary>
    public static bool IsRainViewerHost(string? host) =>
        Uri.TryCreate(host, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("rainviewer.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".rainviewer.com", StringComparison.OrdinalIgnoreCase))
        && string.IsNullOrEmpty(uri.UserInfo);

    public static RainViewerIndex ParseIndex(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var host = root.StringOrNull("host") is { } h && IsRainViewerHost(h) ? h : DefaultHost;
        var frames = new List<RainViewerFrame>();
        if (root.TryGetProperty("radar", out var radar) && radar.TryGetProperty("past", out var past) && past.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in past.EnumerateArray())
            {
                if (item.TryGetProperty("time", out var t) && item.StringOrNull("path") is { } path)
                {
                    frames.Add(new RainViewerFrame(DateTime.UnixEpoch.AddSeconds(t.GetInt64()), path));
                }
            }
        }

        frames.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));
        return new RainViewerIndex(host.TrimEnd('/'), frames);
    }

    public static string TileUrl(string host, string path, int zoom, int x, int y)
        => host + path + "/256/" + zoom.ToString(CultureInfo.InvariantCulture) + "/" + x.ToString(CultureInfo.InvariantCulture) + "/" + y.ToString(CultureInfo.InvariantCulture)
           + "/" + ColorScheme.ToString(CultureInfo.InvariantCulture) + "/1_1.png";

    public async Task<IReadOnlyList<DateTime>> FrameTimesAsync(DateTime nowUtc, int count, CancellationToken ct)
    {
        var index = await IndexAsync(ct).ConfigureAwait(false);
        var past = index.Past;
        var start = Math.Max(0, past.Count - count);
        return past.Skip(start).Select(f => f.TimeUtc).ToList();
    }

    public async Task<byte[]> FetchOverlayAsync(RadarViewport viewport, DateTime timeUtc, CancellationToken ct)
    {
        var index = await IndexAsync(ct).ConfigureAwait(false);
        var frame = index.Past.FirstOrDefault(f => f.TimeUtc == timeUtc)
            ?? throw new InvalidOperationException("RainViewer no longer lists the frame for " + timeUtc.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC");

        var (png, _) = await _http.StitchTilesAsync(
            viewport,
            (x, y) => TileUrl(index.Host, frame.Path, viewport.DataZoom, x, y),
            (ex, x, y) => _logger.LogDebug(ex, "JellyStations radar: RainViewer tile {Z}/{X}/{Y} failed", viewport.DataZoom, x, y),
            ct).ConfigureAwait(false);
        return png ?? throw new InvalidOperationException("No RainViewer tile could be fetched");
    }

    private async Task<RainViewerIndex> IndexAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_index is not null && DateTime.UtcNow - _indexFetchedUtc < IndexTtl)
            {
                return _index;
            }
        }

        var json = await _http.GetStringAsync(IndexUrl, ct).ConfigureAwait(false);
        var index = ParseIndex(json);
        lock (_lock)
        {
            _index = index;
            _indexFetchedUtc = DateTime.UtcNow;
        }

        return index;
    }
}
