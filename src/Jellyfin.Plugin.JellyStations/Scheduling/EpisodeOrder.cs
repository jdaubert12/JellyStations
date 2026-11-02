using Jellyfin.Plugin.JellyStations.Models;

namespace Jellyfin.Plugin.JellyStations.Scheduling;

/// <summary>Post-processes a shuffled permutation so every series plays its episodes in order.</summary>
internal static class EpisodeOrder
{
    /// <summary>
    /// Rewrites <paramref name="order"/> in place: for each series, the positions it occupies keep playing that
    /// series, but are refilled with its episodes sorted by season and episode (specials last). Movies and
    /// items without a series are untouched, so adjacency rules applied before this call still hold.
    /// </summary>
    public static void Apply(int[] order, IReadOnlyList<PlayableItem> items) => Reorder(order, items, i => i);

    /// <summary>
    /// Rewrites <paramref name="slots"/> in place: for each series (read from the item each element leads with),
    /// the positions it occupies keep playing that series, but their elements are re-ranked by the play order of
    /// their lead items (see <see cref="Sorted"/>). Elements without a series are untouched.
    /// </summary>
    public static void Reorder<T>(IList<T> slots, IReadOnlyList<PlayableItem> items, Func<T, int> leadItem)
    {
        var positions = new Dictionary<Guid, List<int>>();
        for (var p = 0; p < slots.Count; p++)
        {
            var series = items[leadItem(slots[p])].SeriesId;
            if (series is null)
            {
                continue;
            }

            if (!positions.TryGetValue(series.Value, out var list))
            {
                list = new List<int>();
                positions[series.Value] = list;
            }

            list.Add(p);
        }

        foreach (var held in positions.Values)
        {
            if (held.Count < 2)
            {
                continue;
            }

            var rank = Sorted(held.Select(p => leadItem(slots[p])), items).Select((idx, r) => (idx, r)).ToDictionary(t => t.idx, t => t.r);
            var ordered = held.Select(p => slots[p]).OrderBy(e => rank[leadItem(e)]).ToList();
            for (var j = 0; j < held.Count; j++)
            {
                slots[held[j]] = ordered[j];
            }
        }
    }

    /// <summary>
    /// Sorts item indices of one series into play order: season/episode ascending, specials last. When the
    /// same episode appears more than once (weighted sources), each full pass completes before the next
    /// begins (E1..En, E1..En) instead of repeating episodes back to back.
    /// </summary>
    public static List<int> Sorted(IEnumerable<int> indices, IReadOnlyList<PlayableItem> items)
    {
        var seen = new Dictionary<(int Season, int Episode), int>();
        return indices
            .OrderBy(i => i)
            .Select(i =>
            {
                var key = (SeasonKey(items[i].SeasonNumber), items[i].EpisodeNumber ?? int.MaxValue);
                seen.TryGetValue(key, out var pass);
                seen[key] = pass + 1;
                return (Pass: pass, Season: key.Item1, Episode: key.Item2, Index: i);
            })
            .OrderBy(t => t.Pass)
            .ThenBy(t => t.Season)
            .ThenBy(t => t.Episode)
            .ThenBy(t => t.Index)
            .Select(t => t.Index)
            .ToList();
    }

    /// <summary>
    /// Where a series continues after a revision bump: the index in its sorted episode list after the episode that
    /// aired last. By id when that episode is still there; when it is gone, the first episode numbered after it;
    /// 0 when nothing matches. Weighted duplicates (E1..En, E1..En) resolve in the first pass, so the rotation never
    /// splits a pass.
    /// </summary>
    public static int StartIndex(IReadOnlyList<int> sorted, IReadOnlyList<PlayableItem> items, ResumePoint point) => StartIndex(sorted, items, point, out _);

    /// <summary>As <see cref="StartIndex(IReadOnlyList{int}, IReadOnlyList{PlayableItem}, ResumePoint)"/>; <paramref name="found"/> tells whether anything matched.</summary>
    public static int StartIndex(IReadOnlyList<int> sorted, IReadOnlyList<PlayableItem> items, ResumePoint point, out bool found)
    {
        found = false;
        if (sorted.Count == 0)
        {
            return 0;
        }

        if (point.ItemId is { } id)
        {
            for (var j = 0; j < sorted.Count; j++)
            {
                if (items[sorted[j]].ItemId == id)
                {
                    found = true;
                    return (j + 1) % sorted.Count;
                }
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
                found = true;
                return j;
            }
        }

        return 0;
    }

    /// <summary>
    /// Rotates each series' episodes within the positions it holds in <paramref name="order"/> so that the first
    /// position whose slot starts at or after <paramref name="firstRealStart"/> plays the episode at that series'
    /// offset, filling forward (and wrapping into the earlier, fictional positions). Which series holds each
    /// position never changes, so spacing, gate and marathon runs all hold. Returns the offsets the next cycle
    /// starts at: a series' offset advanced by the number of real positions it held. Series without an offset
    /// start at 0, as a plain <see cref="Apply"/> would place them.
    /// </summary>
    public static Dictionary<Guid, int> Resume(int[] order, IReadOnlyList<PlayableItem> items, long[] starts, IReadOnlyDictionary<Guid, int> offsets, long firstRealStart)
    {
        var positions = new Dictionary<Guid, List<int>>();
        for (var p = 0; p < order.Length; p++)
        {
            var series = items[order[p]].SeriesId;
            if (series is null)
            {
                continue;
            }

            if (!positions.TryGetValue(series.Value, out var list))
            {
                list = new List<int>();
                positions[series.Value] = list;
            }

            list.Add(p);
        }

        var next = new Dictionary<Guid, int>();
        foreach (var (series, held) in positions)
        {
            var n = held.Count;
            var sorted = Sorted(held.Select(p => order[p]), items);
            offsets.TryGetValue(series, out var offset);
            offset = ((offset % n) + n) % n;
            var f = 0;
            while (f < n && starts[held[f]] < firstRealStart)
            {
                f++;
            }

            for (var j = 0; j < n; j++)
            {
                order[held[j]] = sorted[(((offset + j - f) % n) + n) % n];
            }

            next[series] = (offset + (n - f)) % n;
        }

        return next;
    }

    /// <summary>The offsets a snapshot gives each series of <paramref name="items"/>: the episode after the one that aired last.</summary>
    public static Dictionary<Guid, int> OffsetsFrom(AirHistorySnapshot snapshot, IReadOnlyList<PlayableItem> items)
    {
        var result = new Dictionary<Guid, int>();
        foreach (var group in items.Select((item, i) => (item, i)).Where(t => t.item.SeriesId.HasValue).GroupBy(t => t.item.SeriesId!.Value))
        {
            if (!snapshot.Keys.TryGetValue(PoolOrdering.KeyOf(group.First().item), out var key))
            {
                continue;
            }

            var sorted = Sorted(group.Select(t => t.i), items);
            result[group.Key] = StartIndex(sorted, items, new ResumePoint(key.LastItemId, key.Season, key.Episode));
        }

        return result;
    }

    private static int SeasonKey(int? season) => season switch
    {
        null => int.MaxValue,
        0 => int.MaxValue - 1,
        var s => s.Value
    };
}

/// <summary>The last episode of a series that aired, by id and by number (either may be missing).</summary>
internal readonly record struct ResumePoint(Guid? ItemId, int? Season, int? Episode);
