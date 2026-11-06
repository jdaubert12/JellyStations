using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyStations.Library;

/// <summary>
/// Looks at the picture of files that are flagged progressive for the lines of interlacing: a DVD or broadcast
/// source encoded without deinterlacing carries its fields woven into frames, and nothing in the file says so, so
/// the "when the file is interlaced" mode passes it by. Each file is sampled once in the background with ffmpeg's
/// <c>idet</c> (three short stretches) and the answer is kept in <c>interlace-check.json</c> keyed by path, size
/// and modification time. The restreamer asks for the answer when a program starts and deinterlaces the files
/// found, with the field order the samples showed.
/// </summary>
public sealed class InterlaceCheckService : IDisposable
{
    /// <summary>Taller pictures are not looked at: interlacing above 1080 lines does not exist, and decoding them is the slow part.</summary>
    public const int MaxHeight = 1088;

    /// <summary>Only frame rates a woven field pair can have (25, 29.97, 30) are looked at; film rates and 50/60 are frames already.</summary>
    public const double MinFps = 24.5;

    public const double MaxFps = 30.5;

    /// <summary>The share of the frames with a verdict that must show fields. 3:2 pulldown shows them in two frames of five or more.</summary>
    public const double MinInterlacedShare = 0.25;

    /// <summary>
    /// The share of those that must agree on the field order. Grain and a starved encode make idet call single
    /// frames interlaced too, but top first and bottom first about as often as each other; real fields keep one order.
    /// </summary>
    public const double MinFieldOrderShare = 0.85;

    /// <summary>Fewer frames with a verdict than this (a still picture, a black stretch) decide nothing.</summary>
    public const int MinFrames = 30;

    public const double SampleSeconds = 6;

    public const string TopFirst = "tff";

    public const string BottomFirst = "bff";

