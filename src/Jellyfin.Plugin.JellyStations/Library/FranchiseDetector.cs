using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JellyStations.Library;

/// <summary>A movie offered to the detector: its title and, when the metadata provider knows one, the collection it belongs to.</summary>
public sealed record FranchiseCandidate(Guid Id, string Title, int? Year, long RuntimeTicks, string? Collection = null);

/// <summary>A detected group of movies sharing a collection or a title prefix.</summary>
public sealed record Franchise(string Name, IReadOnlyList<FranchiseCandidate> Members);

/// <summary>A series offered to <see cref="FranchiseDetector.Universes"/>: its name and how much of it there is.</summary>
public sealed record UniverseSeries(Guid Id, string Name, double Hours);

/// <summary>Shows and the movies that share their name.</summary>
public sealed record Universe(string Name, IReadOnlyList<UniverseSeries> Series, IReadOnlyList<FranchiseCandidate> Movies);

/// <summary>
/// Finds franchises. Movies that name the same collection (TMDB's "James Bond Collection", stamped on every member
/// whether or not a box set exists) group first, two titles being enough, so series whose titles share nothing
/// ("Dr. No", "Goldfinger", "Skyfall") are found. The rest group by title prefix: "Harry Potter and the …" ×8,
/// "The Lord of the Rings: …" ×3, "The Bourne …" ×5, "Iron Man" / "Iron Man 2". A prefix before a colon or dash, or
/// the leading words up to the second one that carries meaning ("Men in Black", never "Pride and" or "Adventures
/// of"), needs two titles; a single leading word needs three so "Black Swan" and "Black Panther" stay apart, and
/// a word any title may open with ("Captain", "Princess", "High") names a franchise only when one of the movies
/// is called just that. Each title joins one group only: the one most titles share. The same movie filed twice
/// (same title and year) counts once.
/// </summary>
public static partial class FranchiseDetector
{
    /// <summary>Collections this small are not worth a marathon.</summary>
    public const int CollectionMinMembers = 2;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "of", "in", "on", "at", "to", "and", "or", "for", "from", "with", "my", "our", "your", "his", "her", "its", "no", "not"
    };

    /// <summary>
    /// Words many unrelated titles open with. Three movies sharing one of these are not a franchise unless one of
    /// them has the word as its whole title ("Twilight", "Taken").
    /// </summary>
    private static readonly HashSet<string> GenericLeadWords = new(StringComparer.Ordinal)
    {
        "about", "adventure", "adventures", "american", "back", "beyond", "black", "blue", "born", "captain", "christmas", "city",
        "dark", "dead", "death", "doctor", "down", "final", "first", "ghost", "girl", "girls", "good", "grand", "great", "green",
        "high", "home", "house", "into", "just", "king", "lady", "last", "legend", "life", "little", "lord", "lost", "love",
        "miss", "mister", "monster", "monsters", "night", "over", "perfect", "pride", "prince", "princess", "queen", "return",
        "saint", "secret", "space", "star", "super", "that", "there", "this", "three", "time", "total", "true", "under",
        "what", "when", "where", "white", "wild", "world", "young"
    };

    /// <summary>The most leading words a multi-word prefix key takes.</summary>
    private const int MaxPrefixWords = 5;

    [GeneratedRegex(@"\s*\((19|20)\d{2}\)\s*$")]
    private static partial Regex YearSuffix();

    [GeneratedRegex(@"^(part|chapter|episode|volume|vol\.?)\s+\S+$|^(part|chapter)$|^[ivxlc]+$|^\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex NumberToken();

    public static IReadOnlyList<Franchise> Detect(IEnumerable<FranchiseCandidate> candidates)
    {
        // The same movie filed twice (two files, two versions) is one title of the franchise.
        var all = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Title))
            .GroupBy(c => (Title: c.Title.Trim().ToLowerInvariant(), c.Year))
            .Select(g => g.First())
            .ToList();

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

        // A strong or multi-word key needs two titles. A single word needs three, and one any title may open with
        // needs a movie called just that.
        bool Enough(string key, List<FranchiseCandidate> group)
        {
            if (strongKeys.Contains(key) || key.Contains(' '))
            {
                return group.Count >= 2;
            }

            return group.Count >= 3 && (!GenericLeadWords.Contains(key) || group.Any(m => Normalize(YearSuffix().Replace(m.Title.Trim(), string.Empty)) == key));
        }

        bool Qualifies(string key) => Enough(key, members[key]);

        // Assign each title to one qualifying key, then re-check sizes after the assignment. Of the strong and
        // multi-word keys the one most titles share wins ("harry potter" ×8 over "harry potter and the deathly
        // hallows" ×2), the longer on a tie; a plain single word is the last resort.
        var assigned = new Dictionary<string, List<FranchiseCandidate>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var qualifying = keysOf[item.Id].Where(k => Qualifies(k.Key)).Select(k => k.Key).ToList();
            var best = qualifying
                .Where(k => k.Contains(' ') || strongKeys.Contains(k))
                .OrderByDescending(k => members[k].Count)
                .ThenByDescending(k => k.Length)
                .FirstOrDefault() ?? qualifying.OrderByDescending(k => k.Length).FirstOrDefault();
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
                .Where(kv => Enough(kv.Key, kv.Value))
                .Select(kv => new Franchise(DisplayName(kv.Key), Ordered(kv.Value))))
            .OrderByDescending(f => f.Members.Sum(m => m.RuntimeTicks))
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Shows and the movies named after them: "Star Trek" (the series, its sister series and the films),
    /// "Futurama" and its four movies. A show answers to its whole name and to the part before a colon or dash;
    /// that part counts when it is more than one word, or when another show shares it or is called just that
    /// ("Avatar: The Last Airbender" alone does not claim the movie "Avatar"). A movie belongs when its title, or
    /// the part before its colon or dash, is the name, or when its title goes on from a name of two words or more
    /// ("Star Trek Into Darkness"). A universe is two shows or more, or one show and at least two movies; anything
    /// less is the show's own channel. Largest first.
    /// </summary>
    public static IReadOnlyList<Universe> Universes(IEnumerable<UniverseSeries> series, IEnumerable<FranchiseCandidate> movies)
    {
        var shows = series.Where(s => !string.IsNullOrWhiteSpace(s.Name)).ToList();
        var films = movies
            .Where(m => !string.IsNullOrWhiteSpace(m.Title))
            .GroupBy(m => (Title: m.Title.Trim().ToLowerInvariant(), m.Year))
            .Select(g => g.First())
            .Select(m =>
            {
                var text = YearSuffix().Replace(m.Title.Trim(), string.Empty);
                return (Movie: m, Whole: Normalize(text), Lead: Normalize(LeadPart(text)));
            })
            .ToList();

        var named = shows.Select(s =>
        {
            var text = YearSuffix().Replace(s.Name.Trim(), string.Empty);
            var lead = LeadPart(text);
            return (Show: s, Whole: Normalize(text), Lead: Normalize(lead), WholeText: text, LeadText: lead?.Trim() ?? string.Empty);
        }).ToList();

        // Every name a universe could go by, with the spelling to show for it.
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in named.Where(n => n.Whole.Length >= 3))
        {
            roots.TryAdd(n.Whole, n.WholeText);
        }

        foreach (var n in named.Where(n => n.Lead.Length >= 3 && n.Lead != n.Whole))
        {
            var shared = named.Count(o => o.Lead == n.Lead || o.Whole == n.Lead) >= 2;
            if (n.Lead.Contains(' ') || shared)
            {
                roots.TryAdd(n.Lead, n.LeadText);
            }
        }

        var result = new List<Universe>();
        foreach (var (root, display) in roots)
        {
            var members = named.Where(n => n.Whole == root || n.Lead == root).Select(n => n.Show).ToList();
            var multiWord = root.Contains(' ');
            var matched = films
                .Where(f => f.Whole == root || f.Lead == root || (multiWord && f.Whole.StartsWith(root + " ", StringComparison.Ordinal)))
                .Select(f => f.Movie)
                .ToList();
            if (members.Count >= 2 || (members.Count == 1 && matched.Count >= 2))
            {
                result.Add(new Universe(display, members.OrderByDescending(s => s.Hours).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(), Ordered(matched)));
            }
        }

        return result
            .OrderByDescending(u => u.Series.Sum(s => s.Hours) + u.Movies.Sum(m => m.RuntimeTicks / (double)TimeSpan.TicksPerHour))
            .ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The part of a title before its first colon or spaced dash; null when it has neither.</summary>
    private static string? LeadPart(string text)
    {
        var cut = text.IndexOf(':');
        foreach (var dash in new[] { " - ", " – ", " — " })
        {
            var at = text.IndexOf(dash, StringComparison.Ordinal);
            if (at > 0 && (cut <= 0 || at < cut))
            {
                cut = at;
            }
        }

        return cut > 0 ? text[..cut] : null;
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

        // The leading words up to the second one that is not a stop word: "harry potter", "men in black",
        // "a charlie brown". Two words ending on "and" or "of" say nothing about what follows.
        var words = Words(Normalize(text));
        var meaningful = 0;
        for (var i = 0; i < words.Count && i < MaxPrefixWords; i++)
        {
            if (!StopWords.Contains(words[i]) && ++meaningful == 2)
            {
                Add(string.Join(' ', words.Take(i + 1)), false);
                break;
            }
        }

        // The whole title, so "Anne of Green Gables" meets "Anne of Green Gables: The Sequel".
        if (words.Count >= 2)
        {
            Add(string.Join(' ', words), false);
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
