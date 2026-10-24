namespace Jellyfin.Plugin.JellyStations.Api.Dto;

/// <summary>Movies and series in the library that no channel plays.</summary>
public sealed class CoverageDto
{
    public DateTime ComputedUtc { get; set; }

    /// <summary>How many stations were counted as covering content.</summary>
    public int Channels { get; set; }

    public CoverageTotalsDto Totals { get; set; } = new();

    /// <summary>Movies on no channel, longest first.</summary>
    public List<UncoveredMovieDto> Movies { get; set; } = new();

    /// <summary>Series with at least one episode on no channel, most missing hours first.</summary>
    public List<UncoveredSeriesDto> Series { get; set; } = new();
}

public sealed class CoverageTotalsDto
{
    public int Movies { get; set; }

    public int MoviesOnChannels { get; set; }

    public int Series { get; set; }

    /// <summary>Series with every episode on a channel.</summary>
    public int SeriesOnChannels { get; set; }

    /// <summary>Series with some but not all episodes on a channel.</summary>
    public int SeriesPartly { get; set; }

    /// <summary>Hours of movies and episodes that no channel plays.</summary>
    public double HoursNotOnChannels { get; set; }

    public double HoursTotal { get; set; }
}

public sealed class UncoveredMovieDto
{
    public Guid Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    public string? Rating { get; set; }

    public List<string> Genres { get; set; } = new();

    public int Minutes { get; set; }

    public string? LibraryName { get; set; }
}

public sealed class UncoveredSeriesDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int? Year { get; set; }

    public string? Rating { get; set; }

    public List<string> Genres { get; set; } = new();

    public int Episodes { get; set; }

    /// <summary>Episodes some channel already plays (0 when the series is on no channel at all).</summary>
    public int EpisodesOnChannels { get; set; }

    /// <summary>Hours of the episodes no channel plays.</summary>
    public double Hours { get; set; }

    public string? LibraryName { get; set; }
}
