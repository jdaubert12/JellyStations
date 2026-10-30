using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.JellyStations.Weather.Radar;

/// <summary>A place where precipitation radar imagery comes from. Sources are tried in registration order; the first that covers a place wins.</summary>
public interface IRadarSource
{
    /// <summary>Short stable id ("nexrad", "rainviewer"), part of the cached frame file names.</summary>
    string Id { get; }

    /// <summary>What the screen calls it.</summary>
    string Name { get; }

    /// <summary>The credit line the source asks for.</summary>
    string Attribution { get; }

    /// <summary>Time between frames.</summary>
    TimeSpan FrameStep { get; }

    bool Covers(double latitude, double longitude);

    /// <summary>The times (UTC, oldest first) of the newest <paramref name="count"/> frames available now.</summary>
    Task<IReadOnlyList<DateTime>> FrameTimesAsync(DateTime nowUtc, int count, CancellationToken ct);

    /// <summary>A transparent PNG of the window's <see cref="RadarViewport.DataWidth"/> by <see cref="RadarViewport.DataHeight"/> pixels for one frame time.</summary>
    Task<byte[]> FetchOverlayAsync(RadarViewport viewport, DateTime timeUtc, CancellationToken ct);
}

/// <summary>HTTP for the radar sources: identifies the plugin, short timeouts, and checks that an image came back rather than an error page.</summary>
public sealed class RadarHttp
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47 };
    private readonly IHttpClientFactory _http;
    private readonly ILogger<RadarHttp> _logger;

    public RadarHttp(IHttpClientFactory http, ILogger<RadarHttp> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<byte[]> GetPngAsync(string url, CancellationToken ct)
    {
        var bytes = await GetBytesAsync(url, "image/png", ct).ConfigureAwait(false);
        if (bytes.Length < 8 || !bytes.AsSpan(0, 4).SequenceEqual(PngSignature))
        {
            var text = bytes.Length > 0 ? System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 200)) : string.Empty;
            _logger.LogDebug("JellyStations radar: {Url} did not return a PNG: {Head}", url, text);
            throw new InvalidDataException("The radar server did not return an image");
        }

        return bytes;
    }

    /// <summary>
    /// Fetches the window's slippy-map tiles and draws them onto one transparent PNG of the window. A tile that fails
    /// is reported to <paramref name="onTileFailed"/> and left blank; the PNG is null only when every tile failed.
    /// </summary>
    public async Task<(byte[]? Png, Exception? LastError)> StitchTilesAsync(RadarViewport viewport, Func<int, int, string> tileUrl, Action<Exception, int, int> onTileFailed, CancellationToken ct)
    {
        using var bitmap = new SKBitmap(viewport.DataWidth, viewport.DataHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        var failed = 0;
        var total = 0;
        Exception? lastError = null;
        foreach (var (x, y, ox, oy) in viewport.Tiles())
        {
            total++;
            try
            {
                var png = await GetPngAsync(tileUrl(x, y), ct).ConfigureAwait(false);
                using var tile = SKBitmap.Decode(png);
                if (tile is not null)
                {
                    canvas.DrawBitmap(tile, ox, oy);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                lastError = ex;
                onTileFailed(ex, x, y);
            }
        }

        if (total > 0 && failed == total)
        {
            return (null, lastError);
        }

        canvas.Flush();
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return (data.ToArray(), lastError);
    }

    public async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        var bytes = await GetBytesAsync(url, "application/json", ct).ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private async Task<byte[]> GetBytesAsync(string url, string accept, CancellationToken ct)
    {
        using var client = _http.CreateClient("JellyStations.Radar");
        client.Timeout = TimeSpan.FromSeconds(40);
        using var request = WeatherHttp.CreateGet(url, accept);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"The radar server answered {(int)response.StatusCode} {response.ReasonPhrase}".Trim(), null, response.StatusCode);
        }

        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }
}
