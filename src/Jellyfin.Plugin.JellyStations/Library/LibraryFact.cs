namespace Jellyfin.Plugin.JellyStations.Library;

/// <summary>What the analyzer needs to know about one movie or episode, decoupled from Jellyfin so the analysis is testable.</summary>
public sealed class LibraryFact
{
    public Guid Id { get; set; }

    public bool IsMovie { get; set; }

    /// <summary>Movie title, or episode title.</summary>
    public string Title { get; set; } = string.Empty;

    public Guid? SeriesId { get; set; }

    public string? SeriesName { get; set; }

    public int? SeasonNumber { get; set; }

    /// <summary>Own rating, or the series rating for an episode without one.</summary>
    public string? Rating { get; set; }

    public int? Year { get; set; }

    public long RuntimeTicks { get; set; }

    /// <summary>Own genres, or the series genres for an episode without any.</summary>
    public List<string> Genres { get; set; } = new();

    /// <summary>Own tags, or the series tags for an episode without any.</summary>
    public List<string> Tags { get; set; } = new();

    public Guid? LibraryId { get; set; }

    public string? LibraryName { get; set; }
}

/// <summary>A collection (box set) and the movies/episodes it expands to.</summary>
public sealed class CollectionFact
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public List<Guid> ItemIds { get; set; } = new();
}

/// <summary>What the analysis and the coverage report both say about one series.</summary>
public sealed record SeriesSummary(string Name, int? Year, string? Rating, List<string> Genres, string? LibraryName);

/// <summary>Arithmetic shared by the library analysis and the coverage report.</summary>
public static class LibraryFacts
{
    /// <summary>Ticks as fractional hours.</summary>
    public static double HoursOf(long ticks) => ticks / (double)TimeSpan.TicksPerHour;

    /// <summary>
    /// A series' name and library (first non-blank), earliest year, most common rating and every genre (sorted,
    /// case-insensitively distinct), from its episodes.
    /// </summary>
    public static SeriesSummary SummarizeSeries(IEnumerable<LibraryFact> episodes)
    {
        var list = episodes as IReadOnlyCollection<LibraryFact> ?? episodes.ToList();
        return new SeriesSummary(
            list.Select(e => e.SeriesName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? string.Empty,
            list.Select(e => e.Year).Where(y => y.HasValue).Min(),
            list.Select(e => RatingLadder.Normalize(e.Rating)).Where(r => r is not null).GroupBy(r => r).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault(),
            list.SelectMany(e => e.Genres).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            list.Select(e => e.LibraryName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)));
    }
}
