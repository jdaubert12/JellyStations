namespace Jellyfin.Plugin.TvStation.Api.Dto;

/// <summary>One pass over the library: the facts station suggestions and the health report are built from.</summary>
public sealed class LibraryAnalysisDto
{
    public DateTime ComputedUtc { get; set; }

    public AnalysisTotalsDto Totals { get; set; } = new();

    /// <summary>Hours of content at or below G / TV-G (movies and episodes).</summary>
    public double KidsHours { get; set; }

    /// <summary>Hours of content at or below PG / TV-PG.</summary>
    public double FamilyHours { get; set; }

    /// <summary>Hours of content without a ladder rating.</summary>
    public double UnratedHours { get; set; }

    public List<GenreStatDto> Genres { get; set; } = new();

    public List<SeriesStatDto> Series { get; set; } = new();

    public List<CollectionStatDto> Collections { get; set; } = new();

    public List<FranchiseStatDto> Franchises { get; set; } = new();

    public List<DecadeStatDto> Decades { get; set; } = new();

    public List<RatingStatDto> Ratings { get; set; } = new();

    public List<TagStatDto> Tags { get; set; } = new();

    public List<LibraryStatDto> Libraries { get; set; } = new();
}

public sealed class AnalysisTotalsDto
{
    public int Movies { get; set; }

    public double MovieHours { get; set; }

    public int Episodes { get; set; }

    public double EpisodeHours { get; set; }

    public int Series { get; set; }

    public double Hours => Math.Round(MovieHours + EpisodeHours, 1);
}

public sealed class GenreStatDto
{
    public string Name { get; set; } = string.Empty;

    public int Movies { get; set; }

    public double MovieHours { get; set; }

    public int Episodes { get; set; }

    public double EpisodeHours { get; set; }

    public int Series { get; set; }

    public double Hours => Math.Round(MovieHours + EpisodeHours, 1);
}

public sealed class SeriesStatDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Episodes { get; set; }

    public int Seasons { get; set; }

    public double Hours { get; set; }

    /// <summary>Most common rating across the episodes (own or inherited from the series).</summary>
    public string? Rating { get; set; }

    public int? Year { get; set; }

    public List<string> Genres { get; set; } = new();

    public string? LibraryName { get; set; }
}

public sealed class CollectionStatDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Items { get; set; }

    public double Hours { get; set; }
}

/// <summary>Movies that share a title prefix ("Harry Potter", "Lord of the Rings", "Bourne").</summary>
public sealed class FranchiseStatDto
{
    public string Name { get; set; } = string.Empty;

    public List<Guid> ItemIds { get; set; } = new();

    public List<string> Titles { get; set; } = new();

    public double Hours { get; set; }
}

public sealed class DecadeStatDto
{
    /// <summary>First year of the decade, e.g. 1980.</summary>
    public int Decade { get; set; }

    public int Movies { get; set; }

    public double Hours { get; set; }
}

public sealed class RatingStatDto
{
    /// <summary>Normalized rating, or "Unrated".</summary>
    public string Rating { get; set; } = string.Empty;

    public int Movies { get; set; }

    public double MovieHours { get; set; }

    public int Series { get; set; }

    public double EpisodeHours { get; set; }
}

public sealed class TagStatDto
{
    public string Name { get; set; } = string.Empty;

    public int Movies { get; set; }

    public int Series { get; set; }

    public double Hours { get; set; }
}

public sealed class LibraryStatDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Movies { get; set; }

    public int Episodes { get; set; }

    public double Hours { get; set; }
}
