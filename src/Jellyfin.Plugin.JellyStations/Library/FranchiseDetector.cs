using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JellyStations.Library;

/// <summary>A movie offered to the detector: its title and, when the metadata provider knows one, the collection it belongs to.</summary>
public sealed record FranchiseCandidate(Guid Id, string Title, int? Year, long RuntimeTicks, string? Collection = null);

/// <summary>A detected group of movies sharing a collection or a title prefix.</summary>
public sealed record Franchise(string Name, IReadOnlyList<FranchiseCandidate> Members);

/// <summary>
/// Finds franchises. Movies that name the same collection (TMDB's "James Bond Collection", stamped on every member
/// whether or not a box set exists) group first, two titles being enough, so series whose titles share nothing
/// ("Dr. No", "Goldfinger", "Skyfall") are found. The rest group by title prefix: "Harry Potter and the …" ×8,
/// "The Lord of the Rings: …" ×3, "The Bourne …" ×5, "Iron Man" / "Iron Man 2". A prefix before a colon or dash, or
/// of two leading words, needs two titles; a single leading word needs three so "Black Swan" and "Black Panther"
/// stay apart. Each title joins its most specific qualifying group only.
/// </summary>
public static partial class FranchiseDetector
{
    /// <summary>Collections this small are not worth a marathon.</summary>
    public const int CollectionMinMembers = 2;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "of", "in", "on", "at", "to", "and", "or", "for", "from", "with", "my", "our", "your", "his", "her", "its", "no", "not"
    };

    [GeneratedRegex(@"\s*\((19|20)\d{2}\)\s*$")]
    private static partial Regex YearSuffix();

    [GeneratedRegex(@"^(part|chapter|episode|volume|vol\.?)\s+\S+$|^(part|chapter)$|^[ivxlc]+$|^\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex NumberToken();

    public static IReadOnlyList<Franchise> Detect(IEnumerable<FranchiseCandidate> candidates)
    {
        var all = candidates.Where(c => !string.IsNullOrWhiteSpace(c.Title)).ToList();

        // Collections first: exact membership from the metadata provider beats any guess from the titles.
        var byCollection = all
            .Where(c => CollectionKey(c.Collection).Length > 0)
            .GroupBy(c => CollectionKey(c.Collection), StringComparer.Ordinal)
            .Where(g => g.Count() >= CollectionMinMembers)
            .Select(g => new Franchise(CollectionDisplayName(g.First().Collection!), Ordered(g)))
            .ToList();
        var inCollection = new HashSet<Guid>(byCollection.SelectMany(f => f.Members.Select(m => m.Id)));
        var items = all.Where(c => !inCollection.Contains(c.Id)).ToList();

        // Every key a title offers, strongest first; strong keys need two members, a single word needs three.
        var keysOf = new Dictionary<Guid, List<(string Key, bool Strong)>>();
        var members = new Dictionary<string, List<FranchiseCandidate>>(StringComparer.Ordinal);
        var strongKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var keys = KeysOf(item.Title);
            keysOf[item.Id] = keys;
            foreach (var (key, strong) in keys)
            {
                if (!members.TryGetValue(key, out var list))
                {
                    list = new List<FranchiseCandidate>();
                    members[key] = list;
                }

                if (list.All(m => m.Id != item.Id))
                {
                    list.Add(item);
                }

                if (strong)
                {
                    strongKeys.Add(key);
                }
            }
        }

        bool Qualifies(string key) => members[key].Count >= (strongKeys.Contains(key) || key.Contains(' ') ? 2 : 3);

        // Assign each title to its longest qualifying key, then re-check sizes after the assignment.
        var assigned = new Dictionary<string, List<FranchiseCandidate>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var best = keysOf[item.Id].Where(k => Qualifies(k.Key)).Select(k => k.Key).OrderByDescending(k => k.Length).FirstOrDefault();
            if (best is null)
            {
                continue;
            }

            if (!assigned.TryGetValue(best, out var list))
            {
                list = new List<FranchiseCandidate>();
                assigned[best] = list;
            }

            list.Add(item);
        }

        return byCollection
            .Concat(assigned
                .Where(kv => kv.Value.Count >= (strongKeys.Contains(kv.Key) || kv.Key.Contains(' ') ? 2 : 3))
                .Select(kv => new Franchise(DisplayName(kv.Key), Ordered(kv.Value))))
            .OrderByDescending(f => f.Members.Sum(m => m.RuntimeTicks))
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Release order: by year, then title.</summary>
    private static List<FranchiseCandidate> Ordered(IEnumerable<FranchiseCandidate> members)
        => members.OrderBy(m => m.Year ?? int.MaxValue).ThenBy(m => m.Title, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>A collection name as a grouping key: normalized like a title, with the provider's "Collection" suffix dropped.</summary>
    public static string CollectionKey(string? collection) => Normalize(StripCollectionSuffix(collection));

    /// <summary>"James Bond Collection" -> "James Bond"; the name keeps its own spelling otherwise.</summary>
    public static string CollectionDisplayName(string collection)
    {
        var name = StripCollectionSuffix(collection);
        return string.IsNullOrWhiteSpace(name) ? collection.Trim() : name;
    }

    private static string StripCollectionSuffix(string? collection)
    {
        var s = (collection ?? string.Empty).Trim();
        const string suffix = "collection";
        if (s.Length > suffix.Length && s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^suffix.Length].TrimEnd(' ', ':', '-', '–', '—');
        }

        return s;
    }

    /// <summary>Candidate prefixes of a title, as normalized key strings.</summary>
    public static List<(string Key, bool Strong)> KeysOf(string title)
    {
        var keys = new List<(string, bool)>();
        var text = YearSuffix().Replace(title.Trim(), string.Empty);

        void Add(string? raw, bool strong)
        {
            var key = Normalize(raw);
            if (key.Length >= 3 && keys.All(k => k.Item1 != key) && !(key.IndexOf(' ') < 0 && StopWords.Contains(key)))
            {
                keys.Add((key, strong));
            }
        }

        var colon = text.IndexOf(':');
        if (colon > 0)
        {
            Add(text[..colon], true);
        }

        foreach (var dash in new[] { " - ", " – ", " — " })
        {
            var at = text.IndexOf(dash, StringComparison.Ordinal);
            if (at > 0)
            {
                Add(text[..at], true);
                break;
            }
        }

        var words = Words(Normalize(text));
        if (words.Count >= 2)
        {
            Add(words[0] + " " + words[1], false);
        }

        if (words.Count >= 1 && words[0].Length >= 4 && !StopWords.Contains(words[0]))
        {
            Add(words[0], false);
        }

        return keys;
    }

    /// <summary>Lower-case, "the" and punctuation stripped, trailing number / part tokens removed.</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var s = raw.Trim().ToLowerInvariant().Replace("&", " and ", StringComparison.Ordinal);
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c == ' ' || c == '\'' ? c : ' ').ToArray();
        var words = new string(chars).Replace("'", string.Empty, StringComparison.Ordinal).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && words[0] == "the")
        {
            words.RemoveAt(0);
        }

        // "iron man 2", "rocky iv", "kill bill vol 1", "dune part two" -> drop the trailing number tokens.
        while (words.Count > 1 && (NumberToken().IsMatch(words[^1]) || IsNumberWord(words[^1])))
        {
            words.RemoveAt(words.Count - 1);
            if (words.Count > 1 && (words[^1] is "part" or "chapter" or "vol" or "volume" or "episode"))
            {
                words.RemoveAt(words.Count - 1);
            }
        }

        return string.Join(' ', words);
    }

    private static bool IsNumberWord(string w) => w is "one" or "two" or "three" or "four" or "five" or "six" or "seven" or "eight" or "nine" or "ten";

    private static List<string> Words(string normalized) => normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static string DisplayName(string key)
    {
        var words = key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            if (i > 0 && StopWords.Contains(words[i]))
            {
                continue;
            }

            words[i] = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(words[i]);
        }

        return string.Join(' ', words);
    }
}
