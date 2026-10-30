namespace Jellyfin.Plugin.JellyStations.Models;

/// <summary>
/// The per-file facts the restreamer needs, shared by a whole item (its first or only file) and each part of a
/// multi-part item. An interface rather than a base class so the compiled JSON keeps its property order.
/// </summary>
public interface IPlayableFile
{
    /// <summary>Gets or sets the file path.</summary>
    string Path { get; set; }

    /// <summary>Gets or sets the runtime in ticks.</summary>
    long RuntimeTicks { get; set; }

    /// <summary>Gets or sets the zero-based index among the file's audio streams to map; null = first audio.</summary>
    int? AudioStreamIndex { get; set; }

    /// <summary>Gets or sets a value indicating whether the file has an audio track.</summary>
    bool HasAudio { get; set; }

    /// <summary>Gets or sets the HDR transfer characteristic, or null for SDR video.</summary>
    string? HdrTransfer { get; set; }

    /// <summary>Gets or sets the source video codec as ffmpeg names it.</summary>
    string? VideoCodec { get; set; }

    /// <summary>Gets or sets the codec profile as probed.</summary>
    string? VideoProfile { get; set; }

    /// <summary>Gets or sets the video bit depth as probed.</summary>
    int? VideoBitDepth { get; set; }

    /// <summary>Gets or sets the ffmpeg pixel format as probed.</summary>
    string? PixelFormat { get; set; }

    /// <summary>Gets or sets the zero-based index among the embedded subtitle streams to burn in.</summary>
    int? SubtitleStreamIndex { get; set; }

    /// <summary>Gets or sets an external subtitle file to burn in.</summary>
    string? SubtitlePath { get; set; }

    /// <summary>Gets or sets a value indicating whether the embedded subtitle track is bitmap subtitles.</summary>
    bool SubtitleIsBitmap { get; set; }
}

/// <summary>Helpers over <see cref="IPlayableFile"/>.</summary>
public static class PlayableFiles
{
    /// <summary>Copies the audio, HDR, video and subtitle facts (everything but path and runtime) from one file to another.</summary>
    public static void CopyFacts(IPlayableFile from, IPlayableFile to)
    {
        to.AudioStreamIndex = from.AudioStreamIndex;
        to.HasAudio = from.HasAudio;
        to.HdrTransfer = from.HdrTransfer;
        to.VideoCodec = from.VideoCodec;
        to.VideoProfile = from.VideoProfile;
        to.VideoBitDepth = from.VideoBitDepth;
        to.PixelFormat = from.PixelFormat;
        to.SubtitleStreamIndex = from.SubtitleStreamIndex;
        to.SubtitlePath = from.SubtitlePath;
        to.SubtitleIsBitmap = from.SubtitleIsBitmap;
    }
}