    private const int SaveEvery = 25;
    private const int MaxFoundListed = 200;
    private const int PruneSlack = 500;
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FirstTick = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WorkPerTick = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PerSampleTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan RetryFailedAfter = TimeSpan.FromDays(1);
    private static readonly Regex SingleFrame = new(@"Single frame detection:\s*TFF:\s*(\d+)\s*BFF:\s*(\d+)\s*Progressive:\s*(\d+)\s*Undetermined:\s*(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _storeFile;
    private readonly Func<IEnumerable<Candidate>> _files;
    private readonly Func<string, double, double, CancellationToken, Task<string?>> _sample;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Queue<Candidate> _urgent = new();
    private Dictionary<string, Entry>? _store;
    private int _unsaved;
    private Timer? _timer;
    private bool _disposed;

    public InterlaceCheckService(StationRepository stations, CompiledStationRepository compiled, SettingsRepository settings, JellyStationsPaths paths, MediaProbe probe, ILogger<InterlaceCheckService> logger)
    {
        _storeFile = paths.InterlaceCheckFile;
        _logger = logger;
        _files = () => FilesOf(stations, compiled, settings);
        _sample = (path, start, seconds, ct) => SampleAsync(probe.FfmpegPath(), path, start, seconds, ct);
    }

    /// <summary>For tests: a check over any list of files, with the sampling stood in for.</summary>
    public InterlaceCheckService(string storeFile, Func<IEnumerable<Candidate>> files, Func<string, double, double, CancellationToken, Task<string?>> sample, ILogger logger)
    {
        _storeFile = storeFile;
        _files = files;
        _sample = sample;
        _logger = logger;
    }

    /// <summary>A file a station plays, with the name it is listed under.</summary>
    public readonly record struct Candidate(IPlayableFile File, string Title);

    /// <summary>idet's frame counts, summed over a file's samples.</summary>
    public readonly record struct Counts(int Tff, int Bff, int Progressive, int Undetermined)
    {
        public int Interlaced => Tff + Bff;

        /// <summary>Frames idet gave a verdict on; a still or flat frame has none.</summary>
        public int Determined => Tff + Bff + Progressive;

        public static Counts operator +(Counts a, Counts b) => new(a.Tff + b.Tff, a.Bff + b.Bff, a.Progressive + b.Progressive, a.Undetermined + b.Undetermined);
    }

    /// <summary>One file's answer (or why there is none).</summary>
    public sealed class Entry
    {
        public long Length { get; set; }

        public DateTime LastWriteUtc { get; set; }

        public int Tff { get; set; }

        public int Bff { get; set; }

        public int Progressive { get; set; }

        public int Undetermined { get; set; }

        /// <summary>"tff" or "bff" when the picture carries fields; null when it does not.</summary>
        public string? FieldOrder { get; set; }

        public DateTime CheckedUtc { get; set; }

        public string? Error { get; set; }
    }

    /// <summary>How far the check has got, and what it found, for the Status page.</summary>
    public sealed class Summary
    {
        /// <summary>Files the check applies to: flagged progressive, of a size and rate fields can hide in, on a station that deinterlaces by file.</summary>
        public int Files { get; set; }

        public int Checked { get; set; }

        public int Failed { get; set; }

        public int FoundCount { get; set; }

        /// <summary>The files found, by title; at most <see cref="MaxFoundListed"/>.</summary>
        public List<FoundFile> Found { get; set; } = new();

        public int Pending => Files - Checked - Failed;
    }

    public sealed class FoundFile
    {
        public string Title { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public string FieldOrder { get; set; } = string.Empty;

        /// <summary>The share of the sampled frames that showed fields, 0 to 1.</summary>
        public double Share { get; set; }
    }

    /// <summary>
    /// True when a file's picture is worth a look: not flagged interlaced already (that one is deinterlaced
    /// anyway), SDR, no taller than 1080 lines and at a frame rate a woven field pair has. A size or rate the
    /// compile could not read counts as possible.
    /// </summary>
    public static bool IsCandidate(IPlayableFile file)
    {
        if (file.IsInterlaced || !string.IsNullOrEmpty(file.HdrTransfer) || string.IsNullOrEmpty(file.Path))
        {
            return false;
        }

        if (file.VideoHeight is int height && height > MaxHeight)
        {
            return false;
        }

        return file.FrameRate is not double fps || fps <= 0 || (fps >= MinFps && fps <= MaxFps);
    }

    /// <summary>Sums the "Single frame detection" lines of one or more idet runs.</summary>
    public static Counts ParseIdet(string? stderr)
    {
        var total = default(Counts);
        if (string.IsNullOrEmpty(stderr))
        {
            return total;
        }

        foreach (Match m in SingleFrame.Matches(stderr))
        {
            total += new Counts(Number(m, 1), Number(m, 2), Number(m, 3), Number(m, 4));
        }

        return total;

        static int Number(Match m, int group) => int.TryParse(m.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    /// <summary>The field order when the counts say the picture carries fields ("tff" or "bff"); null when they do not, or say too little.</summary>
    public static string? Judge(Counts c)
    {
        if (c.Determined < MinFrames || c.Interlaced < c.Determined * MinInterlacedShare)
        {
            return null;
        }

        var leading = Math.Max(c.Tff, c.Bff);
        if (leading < c.Interlaced * MinFieldOrderShare)
        {
            return null;
        }

        return c.Tff >= c.Bff ? TopFirst : BottomFirst;
    }

    /// <summary>
    /// Where a file is sampled, in seconds: three stretches at 20, 50 and 80 percent of a file of a minute or more
    /// (past the titles, short of the credits), else one stretch from the start.
    /// </summary>
    public static IReadOnlyList<(double Start, double Seconds)> SamplePlan(TimeSpan runtime)
    {
        var total = runtime.TotalSeconds;
        if (total < 60)
        {
            return new[] { (0d, total > 0 ? Math.Min(total, SampleSeconds * 3) : SampleSeconds * 3) };
        }

        return new[] { (Math.Floor(total * 0.2), SampleSeconds), (Math.Floor(total * 0.5), SampleSeconds), (Math.Floor(total * 0.8), SampleSeconds) };
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

    /// <summary>
    /// The field order to deinterlace a file with when the check found fields in its picture; null when it found
    /// none, has not looked yet, or the file is not the one that was looked at (it is then checked again).
    /// </summary>
    public string? FoundFieldOrder(IPlayableFile file)
    {
        if (!IsCandidate(file))
        {
            return null;
        }

        Entry? entry;
        lock (_lock)
        {
            if (!Load().TryGetValue(file.Path, out entry))
            {
                return null;
            }
        }

        if (entry.FieldOrder is null || entry.Error is not null)
        {
            return null;
        }

        if (!Matches(entry, file.Path))
        {
            lock (_lock)
            {
                if (Load().TryGetValue(file.Path, out var now) && ReferenceEquals(now, entry))
                {
                    _store!.Remove(file.Path);
                    _unsaved++;
                }
            }

            return null;
        }

        return entry.FieldOrder;
    }

    /// <summary>Moves a program's files to the head of the queue and starts on them now: the restreamer names what airs next.</summary>
    public void Prioritize(PlayableItem item)
    {
        var added = false;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            var store = Load();
            foreach (var candidate in Expand(item))
            {
                if (IsCandidate(candidate.File) && !store.ContainsKey(candidate.File.Path) && _urgent.All(u => u.File.Path != candidate.File.Path))
                {
                    _urgent.Enqueue(candidate);
                    added = true;
                }
            }
        }

        if (added)
        {
            _ = Task.Run(RunAsync);
        }
    }

    public Summary Summarize()
    {
        var summary = new Summary();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<FoundFile>();
        lock (_lock)
        {
            var store = Load();
            foreach (var candidate in _files())
            {
                if (!IsCandidate(candidate.File) || !seen.Add(candidate.File.Path))
                {
                    continue;
                }

                summary.Files++;
                if (!store.TryGetValue(candidate.File.Path, out var entry))
                {
                    continue;
                }

                if (entry.Error is not null)
                {
                    summary.Failed++;
                    continue;
                }

                summary.Checked++;
                if (entry.FieldOrder is not null)
                {
                    var determined = entry.Tff + entry.Bff + entry.Progressive;
                    found.Add(new FoundFile
                    {
                        Title = candidate.Title,
                        Path = candidate.File.Path,
                        FieldOrder = entry.FieldOrder,
                        Share = determined > 0 ? Math.Round((entry.Tff + entry.Bff) / (double)determined, 2) : 0
                    });
                }
            }
        }

        summary.FoundCount = found.Count;
        summary.Found = found.OrderBy(f => f.Title, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Path, StringComparer.Ordinal).Take(MaxFoundListed).ToList();
        return summary;
    }

    /// <summary>Checks what is waiting, one file at a time, for a while; the next tick continues.</summary>
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
            var waiting = new Queue<Candidate>(Pending());
            var done = 0;
            var found = 0;
            while (!ct.IsCancellationRequested && DateTime.UtcNow - started < WorkPerTick)
            {
                Candidate next;
                lock (_lock)
                {
                    if (_urgent.Count > 0)
                    {
                        next = _urgent.Dequeue();
                    }
                    else if (waiting.Count > 0)
                    {
                        next = waiting.Dequeue();
                    }
                    else
                    {
                        break;
                    }

                    if (Load().TryGetValue(next.File.Path, out var have) && !IsStale(have, DateTime.UtcNow))
                    {
                        // Looked at since the list was made (it was next on a station, or sits in two lists).
                        continue;
                    }
                }

                var entry = await CheckAsync(next, ct).ConfigureAwait(false);
                done++;
                if (entry.FieldOrder is not null)
                {
                    found++;
                }

                lock (_lock)
                {
                    Load()[next.File.Path] = entry;
                    if (++_unsaved >= SaveEvery)
                    {
                        Save();
                    }
                }
            }

            if (done > 0)
            {
                _logger.LogInformation("JellyStations: picture check looked at {Done} file(s) in {Seconds}s; {Found} carry interlacing and will be deinterlaced", done, (DateTime.UtcNow - started).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture), found);
            }
            else
            {
                Prune();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JellyStations: picture check failed");
        }
        finally
        {
            lock (_lock)
            {
                if (_unsaved > 0)
                {
                    Save();
                }
            }

            _gate.Release();
        }
    }

    /// <summary>Samples one file and judges it. A file that cannot be read gets an entry with the reason, tried again another day.</summary>
    public async Task<Entry> CheckAsync(Candidate candidate, CancellationToken ct)
    {
        var path = candidate.File.Path;
        var entry = new Entry { CheckedUtc = DateTime.UtcNow };
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                entry.Error = "missing";
                return entry;
            }

            entry.Length = info.Length;
            entry.LastWriteUtc = info.LastWriteTimeUtc;
        }
        catch (Exception ex)
        {
            entry.Error = ex.Message;
            return entry;
        }

        var counts = default(Counts);
        var plan = SamplePlan(TimeSpan.FromTicks(Math.Max(0, candidate.File.RuntimeTicks)));
        for (var i = 0; i < plan.Count; i++)
        {
            var output = await _sample(path, plan[i].Start, plan[i].Seconds, ct).ConfigureAwait(false);
            if (output is null)
            {
                entry.Error = "could not be sampled";
                return entry;
            }

            counts += ParseIdet(output);
            if (i == 1 && counts.Interlaced == 0 && counts.Progressive >= MinFrames * 4)
            {
                // Two stretches of moving picture without one frame of fields: the third would say the same.
                break;
            }
        }

        entry.Tff = counts.Tff;
        entry.Bff = counts.Bff;
        entry.Progressive = counts.Progressive;
        entry.Undetermined = counts.Undetermined;
        entry.FieldOrder = Judge(counts);
        if (entry.FieldOrder is not null)
        {
            _logger.LogInformation(
                "JellyStations: {Title} is flagged progressive but its picture carries interlacing ({Interlaced} of {Determined} sampled frames, {Order}); it will be deinterlaced",
                Text.ForLog(candidate.Title),
                counts.Interlaced,
                counts.Determined,
                entry.FieldOrder == TopFirst ? "top field first" : "bottom field first");
        }

        return entry;
    }

