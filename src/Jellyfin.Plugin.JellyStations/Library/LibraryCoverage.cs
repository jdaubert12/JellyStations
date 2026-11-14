using Jellyfin.Plugin.JellyStations.Api.Dto;
using static Jellyfin.Plugin.JellyStations.Library.LibraryFacts;

namespace Jellyfin.Plugin.JellyStations.Library;

/// <summary>Pure comparison of the library against what the channels play: which movies and series are left out.</summary>
public static class LibraryCoverage
{
    /// <summary>The most left-out episodes named for a partly played show.</summary>
    public const int MaxLeftOutListed = 12;

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
            var summary = SummarizeSeries(episodes);
            result.Series.Add(new UncoveredSeriesDto
            {
                Id = g.Key,
                Name = summary.Name,
                Year = summary.Year,
                Rating = summary.Rating,
                Genres = summary.Genres,
                Episodes = episodes.Count,
                EpisodesOnChannels = episodes.Count - missing.Count,
                Hours = Math.Round(hours, 1),
                LibraryName = summary.LibraryName,
                Why = WhyLeftOut(episodes.Where(e => onChannels.Contains(e.Id)).ToList(), missing),
                LeftOut = missing.Count == episodes.Count
                    ? new List<UncoveredEpisodeDto>()
                    : missing
                        .OrderBy(e => e.SeasonNumber ?? int.MaxValue)
                        .ThenBy(e => e.EpisodeNumber ?? int.MaxValue)
                        .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
                        .Take(MaxLeftOutListed)
                        .Select(e => new UncoveredEpisodeDto { Id = e.Id, Season = e.SeasonNumber, Episode = e.EpisodeNumber, Title = e.Title, FileName = e.FileName })
                        .ToList()
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

    /// <summary>
    /// What sets the episodes no channel plays apart from the ones of the same show that are on one, as a sentence
    /// for the page; null when the show is on no channel at all or nothing tells the two apart. The first that
    /// holds: the ones left have no genres of their own (they go by the show's, which a genre channel built on the
    /// episodes' genres does not match); they lack a genre every episode on a channel has; they have no rating
    /// while the others do; they are the specials.
    /// </summary>
    public static string? WhyLeftOut(IReadOnlyList<LibraryFact> onChannels, IReadOnlyList<LibraryFact> leftOut)
    {
        if (onChannels.Count == 0 || leftOut.Count == 0)
        {
            return null;
        }

        var one = leftOut.Count == 1;
        var subject = one ? "The episode left" : "The " + leftOut.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " episodes left";

        if (leftOut.All(e => e.GenresFromSeries) && onChannels.Any(e => !e.GenresFromSeries))
        {
            var shows = leftOut.SelectMany(e => e.Genres).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
            return subject + (one ? " has" : " have") + " no genres of " + (one ? "its" : "their") + " own"
                + (shows.Count > 0 ? " and " + (one ? "goes" : "go") + " by the show's (" + string.Join(", ", shows) + ")" : string.Empty)
                + ". Identify " + (one ? "it" : "them") + " in Jellyfin, or add the show itself to a channel.";
        }

        var shared = onChannels
            .Select(e => e.Genres.AsEnumerable())
            .Aggregate((a, b) => a.Intersect(b, StringComparer.OrdinalIgnoreCase))
            .Where(g => leftOut.All(e => !e.Genres.Contains(g, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (shared.Count > 0)
        {
            return subject + (one ? " is" : " are") + " not under " + string.Join(" or ", shared.Select(g => "\"" + g + "\"")) + " like the episodes on a channel.";
        }

        if (leftOut.All(e => RatingLadder.Rank(e.Rating) is null) && onChannels.All(e => RatingLadder.Rank(e.Rating) is not null))
        {
            return subject + (one ? " has" : " have") + " no rating, and the channel playing the rest asks for one.";
        }

        if (leftOut.All(e => e.SeasonNumber == 0) && onChannels.All(e => e.SeasonNumber != 0))
        {
            return subject + (one ? " is a special" : " are specials") + ", which the channel playing the rest leaves out.";
        }

        return null;
    }
}
