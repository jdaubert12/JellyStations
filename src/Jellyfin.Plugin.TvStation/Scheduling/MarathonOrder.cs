using Jellyfin.Plugin.TvStation.Models;

namespace Jellyfin.Plugin.TvStation.Scheduling;

/// <summary>
/// Marathon ordering for shuffled pools: each series is cut into runs of min..max episodes, movies are runs of
/// one, and the runs are shuffled. Runs of the same series are kept apart when requested, and with
/// <see cref="PlayOrder.ShuffleEpisodesInOrder"/> a series' runs also play in episode order.
/// </summary>
internal static class MarathonOrder
{
    public static bool Enabled(PlayOrder order, int min, int max) => order != PlayOrder.Sequential && Math.Max(min, max) > 1;

    /// <summary>Builds the run sequence for one cycle. Depends only on the seed, not on other cycles.</summary>
    public static List<int[]> BuildRuns(IReadOnlyList<PlayableItem> items, PlayOrder order, int min, int max, bool avoidRepeats, ulong seed)
    {
        min = Math.Max(1, min);
        max = Math.Max(min, max);

        var perm = SplitMix64.Permutation(items.Count, seed);
        var lengths = new SplitMix64(seed ^ 0xA5A5A5A5A5A5A5A5UL);
        var runs = new List<int[]>();
        var bySeries = new Dictionary<Guid, List<int>>();
        var seriesOrder = new List<Guid>();
        foreach (var i in perm)
        {
            var series = items[i].SeriesId;
            if (series is null)
            {
                runs.Add(new[] { i });
                continue;
            }

            if (!bySeries.TryGetValue(series.Value, out var list))
            {
                list = new List<int>();
                bySeries[series.Value] = list;
                seriesOrder.Add(series.Value);
            }

            list.Add(i);
        }

        foreach (var series in seriesOrder)
        {
            var episodes = bySeries[series];
            if (order == PlayOrder.ShuffleEpisodesInOrder)
            {
                episodes = EpisodeOrder.Sorted(episodes, items);
            }

            var pos = 0;
            while (pos < episodes.Count)
            {
                var len = Math.Min(episodes.Count - pos, min + lengths.Next(max - min + 1));
                runs.Add(episodes.GetRange(pos, len).ToArray());
                pos += len;
            }
        }

        var shuffled = SplitMix64.Permutation(runs.Count, seed ^ 0x5A5A5A5A5A5A5A5AUL).Select(r => runs[r]).ToList();
        if (avoidRepeats && shuffled.Count > 2)
        {
            for (var i = 1; i < shuffled.Count; i++)
            {
                if (!Violation(shuffled, items, i, null))
                {
                    continue;
                }

                var done = false;
                for (var j = i + 1; j < shuffled.Count && !done; j++)
                {
                    done = TrySwap(shuffled, items, i, j, null);
                }

                for (var j = 0; j < i - 1 && !done; j++)
                {
                    done = TrySwap(shuffled, items, i, j, null);
                }
            }
        }

        if (order == PlayOrder.ShuffleEpisodesInOrder)
        {
            KeepRunOrder(shuffled, items);
        }

        return shuffled;
    }

    /// <summary>Series of the run (null for a movie).</summary>
    public static Guid? SeriesOf(int[] run, IReadOnlyList<PlayableItem> items) => items[run[0]].SeriesId;

    /// <summary>Back-to-back identity of the run: its series, or the movie itself (see <see cref="PoolOrdering.KeyOf"/>).</summary>
    public static string KeyOfRun(int[] run, IReadOnlyList<PlayableItem> items) => PoolOrdering.KeyOf(items[run[0]]);

    /// <summary>
    /// Re-sorts each series' runs into episode order without changing which series plays in which position,
    /// so runs stay in order after shuffling and swapping.
    /// </summary>
    public static void KeepRunOrder(List<int[]> runs, IReadOnlyList<PlayableItem> items)
    {
        var positions = new Dictionary<Guid, List<int>>();
        for (var p = 0; p < runs.Count; p++)
        {
            var series = SeriesOf(runs[p], items);
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

            // Runs were cut from an episode-sorted list, so ranking their first elements with the same sort
            // puts the runs back into episode order.
            var firsts = slots.Select(p => runs[p][0]).ToList();
            var rank = EpisodeOrder.Sorted(firsts, items).Select((idx, r) => (idx, r)).ToDictionary(t => t.idx, t => t.r);
            var ordered = slots.Select(p => runs[p]).OrderBy(run => rank[run[0]]).ToList();
            for (var j = 0; j < slots.Count; j++)
            {
                runs[slots[j]] = ordered[j];
            }
        }
    }

    /// <summary>True if the run at idx is the same series or movie as the run before it (or as beforeStart for idx 0).</summary>
    public static bool Violation(List<int[]> runs, IReadOnlyList<PlayableItem> items, int idx, string? beforeStart)
    {
        var key = KeyOfRun(runs[idx], items);
        var prev = idx == 0 ? beforeStart : KeyOfRun(runs[idx - 1], items);
        return prev == key;
    }

    public static bool TrySwap(List<int[]> runs, IReadOnlyList<PlayableItem> items, int a, int b, string? beforeStart)
    {
        (runs[a], runs[b]) = (runs[b], runs[a]);
        foreach (var idx in new[] { a, a + 1, b, b + 1 })
        {
            if (idx >= 0 && idx < runs.Count && Violation(runs, items, idx, beforeStart))
            {
                (runs[a], runs[b]) = (runs[b], runs[a]);
                return false;
            }
        }

        return true;
    }
}