    /// <summary>The candidates without an answer (or with a failure old enough to try again), each path once, in station order.</summary>
    private List<Candidate> Pending()
    {
        var pending = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            var store = Load();
            foreach (var candidate in _files())
            {
                if (!IsCandidate(candidate.File) || !seen.Add(candidate.File.Path))
                {
                    continue;
                }

                if (!store.TryGetValue(candidate.File.Path, out var entry) || IsStale(entry, now))
                {
                    pending.Add(candidate);
                }
            }
        }

        return pending;
    }

    private static bool IsStale(Entry entry, DateTime now) => entry.Error is not null && now - entry.CheckedUtc > RetryFailedAfter;

    /// <summary>Forgets the answers for files no station plays any more, once enough of them have piled up to be worth a write.</summary>
    private void Prune()
    {
        lock (_lock)
        {
            var store = Load();
            if (store.Count <= PruneSlack)
            {
                return;
            }

            var inUse = _files().Select(c => c.File.Path).ToHashSet(StringComparer.Ordinal);
            var unused = store.Keys.Where(p => !inUse.Contains(p)).ToList();
            if (unused.Count <= PruneSlack)
            {
                return;
            }

            foreach (var path in unused)
            {
                store.Remove(path);
            }

            Save();
        }
    }

    /// <summary>Every file of every enabled station whose encoding deinterlaces by file: programs and break clips, a multi-part item part by part.</summary>
    private static IEnumerable<Candidate> FilesOf(StationRepository stations, CompiledStationRepository compiled, SettingsRepository settings)
    {
        var fallback = settings.Get().DefaultEncoding ?? new EncodingProfile();
        foreach (var station in stations.GetAll())
        {
            if (!station.Enabled || !(station.Encoding ?? fallback).DeinterlacesByFile)
            {
                continue;
            }

            var snapshot = compiled.Get(station.Id);
            if (snapshot is null)
            {
                continue;
            }

            foreach (var item in snapshot.PlayedItems)
            {
                foreach (var candidate in Expand(item))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<Candidate> Expand(PlayableItem item)
    {
        if (item.Parts.Count == 0)
        {
            yield return new Candidate(item, item.DisplayTitle);
            yield break;
        }

        for (var i = 0; i < item.Parts.Count; i++)
        {
            yield return new Candidate(item.Parts[i], item.DisplayTitle + " (part " + (i + 1).ToString(CultureInfo.InvariantCulture) + ")");
        }
    }

    /// <summary>
    /// Runs idet over one stretch of a file and returns ffmpeg's log, or null when ffmpeg failed or ran out of time.
    /// The seek lands on a keyframe (where exactly does not matter), sound and subtitles are not decoded, and the
    /// process runs at low priority on two threads so a station on the air keeps its cores.
    /// </summary>
    private async Task<string?> SampleAsync(string ffmpeg, string path, double start, double seconds, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        var args = new List<string> { "-hide_banner", "-nostdin", "-nostats", "-v", "info", "-threads", "2" };
        if (start > 0)
        {
            args.AddRange(new[] { "-noaccurate_seek", "-ss", start.ToString("0.###", CultureInfo.InvariantCulture) });
        }

        args.AddRange(new[] { "-t", seconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", path, "-map", "0:v:0", "-an", "-sn", "-dn", "-vf", "idet", "-f", "null", "-" });
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

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
            timeout.CancelAfter(PerSampleTimeout);
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

                _logger.LogWarning("JellyStations: picture check of {Path} timed out", Text.ForLog(path));
                return null;
            }

            var text = await stderr.ConfigureAwait(false);
            await stdout.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                _logger.LogDebug("JellyStations: picture check of {Path} failed: ffmpeg exit {Code}", Text.ForLog(path), process.ExitCode);
                return null;
            }

            return text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "JellyStations: picture check of {Path} failed", Text.ForLog(path));
            return null;
        }
    }

    /// <summary>True when the file on disk is still the one the entry was made from.</summary>
    private static bool Matches(Entry entry, string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length == entry.Length && info.LastWriteTimeUtc == entry.LastWriteUtc;
        }
        catch (Exception)
        {
            return false;
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
            if (File.Exists(_storeFile))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_storeFile));
                if (loaded is not null)
                {
                    _store = new Dictionary<string, Entry>(loaded, StringComparer.Ordinal);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JellyStations: picture check results unreadable, checking again");
        }

        return _store;
    }

    private void Save()
    {
        _unsaved = 0;
        try
        {
            var tmp = _storeFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Load()));
            File.Move(tmp, _storeFile, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JellyStations: picture check results not saved");
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
            if (_unsaved > 0)
            {
                Save();
            }
        }

        _cts.Cancel();
        _cts.Dispose();
    }
}
