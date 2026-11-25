using System.Globalization;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.JellyStations.Weather.Radar;

/// <summary>
/// NOAA's MRMS seamless low-level reflectivity for the contiguous United States, as the Iowa Environmental Mesonet
/// serves it: slippy-map tiles named by absolute time, every two minutes. Every NEXRAD site feeds it, but unlike the
/// raw NEXRAD composite (which 1.18 to 1.38 showed) it is quality controlled: ground clutter, birds and insects, wind
/// farms and sun spikes, which speckle the raw composite around every radar on a dry day, are gone. The tile service
/// draws it in its own colours, so they are swapped for the NEXRAD ramp the legend and the HRRR forecast use.
/// </summary>
public sealed class MrmsSource : IRadarSource
{
    public const string SourceId = "mrms";
    private const string TileBase = "https://mesonet.agron.iastate.edu/cache/tile.py/1.0.0/";

    /// <summary>The mosaic for a time is published about five minutes after it; frames newer than this are not asked for.</summary>
    public static readonly TimeSpan Lag = TimeSpan.FromMinutes(6);

    /// <summary>A multiple of the mosaic's two minutes that divides the hour, so every frame time exists and stays on the same grid.</summary>
    public static readonly TimeSpan Step = TimeSpan.FromMinutes(6);

    private readonly RadarHttp _http;
    private readonly ILogger<MrmsSource> _logger;

    public MrmsSource(RadarHttp http, ILogger<MrmsSource> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string Id => SourceId;

    public string Name => "NOAA MRMS via Iowa Environmental Mesonet";

    public string Attribution => "Radar: NOAA MRMS, Iowa Environmental Mesonet";

    public TimeSpan FrameStep => Step;

    /// <summary>The contiguous United States and a little sea around it; Alaska and Hawaii fall through to the next source.</summary>
    public bool Covers(double latitude, double longitude) => latitude is >= 23 and <= 50.5 && longitude is >= -126 and <= -64;

    /// <summary>The newest frame time that should exist now: on the <see cref="Step"/> grid, <see cref="Lag"/> behind the clock.</summary>
    public static DateTime NewestFrame(DateTime nowUtc)
    {
        var t = nowUtc - Lag;
        return new DateTime(t.Ticks - (t.Ticks % Step.Ticks), DateTimeKind.Utc);
    }

    public Task<IReadOnlyList<DateTime>> FrameTimesAsync(DateTime nowUtc, int count, CancellationToken ct)
    {
        var newest = NewestFrame(nowUtc);
        var list = new List<DateTime>(count);
        for (var i = count - 1; i >= 0; i--)
        {
            list.Add(newest - TimeSpan.FromTicks(Step.Ticks * i));
        }

        return Task.FromResult<IReadOnlyList<DateTime>>(list);
    }

    public static string TileUrl(DateTime timeUtc, int zoom, int x, int y)
        => TileBase + "mrms::lcref-" + timeUtc.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)
           + "/" + zoom.ToString(CultureInfo.InvariantCulture) + "/" + x.ToString(CultureInfo.InvariantCulture) + "/" + y.ToString(CultureInfo.InvariantCulture) + ".png";

