using System.Globalization;

namespace Jellyfin.Plugin.JellyStations.Weather.Radar;

/// <summary>
/// NOAA NEXRAD base reflectivity for the contiguous United States, from the Iowa Environmental Mesonet's
/// time-enabled WMS (one request per frame, any window, absolute times every five minutes, kept for years).
/// </summary>
public sealed class NexradSource : IRadarSource
{
    public const string SourceId = "nexrad";
    private const string Endpoint = "https://mesonet.agron.iastate.edu/cgi-bin/wms/nexrad/n0q-t.cgi";

    /// <summary>The composite for a time is published a few minutes after it; frames newer than this are not asked for.</summary>
    public static readonly TimeSpan Lag = TimeSpan.FromMinutes(4);

    private readonly RadarHttp _http;

    public NexradSource(RadarHttp http)
    {
        _http = http;
    }

    public string Id => SourceId;

    public string Name => "NOAA NEXRAD via Iowa Environmental Mesonet";

    public string Attribution => "Radar: NOAA NEXRAD, Iowa Environmental Mesonet";

    public TimeSpan FrameStep => TimeSpan.FromMinutes(5);

    /// <summary>The contiguous United States and a little sea around it; Alaska and Hawaii fall through to the next source.</summary>
    public bool Covers(double latitude, double longitude) => latitude is >= 23 and <= 50.5 && longitude is >= -126 and <= -64;

    /// <summary>The newest frame time that should exist now: five-minute steps, <see cref="Lag"/> behind the clock.</summary>
    public static DateTime NewestFrame(DateTime nowUtc)
    {
        var t = nowUtc - Lag;
        var ticks = t.Ticks - (t.Ticks % TimeSpan.FromMinutes(5).Ticks);
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    public Task<IReadOnlyList<DateTime>> FrameTimesAsync(DateTime nowUtc, int count, CancellationToken ct)
    {
        var newest = NewestFrame(nowUtc);
        var list = new List<DateTime>(count);
        for (var i = count - 1; i >= 0; i--)
        {
            list.Add(newest - TimeSpan.FromTicks(FrameStep.Ticks * i));
        }

        return Task.FromResult<IReadOnlyList<DateTime>>(list);
    }

    public static string BuildUrl(RadarViewport viewport, DateTime timeUtc)
    {
        var (minX, minY, maxX, maxY) = viewport.MercatorBounds();
        string M(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        return Endpoint + "?SERVICE=WMS&VERSION=1.1.1&REQUEST=GetMap&LAYERS=nexrad-n0q-wmst&STYLES=&SRS=EPSG:900913"
            + "&BBOX=" + M(minX) + "," + M(minY) + "," + M(maxX) + "," + M(maxY)
            + "&WIDTH=" + viewport.DataWidth.ToString(CultureInfo.InvariantCulture)
            + "&HEIGHT=" + viewport.DataHeight.ToString(CultureInfo.InvariantCulture)
            + "&FORMAT=image/png&TRANSPARENT=true&TIME=" + timeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    public Task<byte[]> FetchOverlayAsync(RadarViewport viewport, DateTime timeUtc, CancellationToken ct)
        => _http.GetPngAsync(BuildUrl(viewport, timeUtc), ct);
}
