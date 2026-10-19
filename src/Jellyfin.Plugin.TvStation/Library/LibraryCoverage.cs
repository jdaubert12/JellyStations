using Jellyfin.Plugin.TvStation.Api.Dto;

namespace Jellyfin.Plugin.TvStation.Library;

/// <summary>Pure comparison of the library against what the channels play: which movies and series are left out.</summary>
public static class LibraryCoverage
{
    /// <param name="facts">Every movie and episode in the library.</param>
    /// <param name="onChannels">Ids of the movies and episodes some counted station plays.</param>
    /// <param name="channels">How many stations were counted (reported back for the page).</param>
    /// <param name="nowUtc">Timestamp for the report.</param>
    public static CoverageDto Build(IReadOnlyList<LibraryFact> facts, IReadOnlySet<Guid> onChannels, int channels, DateTime nowUtc)
    {
        var result = new CoverageDto { ComputedUtc = nowUtc, Channels = channels };
        var totals = result.Totals;
        double missingHours = 0;
        double allHours = 0;

        foreach (var m in facts.Where(f => f.IsMovie))
        {
            totals.Movies++;
            allHours += HoursOf(m.RuntimeTicks);
            if (onChannels.Contains(m.Id))
            {
                totals.MoviesOnChannels++;
                continue;
            }

            missingHours += HoursOf(m.RuntimeTicks);
            result.Movies.Add(new UncoveredMovieDto
            {
                Id = m.Id,
                Title = m.Title,
                Year = m.Year,
                Rating = RatingLadder.Normalize(m.Rating),
                Genres = m.Genres.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList(),
                Minutes = (int)Math.Round(TimeSpan.FromTicks(m.RuntimeTicks).TotalMinutes),
                LibraryName = m.LibraryName
            });
        }

        foreach (var g in facts.Where(f => !f.IsMovie && f.SeriesId.HasValue).GroupBy(f => f.SeriesId!.Value))
        {
            totals.Series++;
            var episodes = g.ToList();
            allHours += episodes.Sum(e => HoursOf(e.RuntimeTicks));
            var missing = episodes.Where(e => !onChannels.Contains(e.Id)).ToList();
            if (missing.Count == 0)
            {
                totals.SeriesOnChannels++;
                continue;
            }

            if (missing.Count < episodes.Count)
            {
                totals.SeriesPartly++;
            }

            var hours = missing.Sum(e => HoursOf(e.RuntimeTicks));
            missingHours += hours;
            result.Series.Add(new UncoveredSeriesDto
            {
                Id = g.Key,
                Name = episodes.Select(e => e.SeriesName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? string.Empty,
                Year = episodes.Select(e => e.Year).Where(y => y.HasValue).Min(),
                Rating = episodes.Select(e => RatingLadder.Normalize(e.Rating)).Where(r => r is not null).GroupBy(r => r).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault(),
                Genres = episodes.SelectMany(e => e.Genres).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                Episodes = episodes.Count,
                EpisodesOnChannels = episodes.Count - missing.Count,
                Hours = Math.Round(hours, 1),
                LibraryName = episodes.Select(e => e.LibraryName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
            });
        }

        totals.HoursNotOnChannels = Math.Round(missingHours, 1);
        totals.HoursTotal = Math.Round(allHours, 1);
        result.Movies = result.Movies
            .OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Year)
            .ToList();
        result.Series = result.Series
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return result;
    }

    private static double HoursOf(long ticks) => ticks / (double)TimeSpan.TicksPerHour;
}
