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
