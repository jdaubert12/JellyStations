namespace Jellyfin.Plugin.TvStation.Library;

/// <summary>
/// Orders US movie and TV ratings on one ladder so "at most PG-13" also admits TV-14 and below.
/// Ranks: TV-Y 0, TV-Y7 1, G / TV-G 2, PG / TV-PG 3, PG-13 / TV-14 4, R / TV-MA 5, NC-17 6.
/// Anything else (null, "NR", "Not Rated", "Approved", foreign systems) is unrated and has no rank.
/// </summary>
public static class RatingLadder
{
    private static readonly Dictionary<string, int> Ranks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TV-Y"] = 0,
        ["TV-Y7"] = 1,
        ["TV-Y7-FV"] = 1,
        ["G"] = 2,
        ["TV-G"] = 2,
        ["PG"] = 3,
        ["TV-PG"] = 3,
        ["PG-13"] = 4,
        ["TV-14"] = 4,
        ["R"] = 5,
        ["TV-MA"] = 5,
        ["NC-17"] = 6
    };

    /// <summary>Ladder values in ascending order, for UI dropdowns.</summary>
    public static readonly IReadOnlyList<string> Values = new[] { "TV-Y", "TV-Y7", "G", "TV-G", "PG", "TV-PG", "PG-13", "TV-14", "R", "TV-MA", "NC-17" };

    public static string? Normalize(string? rating)
    {
        if (string.IsNullOrWhiteSpace(rating))
        {
            return null;
        }

        var r = rating.Trim().ToUpperInvariant();
        foreach (var prefix in new[] { "US-", "US:", "USA-" })
        {
            if (r.StartsWith(prefix, StringComparison.Ordinal))
            {
                r = r[prefix.Length..];
            }
        }

        return r.Length == 0 ? null : r;
    }

    public static int? Rank(string? rating)
    {
        var r = Normalize(rating);
        return r is not null && Ranks.TryGetValue(r, out var rank) ? rank : null;
    }

    public static bool IsUnrated(string? rating) => Rank(rating) is null;

    /// <summary>True when <paramref name="rating"/> is on the ladder and at or below <paramref name="max"/>. Unrated never passes here.</summary>
    public static bool IsAtMost(string? rating, string? max)
    {
        var r = Rank(rating);
        var m = Rank(max);
        return r.HasValue && m.HasValue && r <= m;
    }
}
