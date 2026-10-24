using System.Reflection;
using SkiaSharp;

namespace Jellyfin.Plugin.JellyStations.Weather.Screens;

/// <summary>Font weight of the Inter files shipped inside the plugin.</summary>
public enum FontWeight
{
    Regular,
    SemiBold,
    Bold
}

/// <summary>
/// The Inter font (SIL Open Font License) embedded in the plugin: as SkiaSharp typefaces for the screens, and
/// unpacked to the data folder so ffmpeg's drawtext can read the same face for the live clock.
/// </summary>
public sealed class WeatherAssets : IDisposable
{
    private const string ResourcePrefix = "Jellyfin.Plugin.JellyStations.Assets.";
    private readonly string _assetsDir;
    private readonly Dictionary<FontWeight, SKTypeface> _typefaces = new();
    private readonly object _lock = new();

    public WeatherAssets(string assetsDir)
    {
        _assetsDir = assetsDir;
    }

    public static string FileName(FontWeight weight) => weight switch
    {
        FontWeight.Bold => "Inter-Bold.ttf",
        FontWeight.SemiBold => "Inter-SemiBold.ttf",
        _ => "Inter-Regular.ttf"
    };

    /// <summary>The on-disk copy of a font, written on first use; for ffmpeg.</summary>
    public string FontFile(FontWeight weight)
    {
        var path = Path.Combine(_assetsDir, FileName(weight));
        lock (_lock)
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                Directory.CreateDirectory(_assetsDir);
                using var stream = Open(FileName(weight));
                using var file = File.Create(path);
                stream.CopyTo(file);
            }
        }

        return path;
    }

    public SKTypeface Typeface(FontWeight weight)
    {
        lock (_lock)
        {
            if (_typefaces.TryGetValue(weight, out var tf))
            {
                return tf;
            }

            using var stream = Open(FileName(weight));
            using var data = SKData.Create(stream);
            tf = SKTypeface.FromData(data) ?? SKTypeface.Default;
            _typefaces[weight] = tf;
            return tf;
        }
    }

    private static Stream Open(string fileName)
        => Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourcePrefix + fileName)
           ?? throw new FileNotFoundException("Embedded font missing: " + fileName);

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var tf in _typefaces.Values)
            {
                tf.Dispose();
            }

            _typefaces.Clear();
        }
    }
}
