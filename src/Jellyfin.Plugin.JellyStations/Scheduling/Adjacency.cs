namespace Jellyfin.Plugin.JellyStations.Scheduling;

/// <summary>
/// Repairs "same thing back to back" in a shuffled sequence by swapping elements, where "the same thing" is
/// whatever <c>keyOf</c> says (the series or movie of an item, or of a marathon run's first item).
/// </summary>
internal static class Adjacency
{
    /// <summary>
    /// Walks the sequence and, wherever an element repeats its predecessor's key, swaps it with the first later
    /// element (then the first earlier one) whose move introduces no new repeat around either position.
    /// </summary>
    public static void Spread<T>(IList<T> list, Func<T, string> keyOf)
    {
        for (var i = 1; i < list.Count; i++)
        {
            if (!Violation(list, keyOf, i, null))
            {
                continue;
            }

            var done = false;
            for (var j = i + 1; j < list.Count && !done; j++)
            {
                done = TrySwap(list, keyOf, i, j, null);
            }

            for (var j = 0; j < i - 1 && !done; j++)
            {
                done = TrySwap(list, keyOf, i, j, null);
            }
        }
    }

    /// <summary>
    /// When the first element repeats <paramref name="prevKey"/> (the last key of the previous pass), swaps it with
    /// the first element that can take its place cleanly. Returns true when the seam needed repair, whether or not a
    /// swap was found.
    /// </summary>
    public static bool FixSeam<T>(IList<T> list, Func<T, string> keyOf, string prevKey)
    {
        if (keyOf(list[0]) != prevKey)
        {
            return false;
        }

        for (var j = 1; j < list.Count; j++)
        {
            if (TrySwap(list, keyOf, 0, j, prevKey))
            {
                break;
            }
        }

        return true;
    }

    /// <summary>True if the element at idx has the same key as its predecessor (or as beforeStart for idx 0).</summary>
    public static bool Violation<T>(IList<T> list, Func<T, string> keyOf, int idx, string? beforeStart)
    {
        var key = keyOf(list[idx]);
        var prev = idx == 0 ? beforeStart : keyOf(list[idx - 1]);
        return prev == key;
    }

    /// <summary>Swaps a and b if doing so introduces no adjacency violation around either position; reverts otherwise.</summary>
    public static bool TrySwap<T>(IList<T> list, Func<T, string> keyOf, int a, int b, string? beforeStart)
    {
        (list[a], list[b]) = (list[b], list[a]);
        foreach (var idx in new[] { a, a + 1, b, b + 1 })
        {
            if (idx >= 0 && idx < list.Count && Violation(list, keyOf, idx, beforeStart))
            {
                (list[a], list[b]) = (list[b], list[a]);
                return false;
            }
        }

        return true;
    }
}
