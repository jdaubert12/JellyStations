using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyStations.Library;

/// <summary>
/// Measures the loudness of weather music once per file, in the background, so every track can be played at the
/// same level. Each file is run through ffmpeg's <c>loudnorm</c> analysis (integrated loudness in LUFS and the
/// true peak) and the answer is kept in <c>music-loudness.json</c> keyed by path, size and modification time; a
/// recompile or a restart costs nothing until the file changes. The gain for a track is the distance from the
/// measured loudness to <see cref="TargetLufs"/>, capped so the true peak stays under <see cref="PeakCeilingDb"/>,
/// and zero until the track has been measured (so music starts at once and evens out as the measurements come in).
/// </summary>
public sealed class MusicLoudnessService : IDisposable
{
    /// <summary>Where every track is brought to. Quieter than streaming services' -14 so music sits under the screens.</summary>
    public const double TargetLufs = -18;

    /// <summary>The true peak no track is pushed past.</summary>
    public const double PeakCeilingDb = -1;

    public const double MaxBoostDb = 12;
    public const double MaxCutDb = 24;

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FirstTick = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan WorkPerTick = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PerFileTimeout = TimeSpan.FromMinutes(10);

    private readonly StationRepository _stations;
    private readonly CompiledStationRepository _compiled;
    private readonly JellyStationsPaths _paths;
    private readonly MusicFolderScanner _scanner;
    private readonly ILogger<MusicLoudnessService> _logger;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Dictionary<string, Entry>? _store;
    private Timer? _timer;
    private bool _disposed;

    public MusicLoudnessService(StationRepository stations, CompiledStationRepository compiled, JellyStationsPaths paths, MusicFolderScanner scanner, ILogger<MusicLoudnessService> logger)
    {
        _stations = stations;
        _compiled = compiled;
        _paths = paths;
        _scanner = scanner;
        _logger = logger;
    }

    /// <summary>One file's measurement (or why it could not be measured).</summary>
    public sealed class Entry
    {
        public long Length { get; set; }

        public DateTime LastWriteUtc { get; set; }

        /// <summary>Integrated loudness; null when the file is silent or could not be read.</summary>
        public double? Lufs { get; set; }

        public double? TruePeakDb { get; set; }

        public DateTime MeasuredUtc { get; set; }

        public string? Error { get; set; }
    }

    /// <summary>How far the levelling has got over a set of tracks.</summary>
    public readonly record struct Progress(int Total, int Measured, int Failed)
    {
        public int Pending => Total - Measured - Failed;
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_disposed || _timer is not null)
            {
                return;
            }

