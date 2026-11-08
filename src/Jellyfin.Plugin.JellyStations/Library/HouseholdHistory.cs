using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Scheduling;
using Jellyfin.Plugin.JellyStations.Storage;

namespace Jellyfin.Plugin.JellyStations.Library;

/// <summary>One household user's state for one item, as Jellyfin keeps it.</summary>
public sealed record WatchState(bool Played, DateTime? LastPlayedUtc);

public sealed record HouseholdUser(Guid Id, string Name);

/// <summary>Reads Jellyfin's per-user play state. The plugin's only window onto who watched what.</summary>
public interface IHouseholdHistory
{
    IReadOnlyList<HouseholdUser> Users();

    /// <summary>
    /// For each item, one state per household user, in the order of <paramref name="userIds"/>; users that no
    /// longer exist are left out of every list. Items Jellyfin does not know are missing from the result.
    /// </summary>
    IReadOnlyDictionary<Guid, IReadOnlyList<WatchState>> Read(IEnumerable<Guid> itemIds, IReadOnlyList<Guid> userIds);
}

/// <summary>
/// What the household's history does to a station, as pure functions over <see cref="WatchState"/>s: which items
/// to leave out as recently finished, where each series stands, and how that position is folded into the air
/// history snapshot so the schedulers pick it up without knowing where it came from.
/// </summary>
public static class HouseholdHistory
{
    /// <summary>True when the station reads the household's history at all: someone is ticked and a toggle is on.</summary>
    public static bool Wanted(Station station, IReadOnlyCollection<Guid>? users)
        => users is { Count: > 0 } && station.Household is { } h && h.Any && station.Mode is ScheduleMode.Pool or ScheduleMode.Slots;

    /// <summary>Watched when everyone in the household has played it. Nobody ticked, or no state: not watched.</summary>
    public static bool Watched(IReadOnlyList<WatchState>? states) => states is { Count: > 0 } && states.All(s => s.Played);

    /// <summary>
    /// When the household finished it: the latest of everyone's last-played dates, once everyone has played it.
    /// Null while someone has not, or when a date is missing (then "recently" cannot be judged and it is kept).
    /// </summary>
    public static DateTime? FinishedUtc(IReadOnlyList<WatchState>? states)
    {
        if (!Watched(states) || states!.Any(s => s.LastPlayedUtc is null))
        {
            return null;
        }

        return states.Max(s => s.LastPlayedUtc!.Value);
    }

    /// <summary>True when the household finished the item within <paramref name="days"/> before <paramref name="nowUtc"/>.</summary>
    public static bool RecentlyFinished(IReadOnlyList<WatchState>? states, int days, DateTime nowUtc)
        => days > 0 && FinishedUtc(states) is { } at && at > nowUtc.AddDays(-days);

    /// <summary>
    /// Drops the items the household finished within <paramref name="days"/>, recording each once as
    /// <see cref="ExclusionReason.HouseholdWatched"/> and keeping it in <see cref="CompiledStation.HouseholdExcluded"/>
    /// for the content view.
    /// </summary>
    public static List<PlayableItem> SkipFinished(List<PlayableItem> items, int days, DateTime nowUtc, IReadOnlyDictionary<Guid, IReadOnlyList<WatchState>> states, CompiledStation compiled)
    {
        if (days <= 0 || items.Count == 0)
        {
            return items;
        }

        var kept = new List<PlayableItem>(items.Count);
        foreach (var item in items)
        {
            states.TryGetValue(item.ItemId, out var s);
            if (!RecentlyFinished(s, days, nowUtc))
            {
                kept.Add(item);
                continue;
            }

            if (compiled.HouseholdExcluded.All(h => h.ItemId != item.ItemId))
            {
                compiled.HouseholdExcluded.Add(item);
                compiled.Excluded.Add(new ExcludedItem { ItemId = item.ItemId, Name = item.DisplayTitle, Reason = ExclusionReason.HouseholdWatched });
            }
        }

        return kept;
    }

    /// <summary>
    /// Where each series stands with the household: the episode before the first one not everyone has watched,
    /// in play order (season and episode, specials last). A series nobody has started, or everyone has finished,
    /// gets no entry: it plays as the station would anyway.
    /// </summary>
    public static Dictionary<string, HouseholdPosition> Progress(IEnumerable<PlayableItem> items, IReadOnlyDictionary<Guid, IReadOnlyList<WatchState>> states)
    {
        var result = new Dictionary<string, HouseholdPosition>(StringComparer.Ordinal);
        var episodes = items.Where(i => i.SeriesId.HasValue).GroupBy(i => i.ItemId).Select(g => g.First()).ToList();
        foreach (var series in episodes.GroupBy(PoolOrdering.KeyOf))
        {
            var list = series.ToList();
            var sorted = EpisodeOrder.Sorted(Enumerable.Range(0, list.Count), list);
            var first = 0;
            while (first < sorted.Count)
            {
                states.TryGetValue(list[sorted[first]].ItemId, out var s);
                if (!Watched(s))
                {
                    break;
                }

                first++;
            }

            if (first == 0 || first >= sorted.Count)
            {
                continue;
            }

            var last = list[sorted[first - 1]];
            result[series.Key] = new HouseholdPosition { ItemId = last.ItemId, Season = last.SeasonNumber, Episode = last.EpisodeNumber };
        }

        return result;
    }

