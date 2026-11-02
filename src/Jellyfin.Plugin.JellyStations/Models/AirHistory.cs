namespace Jellyfin.Plugin.JellyStations.Models;

/// <summary>
/// What a station has aired: per series or film (keyed like the scheduler's back-to-back checks, see
/// <c>PoolOrdering.KeyOf</c>) and per item. Written by the recorder once a minute; read only when a snapshot is
/// taken for a new revision, and by the dashboard. Never read by a scheduler live.
/// </summary>
public sealed class AirLog
{
    public const int CurrentFormat = 1;

    public const int MaxKeys = 5000;

    public const int MaxItems = 20000;

    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(365);

    public Guid StationId { get; set; }

    public int Format { get; set; } = CurrentFormat;

    /// <summary>When the recorder last looked at the station's timeline.</summary>
    public DateTime? LastTickUtc { get; set; }

    /// <summary>The program with the latest start the recorder has logged, so a restart never logs it twice.</summary>
    public string? LastProgramId { get; set; }

    public Dictionary<string, AirLogKey> Keys { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<Guid, AirLogItem> Items { get; set; } = new();

    /// <summary>Number of program airings logged (every airing of every key).</summary>
    public long Total => Keys.Values.Sum(k => (long)k.Count);

    /// <summary>Drops entries older than a year and, past the caps, the oldest.</summary>
    public void Prune(DateTime nowUtc)
    {
        var cutoff = nowUtc - MaxAge;
        foreach (var key in Keys.Where(kv => kv.Value.LastStartUtc < cutoff).Select(kv => kv.Key).ToList())
        {
            Keys.Remove(key);
        }

        foreach (var id in Items.Where(kv => kv.Value.LastStartUtc < cutoff).Select(kv => kv.Key).ToList())
        {
            Items.Remove(id);
        }

        if (Keys.Count > MaxKeys)
        {
            foreach (var key in Keys.OrderBy(kv => kv.Value.LastStartUtc).Take(Keys.Count - MaxKeys).Select(kv => kv.Key).ToList())
            {
                Keys.Remove(key);
            }
        }

        if (Items.Count > MaxItems)
        {
            foreach (var id in Items.OrderBy(kv => kv.Value.LastStartUtc).Take(Items.Count - MaxItems).Select(kv => kv.Key).ToList())
            {
                Items.Remove(id);
            }
        }
    }
}

/// <summary>The last airing and the count of one series or film.</summary>
public sealed class AirLogKey
{
    public DateTime LastStartUtc { get; set; }

    /// <summary>Scheduled end of the last airing.</summary>
    public DateTime LastEndUtc { get; set; }

    public int Count { get; set; }

    /// <summary>The item that aired last (the episode, for a series).</summary>
    public Guid LastItemId { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>Season and episode of the item that aired last, kept so a series can be continued by number once that episode is gone.</summary>
    public int? Season { get; set; }

    public int? Episode { get; set; }
}

/// <summary>The last airing and the count of one item.</summary>
public sealed class AirLogItem
{
    public DateTime LastStartUtc { get; set; }

    public int Count { get; set; }
}

/// <summary>
/// The air log frozen into a <see cref="CompiledStation"/> when a revision is bumped, so the new timeline can
/// continue every series from its next episode and rest what just aired. Bounded by the station's content: only
/// keys and items the compile contains are kept. Null in a compiled station means "no history": the schedule is
/// exactly what it was before 1.25.
/// </summary>
public sealed class AirHistorySnapshot
{
    public DateTime TakenUtc { get; set; }

    /// <summary>The series or film airing when the snapshot was taken, or the last one that ended.</summary>
    public string? LastKey { get; set; }

    public Dictionary<string, SnapshotKey> Keys { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Last start per item.</summary>
    public Dictionary<Guid, DateTime> Items { get; set; } = new();
}

public sealed class SnapshotKey
{
    public DateTime LastEndUtc { get; set; }

    public Guid LastItemId { get; set; }

    /// <summary>Copied from the compiled item at snapshot time so a vanished episode can still be placed by number.</summary>
    public int? Season { get; set; }

    public int? Episode { get; set; }
}
