using Jellyfin.Plugin.JellyStations.Api.Dto;

namespace Jellyfin.Plugin.JellyStations.Library;

/// <summary>Pure aggregation of library facts into the analysis the suggester and health report read.</summary>
public static class LibraryAnalyzer
{
    private const int KidsMaxRank = 2;   // G / TV-G
    private const int FamilyMaxRank = 3; // PG / TV-PG

    public static LibraryAnalysisDto Analyze(IReadOnlyList<LibraryFact> facts, IReadOnlyList<CollectionFact> collections, DateTime nowUtc)
    {
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
            .Detect(movies.Select(m => new FranchiseCandidate(m.Id, m.Title, m.Year, m.RuntimeTicks)))
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
        result.Libraries = facts
            .Where(f => f.LibraryId.HasValue)
            .GroupBy(f => f.LibraryId!.Value)
            .Select(g => new LibraryStatDto
            {
                Id = g.Key,
                Name = g.First().LibraryName ?? string.Empty,
                Movies = g.Count(f => f.IsMovie),
                Episodes = g.Count(f => !f.IsMovie),
                Hours = Hours(g)
            })
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return result;
    }

    private static List<GenreStatDto> Genres(List<LibraryFact> movies, List<LibraryFact> episodes)
    {
        var stats = new Dictionary<string, GenreStatDto>(StringComparer.OrdinalIgnoreCase);
        var seriesPerGenre = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);

        GenreStatDto Stat(string name)
        {
            if (!stats.TryGetValue(name, out var s))
            {
                s = new GenreStatDto { Name = name.Trim() };
                stats[name] = s;
                seriesPerGenre[name] = new HashSet<Guid>();
            }

            return s;
        }

        foreach (var m in movies)
        {
            foreach (var g in m.Genres.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var s = Stat(g);
                s.Movies++;
                s.MovieHours += HoursOf(m.RuntimeTicks);
            }
        }

        foreach (var e in episodes)
        {
            foreach (var g in e.Genres.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var s = Stat(g);
                s.Episodes++;
                s.EpisodeHours += HoursOf(e.RuntimeTicks);
                if (e.SeriesId.HasValue)
                {
                    seriesPerGenre[g].Add(e.SeriesId.Value);
                }
            }
        }

        foreach (var kv in stats)
        {
            kv.Value.Series = seriesPerGenre[kv.Key].Count;
            kv.Value.MovieHours = Math.Round(kv.Value.MovieHours, 1);
            kv.Value.EpisodeHours = Math.Round(kv.Value.EpisodeHours, 1);
        }

        return stats.Values.OrderByDescending(s => s.Hours).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<SeriesStatDto> Series(List<LibraryFact> episodes)
    {
        return episodes
            .Where(e => e.SeriesId.HasValue)
            .GroupBy(e => e.SeriesId!.Value)
            .Select(g => new SeriesStatDto
            {
                Id = g.Key,
                Name = g.Select(e => e.SeriesName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? string.Empty,
                Episodes = g.Count(),
                Seasons = g.Select(e => e.SeasonNumber).Where(s => s.HasValue && s > 0).Distinct().Count(),
                Hours = Hours(g),
                Rating = g.Select(e => RatingLadder.Normalize(e.Rating)).Where(r => r is not null).GroupBy(r => r).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault(),
                Year = g.Select(e => e.Year).Where(y => y.HasValue).Min(),
                Genres = g.SelectMany(e => e.Genres).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                LibraryName = g.Select(e => e.LibraryName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
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

    private static List<RatingStatDto> Ratings(List<LibraryFact> movies, List<LibraryFact> episodes)
    {
        static string Key(string? rating) => RatingLadder.Normalize(rating) is { } r && RatingLadder.Rank(r).HasValue ? r : "Unrated";

        var stats = new Dictionary<string, RatingStatDto>(StringComparer.OrdinalIgnoreCase);
        var seriesPerRating = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);

        RatingStatDto Stat(string key)
        {
            if (!stats.TryGetValue(key, out var s))
            {
                s = new RatingStatDto { Rating = key };
                stats[key] = s;
                seriesPerRating[key] = new HashSet<Guid>();
            }

            return s;
        }

        foreach (var m in movies)
        {
            var s = Stat(Key(m.Rating));
            s.Movies++;
            s.MovieHours += HoursOf(m.RuntimeTicks);
        }

        foreach (var e in episodes)
        {
            var s = Stat(Key(e.Rating));
            s.EpisodeHours += HoursOf(e.RuntimeTicks);
            if (e.SeriesId.HasValue)
            {
                seriesPerRating[s.Rating].Add(e.SeriesId.Value);
            }
        }

        foreach (var kv in stats)
        {
            kv.Value.Series = seriesPerRating[kv.Key].Count;
            kv.Value.MovieHours = Math.Round(kv.Value.MovieHours, 1);
            kv.Value.EpisodeHours = Math.Round(kv.Value.EpisodeHours, 1);
        }

        return stats.Values
            .OrderBy(s => RatingLadder.Rank(s.Rating) ?? int.MaxValue)
            .ThenBy(s => s.Rating, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<TagStatDto> Tags(List<LibraryFact> movies, List<LibraryFact> episodes)
    {
        var stats = new Dictionary<string, TagStatDto>(StringComparer.OrdinalIgnoreCase);
        var seriesPerTag = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);

        TagStatDto Stat(string name)
        {
            if (!stats.TryGetValue(name, out var s))
            {
                s = new TagStatDto { Name = name.Trim() };
                stats[name] = s;
                seriesPerTag[name] = new HashSet<Guid>();
            }

            return s;
        }

        foreach (var m in movies)
        {
            foreach (var t in m.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var s = Stat(t);
                s.Movies++;
                s.Hours += HoursOf(m.RuntimeTicks);
            }
        }

        foreach (var e in episodes)
        {
            foreach (var t in e.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var s = Stat(t);
                s.Hours += HoursOf(e.RuntimeTicks);
                if (e.SeriesId.HasValue)
                {
                    seriesPerTag[t].Add(e.SeriesId.Value);
                }
            }
        }

        foreach (var kv in stats)
        {
            kv.Value.Series = seriesPerTag[kv.Key].Count;
            kv.Value.Hours = Math.Round(kv.Value.Hours, 1);
        }

        return stats.Values.OrderByDescending(s => s.Hours).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static double HoursOf(long ticks) => ticks / (double)TimeSpan.TicksPerHour;

    private static double Hours(IEnumerable<LibraryFact> facts) => Math.Round(facts.Sum(f => HoursOf(f.RuntimeTicks)), 1);
}