    public async Task<byte[]> FetchOverlayAsync(RadarViewport viewport, DateTime timeUtc, CancellationToken ct)
    {
        var (png, lastError) = await _http.StitchTilesAsync(
            viewport,
            (x, y) => TileUrl(timeUtc, viewport.DataZoom, x, y),
            (ex, x, y) => _logger.LogDebug(ex, "JellyStations radar: MRMS tile {Time} {Z}/{X}/{Y} failed", timeUtc, viewport.DataZoom, x, y),
            ct,
            Recolour).ConfigureAwait(false);

        // A frame is cached for the whole loop, so one with a hole in it is refused and asked for again next time.
        if (png is null || lastError is not null)
        {
            throw new InvalidOperationException("An MRMS tile could not be fetched for " + timeUtc.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC" + (lastError is null ? string.Empty : ": " + lastError.Message), lastError);
        }

        return png;
    }

    /// <summary>Repaints the tile service's MRMS colours in the NEXRAD colours of the same reflectivity; anything else is left as it is.</summary>
    public static void Recolour(SKBitmap bitmap)
    {
        var pixels = bitmap.Pixels;
        for (var i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            if (p.Alpha == 0xFF && Ramp.TryGetValue((uint)p & 0xFFFFFF, out var rgb))
            {
                pixels[i] = new SKColor(0xFF000000 | rgb);
            }
        }

        bitmap.Pixels = pixels;
    }

    // Both ramps are the Mesonet's published lookup tables (GIS/rasters.php, mrms_lcref and composite_n0q),
    // 11 dBZ to 80 dBZ in half-dBZ steps. MRMS is transparent below that.
    private static readonly uint[] MrmsRamp =
    {
        0xA4A4FF, 0xA1A1FC, 0x9E9EF9, 0x9A9AF6, 0x9797F2, 0x9494EF, 0x9191EC, 0x8E8EE9, 0x8A8AE6, 0x8787E3,
        0x8484E0, 0x8181DC, 0x7E7ED9, 0x7A7AD6, 0x7777D3, 0x7474D0, 0x7171CD, 0x6E6EC9, 0x6A6AC6, 0x6767C3,
        0x4080FF, 0x3E7DF9, 0x3D7AF2, 0x3B76EC, 0x3A73E6, 0x3870DF, 0x366DD9, 0x356AD3, 0x3366CC, 0x3263C6,
        0x3060C0, 0x2E5DB9, 0x2D5AB3, 0x2B56AC, 0x2A53A6, 0x2850A0, 0x264D99, 0x254A93, 0x23468D, 0x224386,
        0x204080, 0x00F900, 0x00F200, 0x00EC00, 0x00E600, 0x00DF00, 0x00D900, 0x00D300, 0x00CC00, 0x00C600,
        0x00C000, 0x00B900, 0x00B300, 0x00AC00, 0x00A600, 0x00A000, 0x009900, 0x009300, 0x008D00, 0x008600,
        0x008000, 0xFFF900, 0xFFF200, 0xFFEC00, 0xFFE600, 0xFFDF00, 0xFFD900, 0xFFD300, 0xFFCC00, 0xFFC600,
        0xFFC000, 0xFFB900, 0xFFB300, 0xFFAC00, 0xFFA600, 0xFFA000, 0xFF9900, 0xFF9300, 0xFF8D00, 0xFF8600,
        0xFF0000, 0xFA0000, 0xF50000, 0xF10000, 0xEC0000, 0xE70000, 0xE30000, 0xDE0000, 0xD90000, 0xD40000,
        0xCF0000, 0xCB0000, 0xC60000, 0xC10000, 0xBD0000, 0xB80000, 0xB30000, 0xAE0000, 0xAA0000, 0xA50000,
        0xFF00FF, 0xF900F9, 0xF200F2, 0xEC00EC, 0xE600E6, 0xDF00DF, 0xD900D9, 0xD300D3, 0xCC00CC, 0xC600C6,
        0xC000C0, 0xB900B9, 0xB300B3, 0xAC00AC, 0xA600A6, 0xA000A0, 0x990099, 0x930093, 0x8D008D, 0x860086,
        0xFFFFFF, 0xF9F9F9, 0xF2F2F2, 0xECECEC, 0xE6E6E6, 0xDFDFDF, 0xD9D9D9, 0xD3D3D3, 0xCCCCCC, 0xC6C6C6,
        0xC0C0C0, 0xB9B9B9, 0xB3B3B3, 0xACACAC, 0xA6A6A6, 0xA0A0A0, 0x999999, 0x939393, 0x8D8D8D
    };

    private static readonly uint[] NexradRamp =
    {
        0x4A76AE, 0x4D7DB2, 0x4F84B6, 0x518BBB, 0x5699C3, 0x599FC7, 0x5BA6CB, 0x5EADCF, 0x60B4D4, 0x62BBD8,
        0x65C2DC, 0x67C9E0, 0x6AD0E4, 0x6FD6E8, 0x68D6D7, 0x59D6B3, 0x52D6A2, 0x4BD690, 0x43D67E, 0x3CD66D,
        0x35D65B, 0x11D518, 0x11D117, 0x10CD17, 0x10C816, 0x10C416, 0x0FBC15, 0x0FB714, 0x0EB314, 0x0EAF13,
        0x0EAB13, 0x0DA612, 0x0DA212, 0x0D9E11, 0x0C9911, 0x0C9510, 0x0C9110, 0x0B880F, 0x0B840E, 0x0A800E,
        0x0A7C0D, 0x0A770D, 0x09730C, 0x096F0C, 0x096B0B, 0x08660B, 0x08620A, 0x095E09, 0x327308, 0x467D08,
        0x5B8807, 0x6F9207, 0x849D06, 0x98A806, 0xADB205, 0xC1BD05, 0xD6C704, 0xEAD204, 0xFFE200, 0xFFD800,
        0xFFD300, 0xFFCE00, 0xFFC900, 0xFFC400, 0xFFC000, 0xFFBB00, 0xFFB600, 0xFFB100, 0xFFAC00, 0xFFA700,
        0xFFA200, 0xFF9900, 0xFF9400, 0xFF8F00, 0xFF8A00, 0xFF8500, 0xFF8000, 0xFF0000, 0xF80000, 0xF10000,
        0xEA0000, 0xE30000, 0xD50000, 0xCD0000, 0xC60000, 0xBF0000, 0xB80000, 0xB10000, 0xAA0000, 0xA30000,
        0x9B0000, 0x940000, 0x8D0000, 0x7F0000, 0x780000, 0x710000, 0xFFFFFF, 0xFFF5FF, 0xFFEAFF, 0xFFDFFF,
        0xFFD4FF, 0xFFC9FF, 0xFFBEFF, 0xFFB3FF, 0xFF9DFF, 0xFF92FF, 0xFF75FF, 0xFC6BFD, 0xF960FA, 0xF656F7,
        0xF34BF4, 0xF040F1, 0xED36EF, 0xEA2BEC, 0xE720E9, 0xE10BE3, 0xB200FF, 0xAC00FC, 0xA400F7, 0x9B00F4,
        0x9300EF, 0x8800EA, 0x8300E8, 0x7900E2, 0x7200DD, 0x6900DB, 0x05ECF0, 0x05EBF0, 0x05EAF0, 0x05DDE0,
        0x05DCE0, 0x05DBE0, 0x05CDD0, 0x05CCD0, 0x04BDC0, 0x04BCC0, 0x04BBC0, 0x04AEB0, 0x04ADB0
    };

    private static readonly Dictionary<uint, uint> Ramp = MrmsRamp.Zip(NexradRamp).ToDictionary(p => p.First, p => p.Second);
}