    /// <summary>Same series at the same places.</summary>
    public static bool SameProgress(HouseholdSnapshot? a, HouseholdSnapshot? b)
    {
        var pa = a?.Progress ?? new Dictionary<string, HouseholdPosition>();
        var pb = b?.Progress ?? new Dictionary<string, HouseholdPosition>();
        return pa.Count == pb.Count && pa.All(kv => pb.TryGetValue(kv.Key, out var o) && o.ItemId == kv.Value.ItemId);
    }

    /// <summary>
    /// Folds the household's positions into the air history snapshot the schedulers continue from: for every series
    /// the household is further along in than the station, the snapshot's "aired last" becomes the household's
    /// last watched episode, so the station skips ahead to the first unwatched one and never goes back. Without a
    /// snapshot one is made with nothing aired, taken at <paramref name="nowUtc"/>. The snapshot given is not
    /// changed. Null progress, or none for these items, returns <paramref name="snapshot"/> as it is.
    /// </summary>
    public static AirHistorySnapshot? Apply(AirHistorySnapshot? snapshot, HouseholdSnapshot? household, IReadOnlyList<PlayableItem> items, DateTime nowUtc)
    {
        if (household is null || household.Progress.Count == 0)
        {
            return snapshot;
        }

        AirHistorySnapshot? result = null;
        var distinct = items.Where(i => i.SeriesId.HasValue).GroupBy(i => i.ItemId).Select(g => g.First()).ToList();
        foreach (var series in distinct.GroupBy(PoolOrdering.KeyOf))
        {
            if (!household.Progress.TryGetValue(series.Key, out var position))
            {
                continue;
            }

            var list = series.ToList();
            var sorted = EpisodeOrder.Sorted(Enumerable.Range(0, list.Count), list);
            var ours = Position(sorted, list, new ResumePoint(position.ItemId, position.Season, position.Episode));
            if (ours == 0)
            {
                continue;
            }

            var theirs = 0;
            if (snapshot is not null && snapshot.Keys.TryGetValue(series.Key, out var aired))
            {
                theirs = Position(sorted, list, new ResumePoint(aired.LastItemId, aired.Season, aired.Episode));
            }

            if (ours <= theirs)
            {
                continue;
            }

            result ??= snapshot is null ? new AirHistorySnapshot { TakenUtc = nowUtc } : JsonFileStore.Clone(snapshot);
            if (!result.Keys.TryGetValue(series.Key, out var key))
            {
                key = new SnapshotKey { LastEndUtc = DateTime.MinValue };
                result.Keys[series.Key] = key;
            }

            key.LastItemId = position.ItemId;
            key.Season = position.Season;
            key.Episode = position.Episode;

            // A Sequential block resumes after the item that aired last; drop this series' airings so it reads the
            // series position instead of going back to an episode the household has already seen.
            foreach (var episode in list)
            {
                result.Items.Remove(episode.ItemId);
            }
        }

        return result ?? snapshot;
    }

    /// <summary>
    /// How far into the sorted episode list <paramref name="point"/> reaches: the 1-based place of the episode it
    /// names (by id, else the first episode numbered after it marks the place before it), 0 when it names nothing.
    /// Unlike <see cref="EpisodeOrder.StartIndex(IReadOnlyList{int}, IReadOnlyList{PlayableItem}, ResumePoint)"/>
    /// it never wraps, so two points can be compared.
    /// </summary>
    internal static int Position(IReadOnlyList<int> sorted, IReadOnlyList<PlayableItem> items, ResumePoint point)
    {
        for (var j = 0; j < sorted.Count; j++)
        {
            if (items[sorted[j]].ItemId == point.ItemId)
            {
                return j + 1;
            }
        }

        if (point.Season is null && point.Episode is null)
        {
            return 0;
        }

        var after = (SeasonKey(point.Season), point.Episode ?? int.MaxValue);
        for (var j = 0; j < sorted.Count; j++)
        {
            var key = (SeasonKey(items[sorted[j]].SeasonNumber), items[sorted[j]].EpisodeNumber ?? int.MaxValue);
            if (key.CompareTo(after) > 0)
            {
                return j;
            }
        }

        return sorted.Count;
    }

    private static int SeasonKey(int? season) => season switch
    {
        null => int.MaxValue,
        0 => int.MaxValue - 1,
        var s => s.Value
    };
}
