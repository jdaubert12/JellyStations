using System.Net.Http.Headers;
using System.Reflection;
using Microsoft.Extensions.Logging;

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

    public async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        var bytes = await GetBytesAsync(url, "application/json", ct).ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private async Task<byte[]> GetBytesAsync(string url, string accept, CancellationToken ct)
    {
        using var client = _http.CreateClient("JellyStations.Radar");
        client.Timeout = TimeSpan.FromSeconds(40);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Jellyfin-JellyStations", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/jdaubert12/JellyStations)"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"The radar server answered {(int)response.StatusCode} {response.ReasonPhrase}".Trim(), null, response.StatusCode);
        }

        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }
}
