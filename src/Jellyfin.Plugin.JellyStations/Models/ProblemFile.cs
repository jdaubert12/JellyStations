namespace Jellyfin.Plugin.JellyStations.Models;

/// <summary>What went wrong with a file, from worst to least.</summary>
public enum ProblemKind
{
    /// <summary>The file was missing or could not be opened when its program came up.</summary>
    CannotOpen,

    /// <summary>ffmpeg gave up on the file within seconds, after every fallback the encoder has.</summary>
    WouldNotPlay,

    /// <summary>ffmpeg reached the end of the file well before the length the schedule has for it.</summary>
    EndedEarly,

    /// <summary>The file played, but the decoder reported damage on the way.</summary>
    DecodeErrors,

    /// <summary>The GPU decoder failed on the file although its format is one it takes; it played in software.</summary>
    HardwareDecode,

    /// <summary>A file in a clip folder that ffprobe could not read as a video with a length.</summary>
    Unreadable
}

/// <summary>One file that gave trouble, as the Status page lists it. One entry per path: the latest problem wins.</summary>
public sealed class ProblemFile
{
    public string Path { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public ProblemKind Kind { get; set; }

    /// <summary>ffmpeg's own line, or how far in the file stopped: what a person needs to judge the file.</summary>
    public string? Detail { get; set; }

    /// <summary>The station it last happened on; null for a folder scan.</summary>
    public Guid? StationId { get; set; }

    public string? StationName { get; set; }

    public DateTime FirstUtc { get; set; }

    public DateTime LastUtc { get; set; }

    public int Count { get; set; }

    /// <summary>The file's size when the problem was seen, so a replaced file can be told from the same one; null when it could not be read.</summary>
    public long? Length { get; set; }

    public DateTime? LastWriteUtc { get; set; }
}

/// <summary>The stored list (problem-files.json).</summary>
public sealed class ProblemFileStore
{
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;

    public List<ProblemFile> Files { get; set; } = new();
}
