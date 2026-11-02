using System.Collections.Concurrent;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyStations.Services;

/// <summary>
/// Keeps every station's air log: once a minute it reads what each enabled station's timeline aired since the last
/// look (programs only; a slot that aired counts whether or not anyone watched) and writes the logs that changed.
/// A gap longer than an hour (the server was off) is not back-filled: only the program airing now is logged.
/// The log never reaches a scheduler live; <see cref="Snapshot"/> freezes it into a compile when a revision begins.
/// </summary>
public sealed class AirLogRecorder : IDisposable
{
    /// <summary>The longest gap between two looks that is still logged in full.</summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromHours(1);

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(15);

    private readonly StationRepository _stations;
    private readonly IScheduleResolver _resolver;
    private readonly AirLogRepository _repository;
    private readonly ILogger<AirLogRecorder> _logger;
    private readonly ConcurrentDictionary<Guid, AirLog> _logs = new();
    private readonly ConcurrentDictionary<Guid, AirLog> _published = new();
    private readonly HashSet<Guid> _dirty = new();
    private readonly object _lock = new();
    private Timer? _timer;
    private int _ticking;
    private int _disposed;

    public AirLogRecorder(StationRepository stations, IScheduleResolver resolver, AirLogRepository repository, ILogger<AirLogRecorder> logger)
    {
        _stations = stations;
        _resolver = resolver;
        _repository = repository;
        _logger = logger;
    }

    /// <summary>Starts recording. Called once by the hosted service.</summary>
    public void Start()
    {
        _timer ??= new Timer(_ => SafeTick(), null, StartDelay, Tick);
    }

    /// <summary>
    /// The station's log as it stands (empty when nothing has aired): a copy the recorder never touches again, so
    /// callers may read it freely. Reused until the recorder logs something new.
    /// </summary>
    public AirLog Get(Guid stationId)
    {
        if (_published.TryGetValue(stationId, out var copy))
        {
            return copy;
        }

        lock (_lock)
        {
            copy = JsonFileStore.Clone(Live(stationId));
            copy.Keys = new Dictionary<string, AirLogKey>(copy.Keys, StringComparer.Ordinal);
            _published[stationId] = copy;
            return copy;
        }
    }

    /// <summary>Forgets everything the station aired. A compiled snapshot already taken is untouched until the next revision.</summary>
    public void Clear(Guid stationId)
    {
        lock (_lock)
        {
            _logs.TryRemove(stationId, out _);
            _published.TryRemove(stationId, out _);
            _dirty.Remove(stationId);
            _repository.Delete(stationId);
        }
    }

    /// <summary>The log the recorder writes to; only under the lock.</summary>
    private AirLog Live(Guid stationId) => _logs.GetOrAdd(stationId, id => _repository.Get(id));

    /// <summary>The log frozen for a new revision of the station, bounded by what the compile contains; null when there is nothing to carry.</summary>
    public AirHistorySnapshot? Snapshot(Guid stationId, DateTime nowUtc, CompiledStation compiled) => Snapshot(Get(stationId), nowUtc, compiled);

    /// <summary>Looks at every station now rather than at the next tick (tests and the dashboard).</summary>
    public void TickNow() => SafeTick();

    /// <summary>
    /// Logs the programs among <paramref name="entries"/> that have started by <paramref name="nowUtc"/>, each once
    /// and never the one the log already ends with. Returns true when anything was logged.
    /// </summary>
    public static bool Observe(AirLog log, IEnumerable<ScheduleEntry> entries, DateTime nowUtc)
    {
        var logged = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ScheduleEntry? latest = null;
        foreach (var e in entries.Where(e => e.Kind == EntryKind.Program && e.Item is not null && e.StartUtc <= nowUtc).OrderBy(e => e.StartUtc))
        {
            var programId = e.ProgramId;
            if (programId == log.LastProgramId || !seen.Add(programId))
            {
                continue;
            }

            var item = e.Item!;
            var key = PoolOrdering.KeyOf(item);
            if (!log.Keys.TryGetValue(key, out var k))
            {
                k = new AirLogKey();
                log.Keys[key] = k;
            }

            k.LastStartUtc = e.StartUtc;
            k.LastEndUtc = e.EndUtc;
            k.LastItemId = item.ItemId;
            k.Title = item.Title;
            k.Season = item.SeasonNumber;
            k.Episode = item.EpisodeNumber;
            k.Count++;

            if (!log.Items.TryGetValue(item.ItemId, out var i))
            {
                i = new AirLogItem();
                log.Items[item.ItemId] = i;
            }

            i.LastStartUtc = e.StartUtc;
            i.Count++;
            logged = true;
            if (latest is null || e.StartUtc > latest.StartUtc)
            {
                latest = e;
            }
        }

        if (latest is not null)
        {
            log.LastProgramId = latest.ProgramId;
        }

        log.LastTickUtc = nowUtc;
        return logged;
    }