            _timer = new Timer(_ => _ = RunAsync(), null, FirstTick, Tick);
        }
    }

    /// <summary>The gain to apply to a track so it plays at the target level; 0 until it has been measured.</summary>
    public double GainDb(MusicTrack track)
    {
        var entry = Lookup(track.Path);
        return entry?.Lufs is null ? 0 : Gain(entry.Lufs.Value, entry.TruePeakDb);
    }

    /// <summary>The gain for a measured loudness and true peak: up to the target, never past the peak ceiling, within the caps.</summary>
    public static double Gain(double lufs, double? truePeakDb)
    {
        if (double.IsNaN(lufs) || double.IsInfinity(lufs))
        {
            return 0;
        }

        var gain = TargetLufs - lufs;
        if (truePeakDb is { } peak && !double.IsNaN(peak) && !double.IsInfinity(peak))
        {
            gain = Math.Min(gain, PeakCeilingDb - peak);
        }

        return Math.Round(Math.Clamp(gain, -MaxCutDb, MaxBoostDb), 1);
    }

    public Progress ProgressOf(IEnumerable<MusicTrack> tracks)
    {
        var total = 0;
        var measured = 0;
        var failed = 0;
        foreach (var path in tracks.Select(t => t.Path).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.Ordinal))
        {
            total++;
            var entry = Lookup(path);
            if (entry is null)
            {
                continue;
            }

            if (entry.Lufs is not null)
            {
                measured++;
            }
            else
            {
                failed++;
            }
        }

        return new Progress(total, measured, failed);
    }

    /// <summary>Measures what is pending, one file at a time, for a while; the next tick continues.</summary>
    public async Task RunAsync()
    {
        if (_disposed || !await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var started = DateTime.UtcNow;
            var ct = _cts.Token;
            while (!ct.IsCancellationRequested && DateTime.UtcNow - started < WorkPerTick)
            {
                var next = NextPending();
                if (next is null)
                {
                    return;
                }

                var entry = await MeasureAsync(next, ct).ConfigureAwait(false);
                if (entry is null)
                {
                    return;
                }

                Remember(next, entry);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JellyStations: music loudness measurement failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Measures one file now (the editor's Refresh); null when it cannot be read.</summary>
    public async Task<Entry?> MeasureAsync(string path, CancellationToken ct)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                return new Entry { Error = "missing", MeasuredUtc = DateTime.UtcNow };
            }
        }
        catch (Exception ex)
        {
            return new Entry { Error = ex.Message, MeasuredUtc = DateTime.UtcNow };
        }

        var entry = new Entry { Length = info.Length, LastWriteUtc = info.LastWriteTimeUtc, MeasuredUtc = DateTime.UtcNow };
        var ffmpeg = _scanner.FfmpegPath();
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-nostats", "-v", "info", "-i", path, "-vn", "-sn", "-dn", "-af", "loudnorm=I=-18:TP=-1:LRA=11:print_format=json", "-f", "null", "-" })
        {
            psi.ArgumentList.Add(a);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception)
            {
                // Not allowed on this platform; the encoders still get their share.
            }

            var stderr = process.StandardError.ReadToEndAsync(ct);
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(PerFileTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(true);
                }
                catch (Exception)
                {
                }

                if (ct.IsCancellationRequested)
                {
                    throw;
                }

                entry.Error = "timed out";
                return entry;
            }

            var text = await stderr.ConfigureAwait(false);
            await stdout.ConfigureAwait(false);
            if (!ParseLoudnorm(text, entry))
            {
                entry.Error = process.ExitCode != 0 ? "ffmpeg exit " + process.ExitCode : "no measurement";
                _logger.LogWarning("JellyStations: could not measure the loudness of {Path}: {Error}", path, entry.Error);
            }
            else
            {
                _logger.LogInformation("JellyStations: {Path} measures {Lufs} LUFS, peak {Peak} dBTP, gain {Gain} dB ({Seconds}s)", Path.GetFileName(path), entry.Lufs, entry.TruePeakDb, Gain(entry.Lufs!.Value, entry.TruePeakDb), sw.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            entry.Error = ex.Message;
            _logger.LogWarning(ex, "JellyStations: could not measure the loudness of {Path}", path);
        }

        return entry;
    }

    /// <summary>Reads loudnorm's JSON summary out of ffmpeg's stderr. False when there is none or the file was silent.</summary>
    public static bool ParseLoudnorm(string stderr, Entry entry)
    {
        var at = stderr.LastIndexOf("\"input_i\"", StringComparison.Ordinal);
        if (at < 0)
        {
            return false;
        }

        var start = stderr.LastIndexOf('{', at);
        var end = stderr.IndexOf('}', at);
        if (start < 0 || end < 0)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(stderr.Substring(start, end - start + 1));
            var lufs = Number(doc.RootElement, "input_i");
            var peak = Number(doc.RootElement, "input_tp");
            if (lufs is null || double.IsInfinity(lufs.Value) || double.IsNaN(lufs.Value) || lufs < -70)
            {
                return false;
            }

            entry.Lufs = Math.Round(lufs.Value, 2);
            entry.TruePeakDb = peak is null || double.IsInfinity(peak.Value) || double.IsNaN(peak.Value) ? null : Math.Round(peak.Value, 2);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static double? Number(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var p))
        {
            return null;
        }

        if (p.ValueKind == JsonValueKind.Number)
        {
            return p.GetDouble();
        }

        var s = p.GetString();
        if (s is null)
        {
            return null;
        }

        if (s.Contains("inf", StringComparison.OrdinalIgnoreCase))
        {
            return s.StartsWith('-') ? double.NegativeInfinity : double.PositiveInfinity;
        }

        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>Every track of every weather channel on the air (itself or through another station) that levels its music, in station order.</summary>
    private IEnumerable<MusicTrack> Tracks()
    {
        foreach (var station in Weather.WeatherSources.InUse(_stations.GetAll(), DateTime.UtcNow).Where(s => s.Weather.MusicEnabled && s.Weather.NormalizeMusic))
        {
            var compiled = _compiled.Get(station.Id);
            if (compiled is null)
            {
                continue;
            }

            foreach (var track in compiled.Music)
            {
                yield return track;
            }
        }
    }

    private string? NextPending()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in Tracks())
        {
            if (string.IsNullOrEmpty(track.Path) || !seen.Add(track.Path))
            {
                continue;
            }

            if (Lookup(track.Path) is null)
            {
                return track.Path;
            }
        }

        return null;
    }

    /// <summary>The stored measurement for a path when it still matches the file on disk.</summary>
    private Entry? Lookup(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        Entry? entry;
        lock (_lock)
        {
            if (!Load().TryGetValue(path, out entry))
            {
                return null;
            }
        }

        if (entry.Error == "missing")
        {
            // Try again when the file shows up.
            return File.Exists(path) ? null : entry;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != entry.Length || info.LastWriteTimeUtc != entry.LastWriteUtc)
            {
                return null;
            }
        }
        catch (Exception)
        {
            return null;
        }

        return entry;
    }

    private void Remember(string path, Entry entry)
    {
        lock (_lock)
        {
            var store = Load();
            store[path] = entry;
            Save(store);
        }
    }

    private Dictionary<string, Entry> Load()
    {
        if (_store is not null)
        {
            return _store;
        }

        _store = new Dictionary<string, Entry>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(_paths.MusicLoudnessFile))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_paths.MusicLoudnessFile));
                if (loaded is not null)
                {
                    _store = new Dictionary<string, Entry>(loaded, StringComparer.Ordinal);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JellyStations: music loudness index unreadable, measuring again");
        }

        return _store;
    }

    private void Save(Dictionary<string, Entry> store)
    {
        try
        {
            var tmp = _paths.MusicLoudnessFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _paths.MusicLoudnessFile, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JellyStations: music loudness index not saved");
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }

        _cts.Cancel();
        _cts.Dispose();
    }
}
