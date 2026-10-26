namespace Jellyfin.Plugin.JellyStations.Models;

/// <summary>One file of a multi-part (stacked) video, e.g. a Blu-ray split into part1 and part2.</summary>
public sealed class PlayablePart
{
    public string Path { get; set; } = string.Empty;

    public long RuntimeTicks { get; set; }

    /// <summary>Zero-based index among the file's audio streams to map; null = first audio.</summary>
    public int? AudioStreamIndex { get; set; }

    public bool HasAudio { get; set; } = true;

    /// <summary>The HDR transfer characteristic ("smpte2084" for PQ, "arib-std-b67" for HLG), or null for SDR video.</summary>
    public string? HdrTransfer { get; set; }

    /// <summary>True when the video is HDR and must be tone mapped to SDR for the stream.</summary>
    public bool IsHdr => !string.IsNullOrEmpty(HdrTransfer);

    /// <summary>Source video codec as ffmpeg names it (h264, hevc, mpeg2video, vc1, vp9, av1, mpeg4...), from the library probe.</summary>
    public string? VideoCodec { get; set; }

    /// <summary>Codec profile as probed ("High", "High 10", "Main 10", "Rext"...).</summary>
    public string? VideoProfile { get; set; }

    public int? VideoBitDepth { get; set; }

    /// <summary>ffmpeg pixel format as probed (yuv420p, yuv420p10le, yuv422p10le...).</summary>
    public string? PixelFormat { get; set; }

    /// <summary>Zero-based index among the file's embedded subtitle streams to burn in; null = none (or an external file, see SubtitlePath).</summary>
    public int? SubtitleStreamIndex { get; set; }

    /// <summary>An external subtitle file (.srt, .ass) to burn in; null when the track is embedded or there is none.</summary>
    public string? SubtitlePath { get; set; }

    /// <summary>True when the embedded track is bitmap subtitles (PGS, DVD), drawn over the picture rather than rendered from text.</summary>
    public bool SubtitleIsBitmap { get; set; }

    public bool HasSubtitles => SubtitleStreamIndex.HasValue || !string.IsNullOrEmpty(SubtitlePath);

    public TimeSpan Runtime => TimeSpan.FromTicks(RuntimeTicks);
}

/// <summary>The file to play at an offset into an item: which part, where in it, and how much of it is left.</summary>
public sealed record PlayableSegment(PlayablePart Part, int Index, TimeSpan Seek, TimeSpan Remaining)
{
    public bool IsLast { get; init; }
}

/// <summary>A Jellyfin-free snapshot of a library item, frozen at compile time.</summary>
public sealed class PlayableItem
{
    public Guid ItemId { get; set; }

    /// <summary>The file, or the first file of a multi-part item.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Total runtime, across every part for a multi-part item.</summary>
    public long RuntimeTicks { get; set; }

    /// <summary>Series name for episodes, title for movies.</summary>
    public string Title { get; set; } = string.Empty;

    public string? EpisodeTitle { get; set; }

    public Guid? SeriesId { get; set; }

    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }

    public bool IsMovie { get; set; }

    public bool IsSeries { get; set; }

    public List<string> Genres { get; set; } = new();

    /// <summary>Library tags; episodes without their own tags carry the series' tags.</summary>
    public List<string> Tags { get; set; } = new();

    public string? Overview { get; set; }

    public int? ProductionYear { get; set; }

    public string? OfficialRating { get; set; }

    public float? CommunityRating { get; set; }

    public string? PrimaryImagePath { get; set; }

    /// <summary>Zero-based index among the file's audio streams to map; null = first audio.</summary>
    public int? AudioStreamIndex { get; set; }

    /// <summary>False when the file is known to have no audio track (silence is generated instead).</summary>
    public bool HasAudio { get; set; } = true;

    /// <summary>The HDR transfer characteristic ("smpte2084" for PQ, "arib-std-b67" for HLG), or null for SDR video.</summary>
    public string? HdrTransfer { get; set; }

    /// <summary>True when the video is HDR and must be tone mapped to SDR for the stream.</summary>
    public bool IsHdr => !string.IsNullOrEmpty(HdrTransfer);

    /// <summary>Source video codec as ffmpeg names it (h264, hevc, mpeg2video, vc1, vp9, av1, mpeg4...), from the library probe.</summary>
    public string? VideoCodec { get; set; }

    /// <summary>Codec profile as probed ("High", "High 10", "Main 10", "Rext"...).</summary>
    public string? VideoProfile { get; set; }

    public int? VideoBitDepth { get; set; }

    /// <summary>ffmpeg pixel format as probed (yuv420p, yuv420p10le, yuv422p10le...).</summary>
    public string? PixelFormat { get; set; }

    /// <summary>Zero-based index among the file's embedded subtitle streams to burn in; null = none (or an external file, see SubtitlePath).</summary>
    public int? SubtitleStreamIndex { get; set; }

    /// <summary>An external subtitle file (.srt, .ass) to burn in; null when the track is embedded or there is none.</summary>
    public string? SubtitlePath { get; set; }

    /// <summary>True when the embedded track is bitmap subtitles (PGS, DVD), drawn over the picture rather than rendered from text.</summary>
    public bool SubtitleIsBitmap { get; set; }

    public bool HasSubtitles => SubtitleStreamIndex.HasValue || !string.IsNullOrEmpty(SubtitlePath);

    /// <summary>Files in play order for a multi-part item. Empty for a single file, whose Path and RuntimeTicks apply.</summary>
    public List<PlayablePart> Parts { get; set; } = new();

    public bool IsMultiPart => Parts.Count > 1;

    public TimeSpan Runtime => TimeSpan.FromTicks(RuntimeTicks);

    public string DisplayTitle => IsSeries && !string.IsNullOrEmpty(EpisodeTitle) ? Title + " - " + EpisodeTitle : Title;

    /// <summary>
    /// The part that is playing <paramref name="offset"/> into the item, the seek position within that file and the
    /// time left in it. Past the end, the last part with nothing remaining.
    /// </summary>
    public PlayableSegment SegmentAt(TimeSpan offset)
    {
        if (offset < TimeSpan.Zero)
        {
            offset = TimeSpan.Zero;
        }

        if (Parts.Count == 0)
        {
            var single = new PlayablePart { Path = Path, RuntimeTicks = RuntimeTicks, AudioStreamIndex = AudioStreamIndex, HasAudio = HasAudio, HdrTransfer = HdrTransfer, VideoCodec = VideoCodec, VideoProfile = VideoProfile, VideoBitDepth = VideoBitDepth, PixelFormat = PixelFormat, SubtitleStreamIndex = SubtitleStreamIndex, SubtitlePath = SubtitlePath, SubtitleIsBitmap = SubtitleIsBitmap };
            var seek = offset < single.Runtime ? offset : single.Runtime;
            return new PlayableSegment(single, 0, seek, single.Runtime - seek) { IsLast = true };
        }

        var start = TimeSpan.Zero;
        for (var i = 0; i < Parts.Count; i++)
        {
            var part = Parts[i];
            var end = start + part.Runtime;
            if (offset < end || i == Parts.Count - 1)
            {
                var seek = offset - start;
                if (seek > part.Runtime)
                {
                    seek = part.Runtime;
                }

                return new PlayableSegment(part, i, seek, part.Runtime - seek) { IsLast = i == Parts.Count - 1 };
            }

            start = end;
        }

        throw new InvalidOperationException("unreachable");
    }
}
