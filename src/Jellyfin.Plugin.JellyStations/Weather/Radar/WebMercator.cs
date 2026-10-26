using Jellyfin.Plugin.JellyStations.Models;

namespace Jellyfin.Plugin.JellyStations.Weather.Radar;

/// <summary>
/// The Web Mercator maths every radar source and the map painter share: the "world pixel" grid of slippy map
/// tiles (256 px tiles, zoom doubling) and the EPSG:900913 metres a WMS request wants.
/// </summary>
public static class WebMercator
{
    public const double EarthRadius = 6378137.0;
    public const int TileSize = 256;
    private const double MaxLatitude = 85.05112878;

    public static double WorldSize(int zoom) => TileSize * (double)(1L << zoom);

    /// <summary>Pixel position on the world image at a zoom level (x east, y south).</summary>
    public static (double X, double Y) ToWorld(double latitude, double longitude, int zoom)
    {
        var size = WorldSize(zoom);
        var x = (longitude + 180.0) / 360.0 * size;
        var lat = Math.Clamp(latitude, -MaxLatitude, MaxLatitude) * Math.PI / 180.0;
        var y = (1.0 - (Math.Log(Math.Tan(lat) + (1.0 / Math.Cos(lat))) / Math.PI)) / 2.0 * size;
        return (x, y);
    }

    /// <summary>The inverse of <see cref="ToWorld"/>.</summary>
    public static (double Latitude, double Longitude) FromWorld(double x, double y, int zoom)
    {
        var size = WorldSize(zoom);
        var longitude = (x / size * 360.0) - 180.0;
        var n = Math.PI - (2.0 * Math.PI * y / size);
        var latitude = 180.0 / Math.PI * Math.Atan(0.5 * (Math.Exp(n) - Math.Exp(-n)));
        return (latitude, longitude);
    }

    /// <summary>EPSG:900913 / 3857 metres.</summary>
    public static (double X, double Y) ToMeters(double latitude, double longitude)
    {
        var x = EarthRadius * longitude * Math.PI / 180.0;
        var lat = Math.Clamp(latitude, -MaxLatitude, MaxLatitude) * Math.PI / 180.0;
        var y = EarthRadius * Math.Log(Math.Tan((Math.PI / 4.0) + (lat / 2.0)));
        return (x, y);
    }

    /// <summary>Projected metres per world pixel at a zoom level (the same everywhere on the Mercator plane).</summary>
    public static double MetersPerPixel(int zoom) => 2.0 * Math.PI * EarthRadius / WorldSize(zoom);

    /// <summary>The slippy-map zoom the radar screen is drawn at for a configured map area.</summary>
    public static int ZoomOf(RadarZoom zoom) => zoom switch
    {
        RadarZoom.Metro => 9,
        RadarZoom.Regional => 7,
        RadarZoom.Wide => 6,
        _ => 8
    };
}

/// <summary>
/// The map window of a radar screen: a place at its centre, a zoom, and a size in screen pixels (the painter's
/// 1080 grid). Radar imagery is fetched one zoom level down at half the pixel size (<see cref="DataWidth"/> by
/// <see cref="DataHeight"/>) and scaled up when drawn; NEXRAD and RainViewer data are coarser than that anyway.
/// </summary>
public sealed record RadarViewport(double Latitude, double Longitude, int Zoom, int Width, int Height)
{
    /// <summary>The map area of the radar screen in 1080-grid pixels; the painter clips it to what fits the picture.</summary>
    public const int MapWidth = 1760;
    public const int MapHeight = 760;

    public static RadarViewport For(WeatherLocation location, int zoom) => new(location.Latitude, location.Longitude, zoom, MapWidth, MapHeight);

    public int DataZoom => Zoom - 1;

    public int DataWidth => Width / 2;

    public int DataHeight => Height / 2;

    /// <summary>Where a coordinate lands in the window (0,0 top-left; may fall outside).</summary>
    public (float X, float Y) Project(double latitude, double longitude)
    {
        var (cx, cy) = WebMercator.ToWorld(Latitude, Longitude, Zoom);
        var (x, y) = WebMercator.ToWorld(latitude, longitude, Zoom);
        return ((float)(x - cx + (Width / 2.0)), (float)(y - cy + (Height / 2.0)));
    }

    /// <summary>The window in degrees, grown by <paramref name="margin"/> of its size on every side, for picking what to draw.</summary>
    public (double LonMin, double LatMin, double LonMax, double LatMax) Bounds(double margin = 0.1)
    {
        var (cx, cy) = WebMercator.ToWorld(Latitude, Longitude, Zoom);
        var hw = Width * (0.5 + margin);
        var hh = Height * (0.5 + margin);
        var (latMax, lonMin) = WebMercator.FromWorld(cx - hw, cy - hh, Zoom);
        var (latMin, lonMax) = WebMercator.FromWorld(cx + hw, cy + hh, Zoom);
        return (lonMin, latMin, lonMax, latMax);
    }

    /// <summary>The window as an EPSG:900913 bounding box (min x, min y, max x, max y) for a WMS request.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) MercatorBounds()
    {
        var (cx, cy) = WebMercator.ToMeters(Latitude, Longitude);
        var mpp = WebMercator.MetersPerPixel(Zoom);
        var hw = Width / 2.0 * mpp;
        var hh = Height / 2.0 * mpp;
        return (cx - hw, cy - hh, cx + hw, cy + hh);
    }

    /// <summary>The tiles at <see cref="DataZoom"/> that cover the window and where each one's top-left lands in the data image.</summary>
    public IEnumerable<(int X, int Y, int OffsetX, int OffsetY)> Tiles()
    {
        var (cx, cy) = WebMercator.ToWorld(Latitude, Longitude, DataZoom);
        var left = cx - (DataWidth / 2.0);
        var top = cy - (DataHeight / 2.0);
        var n = 1 << DataZoom;
        var x0 = (int)Math.Floor(left / WebMercator.TileSize);
        var x1 = (int)Math.Floor((left + DataWidth - 1) / WebMercator.TileSize);
        var y0 = (int)Math.Floor(top / WebMercator.TileSize);
        var y1 = (int)Math.Floor((top + DataHeight - 1) / WebMercator.TileSize);
        for (var y = y0; y <= y1; y++)
        {
            if (y < 0 || y >= n)
            {
                continue;
            }

            for (var x = x0; x <= x1; x++)
            {
                var wrapped = ((x % n) + n) % n;
                yield return (wrapped, y, (int)Math.Round((x * WebMercator.TileSize) - left), (int)Math.Round((y * WebMercator.TileSize) - top));
            }
        }
    }
}
