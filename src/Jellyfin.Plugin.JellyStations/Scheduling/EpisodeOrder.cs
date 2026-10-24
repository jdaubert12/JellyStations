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
    public static void Apply(int[] order, IReadOnlyList<PlayableItem> items)
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

        foreach (var slots in positions.Values)
        {
            if (slots.Count < 2)
            {
                continue;
            }

            var episodes = Sorted(slots.Select(p => order[p]), items);
            for (var j = 0; j < slots.Count; j++)
            {
                order[slots[j]] = episodes[j];
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