    /// <summary>Where the next look starts: the last tick when it is recent, otherwise now (so a long gap is not back-filled).</summary>
    public static DateTime WindowStart(AirLog log, DateTime nowUtc)
        => log.LastTickUtc is { } last && last <= nowUtc && nowUtc - last <= MaxGap ? last : nowUtc;

    /// <summary>The log frozen at <paramref name="nowUtc"/>, bounded by the compile's program items; null when nothing in it is on the station.</summary>
    public static AirHistorySnapshot? Snapshot(AirLog log, DateTime nowUtc, CompiledStation compiled)
    {
        var ids = new HashSet<Guid>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in compiled.ProgramItems)
        {
            ids.Add(item.ItemId);
            keys.Add(PoolOrdering.KeyOf(item));
        }

        var snapshot = new AirHistorySnapshot { TakenUtc = nowUtc };
        var latest = DateTime.MinValue;
        foreach (var (key, entry) in log.Keys)
        {
            if (!keys.Contains(key) || entry.LastStartUtc > nowUtc)
            {
                continue;
            }

            snapshot.Keys[key] = new SnapshotKey { LastEndUtc = entry.LastEndUtc, LastItemId = entry.LastItemId, Season = entry.Season, Episode = entry.Episode };
            if (entry.LastStartUtc > latest)
            {
                latest = entry.LastStartUtc;
                snapshot.LastKey = key;
            }
        }

        foreach (var (id, entry) in log.Items)
        {
            if (ids.Contains(id) && entry.LastStartUtc <= nowUtc)
            {
                snapshot.Items[id] = entry.LastStartUtc;
            }
        }

        return snapshot.Keys.Count == 0 && snapshot.Items.Count == 0 ? null : snapshot;
    }

    private void SafeTick()
    {
        if (Volatile.Read(ref _disposed) == 1 || Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            var now = DateTime.UtcNow;
            foreach (var station in _stations.GetAll())
            {
                if (!station.Enabled || station.Mode == ScheduleMode.Weather || !SeasonWindows.IsActive(station, now))
                {
                    continue;
                }

                DateTime from;
                lock (_lock)
                {
                    from = WindowStart(Live(station.Id), now);
                }

                var entries = _resolver.Enumerate(station.Id, from, now.AddTicks(1));
                lock (_lock)
                {
                    if (Observe(Live(station.Id), entries, now))
                    {
                        _dirty.Add(station.Id);
                        _published.TryRemove(station.Id, out _);
                    }
                }
            }

            Flush(now, all: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JellyStations: air log tick failed");
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    /// <summary>Writes the logs that changed (or every loaded log on shutdown, so the last tick time survives a restart).</summary>
    private void Flush(DateTime nowUtc, bool all)
    {
        List<AirLog> toWrite;
        lock (_lock)
        {
            toWrite = all ? _logs.Values.ToList() : _dirty.Select(id => _logs.TryGetValue(id, out var l) ? l : null).Where(l => l is not null).Select(l => l!).ToList();
            _dirty.Clear();
        }

        foreach (var log in toWrite)
        {
            try
            {
                lock (_lock)
                {
                    log.Prune(nowUtc);
                    _repository.Save(log);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "JellyStations: could not write the air log of station {Id}", log.StationId);
            }
        }
    }

    /// <summary>Stops the timer and writes every loaded log. Safe to call twice.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _timer?.Dispose();
        try
        {
            Flush(DateTime.UtcNow, all: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JellyStations: could not write the air logs on shutdown");
        }

        GC.SuppressFinalize(this);
    }
}
