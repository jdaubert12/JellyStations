using Jellyfin.Plugin.TvStation.Models;

namespace Jellyfin.Plugin.TvStation.Scheduling;

/// <summary>
/// The order a pool plays in during one pass (cycle): a seeded shuffle, optionally cut into marathon runs, with
/// the same series kept apart (also across the seam from the previous pass) and episodes put in order when asked.
/// Shared by the continuous pool scheduler and the weekly scheduler, which plays the pool between blocks.
/// </summary>
internal static class PoolOrdering
{
    /// <summary>Runs for pass <paramref name="k"/>: marathon runs, or one item per run.</summary>
    public static List<int[]> BuildRuns(IReadOnlyList<PlayableItem> items, PlayOrder order, bool avoidRepeats, int marathonMin, int marathonMax, Func<long, ulong> seed, long k)
    {
        return MarathonOrder.Enabled(order, marathonMin, marathonMax)
            ? BuildMarathonRuns(items, order, avoidRepeats, marathonMin, marathonMax, seed, k)
            : BuildPlainOrder(items, order, avoidRepeats, seed, k).Select(i => new[] { i }).ToList();
    }

    /// <summary>Flat item order for pass <paramref name="k"/>.</summary>
    public static int[] Build(IReadOnlyList<PlayableItem> items, PlayOrder order, bool avoidRepeats, int marathonMin, int marathonMax, Func<long, ulong> seed, long k)
    {
        return BuildRuns(items, order, avoidRepeats, marathonMin, marathonMax, seed, k).SelectMany(r => r).ToArray();
    }

    /// <summary>Marathon runs: shuffle at run level, keeping runs of the same series apart across the pass seam too.</summary>
    private static List<int[]> BuildMarathonRuns(IReadOnlyList<PlayableItem> items, PlayOrder order, bool avoidRepeats, int marathonMin, int marathonMax, Func<long, ulong> seed, long k)
    {
        var runs = MarathonOrder.BuildRuns(items, order, marathonMin, marathonMax, avoidRepeats, seed(k));
        if (avoidRepeats && runs.Count > 2)
        {
            var prev = MarathonOrder.BuildRuns(items, order, marathonMin, marathonMax, avoidRepeats, seed(k - 1));
            var prevSeries = items[prev[^1][^1]].SeriesId;
            if (prevSeries.HasValue && MarathonOrder.SeriesOf(runs[0], items) == prevSeries)
            {
                for (var j = 1; j < runs.Count; j++)
                {
                    if (MarathonOrder.TrySwap(runs, items, 0, j, prevSeries))
                    {
                        break;
                    }
                }

                if (order == PlayOrder.ShuffleEpisodesInOrder)
                {
                    MarathonOrder.KeepRunOrder(runs, items);
                }
            }
        }

        return runs;
    }

    private static int[] BuildPlainOrder(IReadOnlyList<PlayableItem> items, PlayOrder order, bool avoidRepeats, Func<long, ulong> seed, long k)
    {
        var shuffle = order != PlayOrder.Sequential;
        var result = BuildOrder(items, shuffle, avoidRepeats, seed, k);
        if (avoidRepeats && shuffle && items.Count > 2)
        {
            var prevLast = BuildOrder(items, shuffle, avoidRepeats, seed, k - 1);
            var prevSeries = items[prevLast[^1]].SeriesId;
            if (prevSeries.HasValue && items[result[0]].SeriesId == prevSeries)
            {
                for (var j = 1; j < result.Length; j++)
                {
                    if (TrySwap(result, items, 0, j, prevSeries))
                    {
                        break;
                    }
                }
            }
        }

        if (order == PlayOrder.ShuffleEpisodesInOrder)
        {
            // Keeps which series plays in each slot (so the adjacency passes above still hold) but puts episodes in order.
            EpisodeOrder.Apply(result, items);
        }

        return result;
    }

    /// <summary>Order for pass k after the intra-pass "no same series back-to-back" pass (does not depend on other passes).</summary>
    private static int[] BuildOrder(IReadOnlyList<PlayableItem> items, bool shuffle, bool avoidRepeats, Func<long, ulong> seed, long k)
    {
        var order = shuffle
            ? SplitMix64.Permutation(items.Count, seed(k))
            : Enumerable.Range(0, items.Count).ToArray();

        if (avoidRepeats && shuffle && items.Count > 2)
        {
            for (var i = 1; i < order.Length; i++)
            {
                if (!Violation(order, items, i, null))
                {
                    continue;
                }

                var done = false;
                for (var j = i + 1; j < order.Length && !done; j++)
                {
                    done = TrySwap(order, items, i, j, null);
                }

                for (var j = 0; j < i - 1 && !done; j++)
                {
                    done = TrySwap(order, items, i, j, null);
                }
            }
        }

        return order;
    }

    /// <summary>True if the item at idx has the same (non-null) series as its predecessor (or as beforeStart for idx 0).</summary>
    private static bool Violation(int[] order, IReadOnlyList<PlayableItem> items, int idx, Guid? beforeStart)
    {
        var s = items[order[idx]].SeriesId;
        if (!s.HasValue)
        {
            return false;
        }

        var prev = idx == 0 ? beforeStart : items[order[idx - 1]].SeriesId;
        return prev == s;
    }

    /// <summary>Swaps a and b if doing so introduces no adjacency violation around either position; reverts otherwise.</summary>
    private static bool TrySwap(int[] order, IReadOnlyList<PlayableItem> items, int a, int b, Guid? beforeStart)
    {
        (order[a], order[b]) = (order[b], order[a]);
        foreach (var idx in new[] { a, a + 1, b, b + 1 })
        {
            if (idx >= 0 && idx < order.Length && Violation(order, items, idx, beforeStart))
            {
                (order[a], order[b]) = (order[b], order[a]);
                return false;
            }
        }

        return true;
    }
}
