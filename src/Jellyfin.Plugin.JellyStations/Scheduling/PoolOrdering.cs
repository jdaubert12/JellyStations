using Jellyfin.Plugin.JellyStations.Models;

namespace Jellyfin.Plugin.JellyStations.Scheduling;

/// <summary>
/// The order a pool plays in during one pass (cycle): a seeded shuffle, optionally cut into marathon runs, with
/// the same series or movie kept apart (also across the seam from the previous pass) and episodes put in order
/// when asked. Shared by the continuous pool scheduler and the weekly scheduler, which plays the pool between blocks.
/// </summary>
internal static class PoolOrdering
{
    /// <summary>
    /// What "the same thing" means for back-to-back checks: the series for an episode, the title and year for a
    /// movie (so two library entries of one film count as one), the item id for anything else.
    /// </summary>
    public static string KeyOf(PlayableItem item)
    {
        if (item.SeriesId is { } series)
        {
            return "s:" + series.ToString("N");
        }

        var title = item.Title?.Trim();
        return string.IsNullOrEmpty(title)
            ? "i:" + item.ItemId.ToString("N")
            : "m:" + title.ToUpperInvariant() + "|" + (item.ProductionYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
    }

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
            var prevKey = KeyOf(items[prev[^1][^1]]);

            // Unlike the plain order, runs are re-sorted only when the seam needed repair.
            if (Adjacency.FixSeam(runs, run => MarathonOrder.KeyOfRun(run, items), prevKey) && order == PlayOrder.ShuffleEpisodesInOrder)
            {
                MarathonOrder.KeepRunOrder(runs, items);
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
            Adjacency.FixSeam(result, i => KeyOf(items[i]), KeyOf(items[prevLast[^1]]));
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
            Adjacency.Spread(order, i => KeyOf(items[i]));
        }

        return order;
    }
}
