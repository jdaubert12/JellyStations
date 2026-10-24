using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Storage;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyStations.Library;

/// <summary>
/// Lists the audio files under a folder as <see cref="MusicTrack"/>s. Each file is probed once (ffprobe, or ffmpeg
/// when there is no ffprobe) for its length, title and artist; the answers are kept in an index keyed by path, size
/// and modification time, so a recompile costs nothing until files change.
/// </summary>
public sealed class MusicFolderScanner
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".ogg", ".oga", ".opus", ".m4a", ".aac", ".wav", ".wma", ".aiff", ".aif", ".mka", ".alac", ".ape", ".wv"
    };

    private readonly JellyStationsPaths _paths;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly SettingsRepository _settings;
    private readonly ILogger<MusicFolderScanner> _logger;
    private readonly object _lock = new();
    private Dictionary<string, IndexEntry>? _index;

    public MusicFolderScanner(JellyStationsPaths paths, IMediaEncoder mediaEncoder, SettingsRepository settings, ILogger<MusicFolderScanner> logger)
    {
        _paths = paths;
        _mediaEncoder = mediaEncoder;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>A remembered probe of one file.</summary>
    public sealed class IndexEntry
    {
        public long Length { get; set; }

        public DateTime LastWriteUtc { get; set; }

        public long RuntimeTicks { get; set; }

        public string? Title { get; set; }

        public string? Artist { get; set; }
    }

    public static bool IsAudioFile(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>A stable id for a folder file, so the music timeline survives recompiles.</summary>
    public static Guid IdFor(string path)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(path.Replace('\\', '/').ToLowerInvariant()));
        return new Guid(hash);
    }

    public IReadOnlyList<MusicTrack> Scan(string folder)
    {
        var tracks = new List<MusicTrack>();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return tracks;
        }

        lock (_lock)
        {
            var index = LoadIndex();
            var dirty = false;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(IsAudioFile).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "JellyStations: cannot list music folder {Folder}", folder);
                return tracks;
            }

            foreach (var file in files)
            {
                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                }
                catch (Exception)
                {
                    continue;
                }

                if (!index.TryGetValue(file, out var entry) || entry.Length != info.Length || entry.LastWriteUtc != info.LastWriteTimeUtc)
                {
                    entry = Probe(file);
                    entry.Length = info.Length;
                    entry.LastWriteUtc = info.LastWriteTimeUtc;
                    index[file] = entry;
                    dirty = true;
                }

                if (entry.RuntimeTicks <= 0)
                {
                    continue;
                }

                tracks.Add(new MusicTrack
                {
                    Id = IdFor(file),
                    Path = file,
                    RuntimeTicks = entry.RuntimeTicks,
                    Title = string.IsNullOrWhiteSpace(entry.Title) ? Path.GetFileNameWithoutExtension(file) : entry.Title!,
                    Artist = string.IsNullOrWhiteSpace(entry.Artist) ? null : entry.Artist,
                    Source = "Folder"
                });
            }

            if (dirty)
            {
                // Forget files that are gone so the index does not grow forever.
                foreach (var gone in index.Keys.Where(k => !File.Exists(k)).ToList())
                {
                    index.Remove(gone);
                }

                try
                {
                    JsonFileStore.Write(_paths.MusicIndexFile, index);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "JellyStations: could not save the music index");
                }
            }
        }

        return tracks;
    }

    private Dictionary<string, IndexEntry> LoadIndex()
    {
        if (_index is not null)
        {
            return _index;
        }

        try
        {
            _index = JsonFileStore.Read<Dictionary<string, IndexEntry>>(_paths.MusicIndexFile) ?? new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
        }
        catch (Exception)
        {
            _index = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
        }

        return _index;
    }

    private string? ProbePath()
    {
        var probe = _mediaEncoder.ProbePath;
        if (!string.IsNullOrWhiteSpace(probe) && File.Exists(probe))
        {
            return probe;
        }

        // Next to the ffmpeg in use (an override may point at a build with its own ffprobe).
        var ffmpeg = FfmpegPath();
        var dir = Path.GetDirectoryName(ffmpeg);
        if (dir is not null)
        {
            foreach (var name in new[] { "ffprobe", "ffprobe.exe" })
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private string FfmpegPath()
    {
        var o = _settings.Get().FfmpegPathOverride;
        return string.IsNullOrWhiteSpace(o) ? _mediaEncoder.EncoderPath : o;
    }

    /// <summary>Length and tags of one file; a zero runtime means it could not be read and the file is skipped.</summary>
    public IndexEntry Probe(string file)
    {
        var entry = new IndexEntry();
        try
        {
            var probe = ProbePath();
            if (probe is not null)
            {
                var output = Run(probe, new[] { "-v", "error", "-show_entries", "format=duration:format_tags=title,artist,album_artist", "-of", "json", file }, stderr: false);
                ParseProbeJson(output, entry);
            }

            if (entry.RuntimeTicks <= 0)
            {
                var output = Run(FfmpegPath(), new[] { "-hide_banner", "-nostdin", "-i", file }, stderr: true);
                ParseFfmpegInfo(output, entry);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("JellyStations: could not probe {File}: {Error}", file, ex.Message);
        }

        return entry;
    }

    /// <summary>Reads ffprobe's JSON: format.duration (seconds) and the title/artist tags.</summary>
    public static void ParseProbeJson(string json, IndexEntry entry)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("format", out var format))
            {
                return;
            }

            if (format.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            {
                entry.RuntimeTicks = (long)(seconds * TimeSpan.TicksPerSecond);
            }

            if (format.TryGetProperty("tags", out var tags))
            {
                foreach (var tag in tags.EnumerateObject())
                {
                    var value = tag.Value.GetString();
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    if (tag.Name.Equals("title", StringComparison.OrdinalIgnoreCase))
                    {
                        entry.Title = value.Trim();
                    }
                    else if (tag.Name.Equals("artist", StringComparison.OrdinalIgnoreCase) || (tag.Name.Equals("album_artist", StringComparison.OrdinalIgnoreCase) && entry.Artist is null))
                    {
                        entry.Artist = value.Trim();
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON: leave the entry empty so the ffmpeg fallback runs.
        }
    }

    /// <summary>Reads "Duration: 00:03:21.45" and the title/artist lines from ffmpeg's own banner for a file.</summary>
    public static void ParseFfmpegInfo(string text, IndexEntry entry)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var durationAt = line.IndexOf("Duration:", StringComparison.OrdinalIgnoreCase);
            if (durationAt >= 0 && entry.RuntimeTicks <= 0)
            {
                var rest = line[(durationAt + 9)..].Trim();
                var comma = rest.IndexOf(',');
                var stamp = comma > 0 ? rest[..comma] : rest;
                if (TimeSpan.TryParseExact(stamp.Trim(), new[] { @"hh\:mm\:ss\.ff", @"hh\:mm\:ss\.fff", @"hh\:mm\:ss" }, CultureInfo.InvariantCulture, out var span) && span > TimeSpan.Zero)
                {
                    entry.RuntimeTicks = span.Ticks;
                }

                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (value.Length == 0)
            {
                continue;
            }

            if (key.Equals("title", StringComparison.OrdinalIgnoreCase) && entry.Title is null)
            {
                entry.Title = value;
            }
            else if ((key.Equals("artist", StringComparison.OrdinalIgnoreCase) || key.Equals("album_artist", StringComparison.OrdinalIgnoreCase)) && entry.Artist is null)
            {
                entry.Artist = value;
            }
        }
    }

    private static string Run(string exe, IReadOnlyList<string> args, bool stderr)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + exe);
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(15000))
        {
            try
            {
                proc.Kill(true);
            }
            catch (Exception)
            {
                // Already gone.
            }

            throw new TimeoutException(exe + " did not finish within 15 s");
        }

        Task.WaitAll(outTask, errTask);
        return stderr ? errTask.Result : outTask.Result;
    }
}
