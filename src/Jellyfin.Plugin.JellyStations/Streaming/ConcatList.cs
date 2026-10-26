using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.JellyStations.Streaming;

/// <summary>
/// A list for ffmpeg's concat demuxer: still images with durations, the plain way to play a frame sequence as one
/// input (the radar loop). Paths are single-quoted, the only quoting the demuxer understands, with a quote inside
/// written as '\''.
/// </summary>
public static class ConcatList
{
    public static string Build(IEnumerable<(string Path, double Seconds)> entries)
    {
        var sb = new StringBuilder("ffconcat version 1.0\n");
        foreach (var (path, seconds) in entries)
        {
            sb.Append("file ").Append(Quote(path)).Append('\n');
            sb.Append("duration ").Append(seconds.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
        }

        return sb.ToString();
    }

    public static string Quote(string path) => "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
