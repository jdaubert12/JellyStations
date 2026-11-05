using Jellyfin.Plugin.JellyStations.Api.Dto;
using static Jellyfin.Plugin.JellyStations.Library.LibraryFacts;

namespace Jellyfin.Plugin.JellyStations.Library;

/// <summary>Pure aggregation of library facts into the analysis the suggester and health report read.</summary>
public static class LibraryAnalyzer
{
    private const int KidsMaxRank = 2;   // G / TV-G
    private const int FamilyMaxRank = 3; // PG / TV-PG

    /// <summary>The longest item that still counts as a clip (an ident, a bump, a trailer).</summary>
    public const int ClipMaxMinutes = 3;

    /// <summary>A clip library has at least this many items.</summary>
    public const int ClipLibraryMinItems = 3;

    /// <summary>The share of a library's items that must be clips for it to be a clip library.</summary>
    public const double ClipLibraryShare = 0.8;

    /// <param name="facts">Every movie and episode.</param>
    /// <param name="collections">The box sets and what they hold.</param>
    /// <param name="nowUtc">Timestamp for the report.</param>
    /// <param name="otherVideos">Plain videos (neither movie nor episode), which only count toward the library figures.</param>
    public static LibraryAnalysisDto Analyze(IReadOnlyList<LibraryFact> facts, IReadOnlyList<CollectionFact> collections, DateTime nowUtc, IReadOnlyList<LibraryFact>? otherVideos = null)
    {
        var libraries = Libraries(facts, otherVideos ?? Array.Empty<LibraryFact>());
        var clipLibraries = ClipLibraryIds(libraries);
        if (clipLibraries.Count > 0)
        {
            // Idents and bumps are not programs: they stay out of every hours and count figure below.
            facts = facts.Where(f => !InAny(f, clipLibraries)).ToList();
        }

        var movies = facts.Where(f => f.IsMovie).ToList();
        var episodes = facts.Where(f => !f.IsMovie).ToList();
        var result = new LibraryAnalysisDto
        {
            ComputedUtc = nowUtc,
            Totals = new AnalysisTotalsDto
            {
                Movies = movies.Count,
                MovieHours = Hours(movies),
                Episodes = episodes.Count,
                EpisodeHours = Hours(episodes),
                Series = episodes.Select(e => e.SeriesId).Where(s => s.HasValue).Distinct().Count()
            }
        };

        foreach (var f in facts)
        {
            var rank = RatingLadder.Rank(f.Rating);
            var hours = HoursOf(f.RuntimeTicks);
            if (rank is null)
            {
                result.UnratedHours += hours;
                continue;
            }

            if (rank <= KidsMaxRank)
            {
                result.KidsHours += hours;
            }

            if (rank <= FamilyMaxRank)
            {
                result.FamilyHours += hours;
            }
        }

        result.KidsHours = Math.Round(result.KidsHours, 1);
        result.FamilyHours = Math.Round(result.FamilyHours, 1);
        result.UnratedHours = Math.Round(result.UnratedHours, 1);

        result.Genres = Genres(movies, episodes);
        result.Series = Series(episodes);
        result.Collections = Collections(facts, collections);
        result.Franchises = FranchiseDetector
            .Detect(movies.Select(m => new FranchiseCandidate(m.Id, m.Title, m.Year, m.RuntimeTicks, m.CollectionName)))
            .Select(fr => new FranchiseStatDto
            {
                Name = fr.Name,
                ItemIds = fr.Members.Select(m => m.Id).ToList(),
                Titles = fr.Members.Select(m => m.Title).ToList(),
                Hours = Math.Round(fr.Members.Sum(m => HoursOf(m.RuntimeTicks)), 1)
            })
            .ToList();
        result.Decades = movies
            .Where(m => m.Year.HasValue)
            .GroupBy(m => m.Year!.Value / 10 * 10)
            .Select(g => new DecadeStatDto { Decade = g.Key, Movies = g.Count(), Hours = Hours(g) })
            .OrderBy(d => d.Decade)
            .ToList();
        result.Ratings = Ratings(movies, episodes);
        result.Tags = Tags(movies, episodes);
        result.Libraries = libraries;

        return result;
    }

