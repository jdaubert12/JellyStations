using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace Jellyfin.Plugin.JellyStations.Weather.Screens;

/// <summary>A coastline, lake shore or border: points in degrees with their bounding box, for a quick "is any of it in view".</summary>
public sealed class MapPolyline
{
    public MapPolyline(float[] longitudes, float[] latitudes)
    {
        Longitudes = longitudes;
        Latitudes = latitudes;
        LonMin = LatMin = float.MaxValue;
        LonMax = LatMax = float.MinValue;
        for (var i = 0; i < longitudes.Length; i++)
        {
            LonMin = Math.Min(LonMin, longitudes[i]);
            LonMax = Math.Max(LonMax, longitudes[i]);
            LatMin = Math.Min(LatMin, latitudes[i]);
            LatMax = Math.Max(LatMax, latitudes[i]);
        }
    }

    public float[] Longitudes { get; }

    public float[] Latitudes { get; }

    public float LonMin { get; }

    public float LatMin { get; }

    public float LonMax { get; }

    public float LatMax { get; }

    public int Count => Longitudes.Length;

    public bool Intersects((double LonMin, double LatMin, double LonMax, double LatMax) b)
        => LonMax >= b.LonMin && LonMin <= b.LonMax && LatMax >= b.LatMin && LatMin <= b.LatMax;
}

/// <summary>A town or city worth a label; rank 0 is the most important.</summary>
public sealed record MapPlace(string Name, double Latitude, double Longitude, int Rank, long Population);

/// <summary>
/// The map under the radar: Natural Earth 1:10m land, lakes, country and state borders and populated places
/// (public domain), simplified and packed into the plugin as a small deflated binary (see dev/basemap for the
/// build script). Loaded once on first use.
/// </summary>
public sealed class BaseMap
{
    private const string ResourceName = "Jellyfin.Plugin.JellyStations.Assets.basemap.bin";
    private static readonly Lazy<BaseMap> Embedded = new(LoadEmbedded, LazyThreadSafetyMode.ExecutionAndPublication);

    private BaseMap(IReadOnlyList<MapPolyline> land, IReadOnlyList<MapPolyline> lakes, IReadOnlyList<MapPolyline> countryBorders, IReadOnlyList<MapPolyline> stateBorders, IReadOnlyList<MapPlace> places)
    {
        Land = land;
        Lakes = lakes;
        CountryBorders = countryBorders;
        StateBorders = stateBorders;
        Places = places;
    }

    public static BaseMap Instance => Embedded.Value;

    /// <summary>Closed rings; outer coasts and holes alike, drawn with even-odd fill.</summary>
    public IReadOnlyList<MapPolyline> Land { get; }

    public IReadOnlyList<MapPolyline> Lakes { get; }

    public IReadOnlyList<MapPolyline> CountryBorders { get; }

    public IReadOnlyList<MapPolyline> StateBorders { get; }

    public IReadOnlyList<MapPlace> Places { get; }

    public static IEnumerable<MapPolyline> Within(IReadOnlyList<MapPolyline> layer, (double LonMin, double LatMin, double LonMax, double LatMax) bounds)
        => layer.Where(p => p.Intersects(bounds));

    public IEnumerable<MapPlace> PlacesWithin((double LonMin, double LatMin, double LonMax, double LatMax) b)
        => Places.Where(p => p.Longitude >= b.LonMin && p.Longitude <= b.LonMax && p.Latitude >= b.LatMin && p.Latitude <= b.LatMax);

    private static BaseMap LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new FileNotFoundException("Embedded base map missing: " + ResourceName);
        return Load(stream);
    }

    /// <summary>Reads the deflated binary: magic, then layers of delta-coded polylines (ids 1-4) and the places (id 5).</summary>
    public static BaseMap Load(Stream deflated)
    {
        using var inflate = new DeflateStream(deflated, CompressionMode.Decompress);
        using var buffer = new MemoryStream();
        inflate.CopyTo(buffer);
        var data = buffer.GetBuffer();
        var length = (int)buffer.Length;
        var pos = 0;
        if (length < 6 || Encoding.ASCII.GetString(data, 0, 6) != "JSMAP2")
        {
            throw new InvalidDataException("Not a JellyStations base map");
        }

        pos = 6;
        var layers = new Dictionary<int, List<MapPolyline>>();
        var places = new List<MapPlace>();
        while (pos < length)
        {
            var id = (int)ReadVarint(data, ref pos);
            var count = (int)ReadVarint(data, ref pos);
            if (id == 5)
            {
                for (var i = 0; i < count; i++)
                {
                    var nameLength = (int)ReadVarint(data, ref pos);
                    var name = Encoding.UTF8.GetString(data, pos, nameLength);
                    pos += nameLength;
                    var lat = BitConverter.ToInt32(data, pos) / 1000.0;
                    var lon = BitConverter.ToInt32(data, pos + 4) / 1000.0;
                    pos += 8;
                    var rank = data[pos++];
                    var pop = (long)ReadVarint(data, ref pos);
                    places.Add(new MapPlace(name, lat, lon, rank, pop));
                }

                continue;
            }

            var list = new List<MapPolyline>(count);
            for (var i = 0; i < count; i++)
            {
                var n = (int)ReadVarint(data, ref pos);
                var lons = new float[n];
                var lats = new float[n];
                long x = 0, y = 0;
                for (var k = 0; k < n; k++)
                {
                    x += ReadZigzag(data, ref pos);
                    y += ReadZigzag(data, ref pos);
                    lons[k] = x / 1000f;
                    lats[k] = y / 1000f;
                }

                list.Add(new MapPolyline(lons, lats));
            }

            layers[id] = list;
        }

        IReadOnlyList<MapPolyline> Layer(int id) => layers.TryGetValue(id, out var l) ? l : Array.Empty<MapPolyline>();
        return new BaseMap(Layer(1), Layer(2), Layer(3), Layer(4), places);
    }

    private static ulong ReadVarint(byte[] data, ref int pos)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            var b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }
    }

    private static long ReadZigzag(byte[] data, ref int pos)
    {
        var u = ReadVarint(data, ref pos);
        return (long)(u >> 1) ^ -(long)(u & 1);
    }
}
