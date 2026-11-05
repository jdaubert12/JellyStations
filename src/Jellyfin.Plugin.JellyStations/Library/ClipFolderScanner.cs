using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyStations.Library;

/// <summary>What a clip folder holds: the clips that can play, and the video files that could not be read.</summary>
public sealed class ClipFolderScan
{
    /// <summary>Gets a value indicating whether the folder exists and could be listed.</summary>
    public bool Exists { get; init; }

    /// <summary>Gets the number of video files found by extension, readable or not.</summary>
    public int Files { get; init; }

    public List<PlayableItem> Clips { get; init; } = new();

    /// <summary>Gets the video files ffprobe could not read as a video with a length.</summary>
    public List<ExcludedItem> Unreadable { get; init; } = new();
}

/// <summary>
/// Lists the video files under a folder as clips for the breaks, for a bumper source that names a folder instead
/// of a Jellyfin library. Jellyfin knows nothing about these files, so each is probed once (ffprobe, or ffmpeg
/// when there is no ffprobe) for what a library item would have told the plugin: its length, whether it has
/// sound, and the video facts the encoder chain depends on (codec, profile, bit depth, pixel format, interlacing,
/// HDR transfer). The answers are kept in an index keyed by path, size and modification time, so a recompile
/// costs nothing until files change.
/// </summary>
public sealed class ClipFolderScanner
{
    /// <summary>The most video files one folder contributes; a path that turns out to be a whole media drive stops here.</summary>
    public const int MaxFiles = 2000;

    /// <summary>How deep under the folder files are looked for.</summary>
    public const int MaxDepth = 8;