    /// <summary>The ids of the libraries flagged <see cref="LibraryStatDto.ClipLibrary"/>.</summary>
    public static HashSet<Guid> ClipLibraryIds(IEnumerable<LibraryStatDto> libraries)
        => libraries.Where(l => l.ClipLibrary).Select(l => l.Id).ToHashSet();

    private static bool InAny(LibraryFact fact, HashSet<Guid> libraries) => fact.LibraryId is { } id && libraries.Contains(id);

    /// <summary>
    /// Per-library counts over movies, episodes and plain videos. A library is a clip library when it has no
    /// episodes, at least <see cref="ClipLibraryMinItems"/> items, and <see cref="ClipLibraryShare"/> of them run
    /// <see cref="ClipMaxMinutes"/> minutes or less.
    /// </summary>
    private static List<LibraryStatDto> Libraries(IReadOnlyList<LibraryFact> facts, IReadOnlyList<LibraryFact> otherVideos)
    {
        var clipTicks = TimeSpan.FromMinutes(ClipMaxMinutes).Ticks;
        var others = otherVideos.Where(f => f.LibraryId.HasValue).ToLookup(f => f.LibraryId!.Value);
        return facts
            .Where(f => f.LibraryId.HasValue)
            .Select(f => f.LibraryId!.Value)
            .Concat(others.Select(g => g.Key))
            .Distinct()
            .Select(id =>
            {
                var own = facts.Where(f => f.LibraryId == id).ToList();
                var plain = others[id].ToList();
                var all = own.Concat(plain).ToList();
                var stat = new LibraryStatDto
                {
                    Id = id,
                    Name = all.Select(f => f.LibraryName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? string.Empty,
                    Movies = own.Count(f => f.IsMovie),
                    Episodes = own.Count(f => !f.IsMovie),
                    Videos = plain.Count,
                    Clips = all.Count(f => f.RuntimeTicks <= clipTicks),
                    Hours = Hours(all)
                };
                stat.ClipLibrary = stat.Episodes == 0 && all.Count >= ClipLibraryMinItems && stat.Clips >= all.Count * ClipLibraryShare;
                return stat;
            })
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<GenreStatDto> Genres(List<LibraryFact> movies, List<LibraryFact> episodes)
    {
        var stats = new KeyedStats<GenreStatDto>(name => new GenreStatDto { Name = name.Trim() });
        foreach (var m in movies)
        {
            foreach (var g in m.Genres.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var s = stats.Get(g);
                s.Movies++;
                s.MovieHours += HoursOf(m.RuntimeTicks);
            }
        }

        foreach (var e in episodes)
        {
            foreach (var g in e.Genres.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var s = stats.Get(g);
                s.Episodes++;
                s.EpisodeHours += HoursOf(e.RuntimeTicks);
                stats.AddSeries(g, e.SeriesId);
            }
        }

        return stats.Finish((s, series) =>
            {
                s.Series = series;
                s.MovieHours = Math.Round(s.MovieHours, 1);
                s.EpisodeHours = Math.Round(s.EpisodeHours, 1);
            })
            .OrderByDescending(s => s.Hours)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<SeriesStatDto> Series(List<LibraryFact> episodes)
    {
        return episodes
            .Where(e => e.SeriesId.HasValue)
            .GroupBy(e => e.SeriesId!.Value)
            .Select(g =>
            {
                var summary = SummarizeSeries(g);
                return new SeriesStatDto
                {
                    Id = g.Key,
                    Name = summary.Name,
                    Episodes = g.Count(),
                    Seasons = g.Select(e => e.SeasonNumber).Where(s => s.HasValue && s > 0).Distinct().Count(),
                    Hours = Hours(g),
                    Rating = summary.Rating,
                    Year = summary.Year,
                    Genres = summary.Genres,
                    LibraryName = summary.LibraryName
                };
            })
            .OrderByDescending(s => s.Hours)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<CollectionStatDto> Collections(IReadOnlyList<LibraryFact> facts, IReadOnlyList<CollectionFact> collections)
    {
        var ticks = facts.ToDictionary(f => f.Id, f => f.RuntimeTicks);
        return collections
            .Select(c =>
            {
                var known = c.ItemIds.Where(ticks.ContainsKey).Distinct().ToList();
                return new CollectionStatDto { Id = c.Id, Name = c.Name, Items = known.Count, Hours = Math.Round(known.Sum(id => HoursOf(ticks[id])), 1) };
            })
            .Where(c => c.Items > 0)
            .OrderByDescending(c => c.Hours)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Unlike genres and tags, each item has one (normalised) rating key, and episodes add hours but are not counted.</summary>
    private static List<RatingStatDto> Ratings(List<LibraryFact> movies, List<LibraryFact> episodes)
    {
        static string Key(string? rating) => RatingLadder.Normalize(rating) is { } r && RatingLadder.Rank(r).HasValue ? r : "Unrated";

        var stats = new KeyedStats<RatingStatDto>(key => new RatingStatDto { Rating = key });
        foreach (var m in movies)
        {
            var s = stats.Get(Key(m.Rating));
            s.Movies++;
            s.MovieHours += HoursOf(m.RuntimeTicks);
        }

        foreach (var e in episodes)
        {
            var key = Key(e.Rating);
            stats.Get(key).EpisodeHours += HoursOf(e.RuntimeTicks);
            stats.AddSeries(key, e.SeriesId);
        }

        return stats.Finish((s, series) =>
            {
                s.Series = series;
                s.MovieHours = Math.Round(s.MovieHours, 1);
                s.EpisodeHours = Math.Round(s.EpisodeHours, 1);
            })
            .OrderBy(s => RatingLadder.Rank(s.Rating) ?? int.MaxValue)
            .ThenBy(s => s.Rating, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Unlike genres, a tag keeps one hours figure for movies and episodes together and does not count episodes.</summary>
    private static List<TagStatDto> Tags(List<LibraryFact> movies, List<LibraryFact> episodes)
    {
        var stats = new KeyedStats<TagStatDto>(name => new TagStatDto { Name = name.Trim() });
        foreach (var m in movies)
        {
            foreach (var t in m.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var s = stats.Get(t);
                s.Movies++;
                s.Hours += HoursOf(m.RuntimeTicks);
            }
        }

        foreach (var e in episodes)
        {
            foreach (var t in e.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                stats.Get(t).Hours += HoursOf(e.RuntimeTicks);
                stats.AddSeries(t, e.SeriesId);
            }
        }

        return stats.Finish((s, series) =>
            {
                s.Series = series;
                s.Hours = Math.Round(s.Hours, 1);
            })
            .OrderByDescending(s => s.Hours)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static double Hours(IEnumerable<LibraryFact> facts) => Math.Round(facts.Sum(f => HoursOf(f.RuntimeTicks)), 1);

    /// <summary>
    /// Per-key statistics (case-insensitive keys, first-seen order) with the distinct series behind each key, which
    /// <see cref="Finish"/> hands to each stat once accumulation is done.
    /// </summary>
    private sealed class KeyedStats<TStat>
    {
        private readonly Dictionary<string, (TStat Stat, HashSet<Guid> Series)> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Func<string, TStat> _create;

        public KeyedStats(Func<string, TStat> create)
        {
            _create = create;
        }

        /// <summary>The stat for a key, created on first use from the key as first spelled.</summary>
        public TStat Get(string key)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = (_create(key), new HashSet<Guid>());
                _entries[key] = entry;
            }

            return entry.Stat;
        }

        /// <summary>Counts a series under a key already created with <see cref="Get"/> (no-op without a series).</summary>
        public void AddSeries(string key, Guid? seriesId)
        {
            if (seriesId.HasValue)
            {
                _entries[key].Series.Add(seriesId.Value);
            }
        }

        /// <summary>Applies <paramref name="finish"/> to each stat with its distinct series count, and returns the stats.</summary>
        public IEnumerable<TStat> Finish(Action<TStat, int> finish)
        {
            foreach (var (stat, series) in _entries.Values)
            {
                finish(stat, series.Count);
            }

            return _entries.Values.Select(e => e.Stat);
        }
    }
}
