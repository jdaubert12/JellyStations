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

    private static int SeasonKey(int? season) => season switch
    {
        null => int.MaxValue,
        0 => int.MaxValue - 1,
        var s => s.Value
    };
}