    private const int ProbesAtOnce = 4;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".mov", ".avi", ".webm", ".ts", ".m2ts", ".mts", ".mpg", ".mpeg", ".wmv", ".flv", ".ogv", ".divx", ".vob"
    };

    private static readonly Regex BannerDuration = new(@"Duration:\s*(\d+):(\d\d):(\d\d(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex BannerVideo = new(@"^\s*Stream #.*?: Video: (\w+)", RegexOptions.Compiled);

    private readonly string _indexFile;
    private readonly string _bumpersDir;
    private readonly Func<string, IndexEntry> _probe;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly Services.ProblemFileLog? _problems;
    private Dictionary<string, IndexEntry>? _index;

    public ClipFolderScanner(JellyStationsPaths paths, MediaProbe probe, ILogger<ClipFolderScanner> logger, Services.ProblemFileLog? problems = null)
    {
        _indexFile = paths.ClipIndexFile;
        _bumpersDir = paths.BumpersDir;
        _logger = logger;
        _probe = file => Probe(probe, file);
        _problems = problems;
    }

    /// <summary>For tests: an index file and a bumpers folder of the caller's, and a stand-in for ffprobe.</summary>
    public ClipFolderScanner(string indexFile, string bumpersDir, Func<string, IndexEntry> probe, ILogger logger, Services.ProblemFileLog? problems = null)
    {
        _problems = problems;
        _indexFile = indexFile;
        _bumpersDir = bumpersDir;
        _probe = probe;
        _logger = logger;
    }

    /// <summary>A remembered probe of one file.</summary>
    public sealed class IndexEntry
    {
        public long Length { get; set; }

        public DateTime LastWriteUtc { get; set; }

        public long RuntimeTicks { get; set; }

        public bool HasVideo { get; set; }

        public bool HasAudio { get; set; }

        public string? VideoCodec { get; set; }

        public string? VideoProfile { get; set; }

        public int? VideoBitDepth { get; set; }

        public string? PixelFormat { get; set; }

        public bool IsInterlaced { get; set; }

        public string? HdrTransfer { get; set; }

        /// <summary>Whether the file can play as a clip: a picture and a known length.</summary>
        public bool Playable() => HasVideo && RuntimeTicks > 0;
    }

    public static bool IsVideoFile(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>A stable id for a folder file, so exclusions and the air log survive recompiles.</summary>
    public static Guid IdFor(string path) => MusicFolderScanner.IdFor(path);

    /// <summary>A folder name under the plugin's bumpers folder, or an absolute path, as the person typed it; null when nothing was typed.</summary>
    public string? Resolve(string? folder)
    {
        var name = Text.TrimOrNull(folder);
        if (name is null)
        {
            return null;
        }

        return Path.IsPathRooted(name) ? name : Path.Combine(_bumpersDir, name);
    }

    /// <summary>The video files under a folder in a steady order, without reading them; null when the folder is missing or cannot be listed.</summary>
    public List<string>? Files(string? folder)
    {
        var path = Resolve(folder);
        if (path is null || !Directory.Exists(path))
        {
            return null;
        }

        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = MaxDepth };
            return Directory.EnumerateFiles(path, "*", options)
                .Where(IsVideoFile)
                .Take(MaxFiles)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JellyStations: cannot list clip folder {Folder}", Text.ForLog(path));
            return null;
        }
    }

    public ClipFolderScan Scan(string? folder)
    {
        var files = Files(folder);
        if (files is null)
        {
            return new ClipFolderScan();
        }

        var scan = new ClipFolderScan { Exists = true, Files = files.Count };
        lock (_lock)
        {
            var index = LoadIndex();
            var stale = new List<(string File, FileInfo Info)>();
            var current = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                    if (!info.Exists)
                    {
                        continue;
                    }
                }
                catch (Exception)
                {
                    continue;
                }

                if (index.TryGetValue(file, out var entry) && entry.Length == info.Length && entry.LastWriteUtc == info.LastWriteTimeUtc)
                {
                    current[file] = entry;
                }
                else
                {
                    stale.Add((file, info));
                }
            }

            if (stale.Count > 0)
            {
                // A first scan of a big folder is one ffprobe per file; a few at a time keeps a save from waiting on them in a row.
                var probed = new ConcurrentDictionary<string, IndexEntry>(StringComparer.Ordinal);
                Parallel.ForEach(stale, new ParallelOptions { MaxDegreeOfParallelism = ProbesAtOnce }, s =>
                {
                    var entry = SafeProbe(s.File);
                    entry.Length = s.Info.Length;
                    entry.LastWriteUtc = s.Info.LastWriteTimeUtc;
                    probed[s.File] = entry;
                });

                foreach (var kv in probed)
                {
                    index[kv.Key] = kv.Value;
                    current[kv.Key] = kv.Value;
                }

                // Forget files that are gone so the index does not grow forever.
                foreach (var gone in index.Keys.Where(k => !File.Exists(k)).ToList())
                {
                    index.Remove(gone);
                }

                try
                {
                    JsonFileStore.Write(_indexFile, index);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "JellyStations: could not save the clip index");
                }
            }

            var unreadable = new List<string>();
            var readable = new List<string>();
            foreach (var file in files)
            {
                if (!current.TryGetValue(file, out var entry))
                {
                    continue;
                }

                if (entry.Playable())
                {
                    scan.Clips.Add(ToPlayable(file, entry));
                    readable.Add(file);
                }
                else
                {
                    unreadable.Add(file);
                    scan.Unreadable.Add(new ExcludedItem { ItemId = IdFor(file), Name = Path.GetFileName(file), Reason = ExclusionReason.Unreadable });
                }
            }

            // The Status page lists the files a person has to fix, with their paths; the exclusion only has the name.
            _problems?.FolderScanned(unreadable, readable);
        }

        return scan;
    }

    /// <summary>The clip a probed file makes: named after the file, with the facts a library item's probe would carry.</summary>
    public static PlayableItem ToPlayable(string file, IndexEntry entry) => new()
    {
        ItemId = IdFor(file),
        Path = file,
        RuntimeTicks = entry.RuntimeTicks,
        Title = Path.GetFileNameWithoutExtension(file),
        IsMovie = true,
        HasAudio = entry.HasAudio,
        HdrTransfer = entry.HdrTransfer,
        IsInterlaced = entry.IsInterlaced,
        VideoCodec = entry.VideoCodec,
        VideoProfile = entry.VideoProfile,
        VideoBitDepth = entry.VideoBitDepth,
        PixelFormat = entry.PixelFormat
    };

    private Dictionary<string, IndexEntry> LoadIndex()
    {
        if (_index is not null)
        {
            return _index;
        }

        try
        {
            _index = JsonFileStore.Read<Dictionary<string, IndexEntry>>(_indexFile) ?? new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
        }
        catch (Exception)
        {
            _index = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
        }

        return _index;
    }

    private IndexEntry SafeProbe(string file)
    {
        try
        {
            return _probe(file);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("JellyStations: could not probe {File}: {Error}", Text.ForLog(file), ex.Message);
            return new IndexEntry();
        }
    }

    /// <summary>One file's facts from ffprobe, or from ffmpeg's banner when there is no ffprobe or it had no answer.</summary>
    private static IndexEntry Probe(MediaProbe tools, string file)
    {
        var entry = new IndexEntry();
        var probe = tools.ProbePath();
        if (probe is not null)
        {
            var output = MediaProbe.Run(
                probe,
                new[] { "-v", "error", "-show_entries", "format=duration:stream=codec_type,codec_name,profile,pix_fmt,bits_per_raw_sample,field_order,color_transfer:stream_disposition=attached_pic", "-of", "json", file },
                stderr: false);
            ParseProbeJson(output, entry);
        }

        if (!entry.Playable())
        {
            var banner = MediaProbe.Run(tools.FfmpegPath(), new[] { "-hide_banner", "-nostdin", "-i", file }, stderr: true);
            ParseFfmpegInfo(banner, entry);
        }

        return entry;
    }

    /// <summary>
    /// Reads ffprobe's JSON: the container's length, whether there is sound, and the first real video stream (cover
    /// art attached to a file is a "video" stream too, and is passed over).
    /// </summary>
    public static void ParseProbeJson(string json, IndexEntry entry)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("format", out var format) && Number(format, "duration") is { } seconds && seconds > 0)
            {
                entry.RuntimeTicks = (long)(seconds * TimeSpan.TicksPerSecond);
            }

            if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var stream in streams.EnumerateArray())
            {
                var type = Str(stream, "codec_type");
                if (type == "audio")
                {
                    entry.HasAudio = true;
                }
                else if (type == "video" && !entry.HasVideo && !IsAttachedPicture(stream))
                {
                    entry.HasVideo = true;
                    entry.VideoCodec = Str(stream, "codec_name")?.ToLowerInvariant();
                    entry.VideoProfile = Str(stream, "profile");
                    entry.PixelFormat = Str(stream, "pix_fmt")?.ToLowerInvariant();
                    entry.VideoBitDepth = BitDepth(Number(stream, "bits_per_raw_sample"), entry.PixelFormat);
                    entry.IsInterlaced = Str(stream, "field_order")?.ToLowerInvariant() is "tt" or "bb" or "tb" or "bt";
                    entry.HdrTransfer = Str(stream, "color_transfer")?.ToLowerInvariant() is "smpte2084" or "arib-std-b67"
                        ? Str(stream, "color_transfer")!.ToLowerInvariant()
                        : null;
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON: leave the entry empty so the ffmpeg fallback runs.
        }
    }

    /// <summary>Reads "Duration: 00:00:21.45" and the stream lines from ffmpeg's own banner for a file: enough to play it, with the video facts left unknown.</summary>
    public static void ParseFfmpegInfo(string text, IndexEntry entry)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (entry.RuntimeTicks <= 0 && BannerDuration.Match(line) is { Success: true } d)
            {
                var seconds = (int.Parse(d.Groups[1].Value, CultureInfo.InvariantCulture) * 3600)
                    + (int.Parse(d.Groups[2].Value, CultureInfo.InvariantCulture) * 60)
                    + double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture);
                if (seconds > 0)
                {
                    entry.RuntimeTicks = (long)(seconds * TimeSpan.TicksPerSecond);
                }

                continue;
            }

            if (!line.TrimStart().StartsWith("Stream #", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Contains(": Audio:", StringComparison.Ordinal))
            {
                entry.HasAudio = true;
            }
            else if (!entry.HasVideo && !line.Contains("(attached pic)", StringComparison.Ordinal) && BannerVideo.Match(line) is { Success: true } v)
            {
                entry.HasVideo = true;
                entry.VideoCodec ??= v.Groups[1].Value.ToLowerInvariant();
            }
        }
    }

    /// <summary>The bit depth ffprobe states, else the one the pixel format implies ("yuv420p10le" is 10, "yuv420p" is 8).</summary>
    public static int? BitDepth(double? stated, string? pixelFormat)
    {
        if (stated is > 0 and <= 16)
        {
            return (int)stated.Value;
        }

        if (string.IsNullOrEmpty(pixelFormat))
        {
            return null;
        }

        if (pixelFormat.Contains("p010", StringComparison.Ordinal))
        {
            return 10;
        }

        foreach (var depth in new[] { 16, 14, 12, 10, 9 })
        {
            var mark = "p" + depth.ToString(CultureInfo.InvariantCulture);
            if (pixelFormat.EndsWith(mark, StringComparison.Ordinal) || pixelFormat.Contains(mark + "le", StringComparison.Ordinal) || pixelFormat.Contains(mark + "be", StringComparison.Ordinal))
            {
                return depth;
            }
        }

        return 8;
    }

    private static bool IsAttachedPicture(JsonElement stream)
        => stream.TryGetProperty("disposition", out var d) && d.ValueKind == JsonValueKind.Object && Number(d, "attached_pic") is > 0;

    private static string? Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Text.TrimOrNull(v.GetString()) : null;

    /// <summary>A number ffprobe wrote as a number or, as it does for most fields, as a string.</summary>
    private static double? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var v))
        {
            return null;
        }

        if (v.ValueKind == JsonValueKind.Number)
        {
            return v.GetDouble();
        }

        return v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
    }
}
