using Jellyfin.Plugin.JellyStations.Library;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Library;

public class LibraryAnalyzerTests
{
    private static readonly Guid Kids = Guid.Parse("11111111-aaaa-0000-0000-000000000001");
    private static readonly Guid Drama = Guid.Parse("22222222-aaaa-0000-0000-000000000002");
    private static readonly Guid MoviesLib = Guid.Parse("33333333-aaaa-0000-0000-000000000003");
    private static readonly Guid ShowsLib = Guid.Parse("44444444-aaaa-0000-0000-000000000004");

    private static LibraryFact Movie(string title, int year, string? rating, int minutes, params string[] genres) => new()
    {
        Id = Guid.NewGuid(),
        IsMovie = true,
        Title = title,
        Year = year,
        Rating = rating,
        RuntimeTicks = TimeSpan.FromMinutes(minutes).Ticks,
        Genres = genres.ToList(),
        LibraryId = MoviesLib,
        LibraryName = "Movies"
    };

    private static LibraryFact Episode(Guid series, string name, int season, string? rating, int minutes, int? year, params string[] genres) => new()
    {
        Id = Guid.NewGuid(),
        IsMovie = false,
        Title = "Ep",
        SeriesId = series,
        SeriesName = name,
        SeasonNumber = season,
        Rating = rating,
        Year = year,
        RuntimeTicks = TimeSpan.FromMinutes(minutes).Ticks,
        Genres = genres.ToList(),
        LibraryId = ShowsLib,
        LibraryName = "Shows"
    };

    private static List<LibraryFact> Facts()
    {
        var facts = new List<LibraryFact>
        {
            Movie("Coco", 2017, "PG", 120, "Animation", "Family"),
            Movie("Heat", 1995, "R", 180, "Crime"),
            Movie("Alien", 1979, null, 120, "Horror"),
            Movie("Aliens", 1986, "R", 120, "Horror")
        };
        for (var i = 1; i <= 4; i++)
        {
            facts.Add(Episode(Kids, "Bluey", i <= 2 ? 1 : 2, "TV-Y", 30, 2018, "Animation"));
        }

        facts.Add(Episode(Drama, "The Wire", 1, "TV-MA", 60, 2002, "Crime"));
        facts.Add(Episode(Drama, "The Wire", 1, null, 60, null, "Crime"));
        facts[^1].Tags.Add("hbo");
        facts[0].Tags.Add("holiday");
        return facts;
    }

    [Fact]
    public void Totals_KidsAndFamilyHours()
    {
        var a = LibraryAnalyzer.Analyze(Facts(), new List<CollectionFact>(), DateTime.UtcNow);

        Assert.Equal(4, a.Totals.Movies);
        Assert.Equal(9.0, a.Totals.MovieHours);
        Assert.Equal(6, a.Totals.Episodes);
        Assert.Equal(4.0, a.Totals.EpisodeHours);
        Assert.Equal(2, a.Totals.Series);
        Assert.Equal(13.0, a.Totals.Hours);

        Assert.Equal(2.0, a.KidsHours);   // Bluey only
        Assert.Equal(4.0, a.FamilyHours); // Bluey + Coco
        Assert.Equal(3.0, a.UnratedHours); // Alien + one Wire episode
    }

    [Fact]
    public void Genres_CountMoviesEpisodesAndSeries()
    {
        var a = LibraryAnalyzer.Analyze(Facts(), new List<CollectionFact>(), DateTime.UtcNow);

        var animation = a.Genres.Single(g => g.Name == "Animation");
        Assert.Equal(1, animation.Movies);
        Assert.Equal(4, animation.Episodes);
        Assert.Equal(1, animation.Series);
        Assert.Equal(4.0, animation.Hours);

        var crime = a.Genres.Single(g => g.Name == "Crime");
        Assert.Equal(1, crime.Movies);
        Assert.Equal(2, crime.Episodes);
        Assert.Equal(5.0, crime.Hours);
        Assert.Equal("Crime", a.Genres[0].Name); // most hours first
    }

    [Fact]
    public void Series_SummarizeEpisodesSeasonsRatingAndYear()
    {
        var a = LibraryAnalyzer.Analyze(Facts(), new List<CollectionFact>(), DateTime.UtcNow);

        var bluey = a.Series.Single(s => s.Name == "Bluey");
        Assert.Equal(4, bluey.Episodes);
        Assert.Equal(2, bluey.Seasons);
        Assert.Equal(2.0, bluey.Hours);
        Assert.Equal("TV-Y", bluey.Rating);
        Assert.Equal(2018, bluey.Year);
        Assert.Equal("Shows", bluey.LibraryName);

        var wire = a.Series.Single(s => s.Name == "The Wire");
        Assert.Equal("TV-MA", wire.Rating);
        Assert.Equal(2002, wire.Year);
    }

    [Fact]
    public void Decades_Ratings_Tags_Libraries_Collections()
    {
        var facts = Facts();
        var alienIds = facts.Where(f => f.Title.StartsWith("Alien", StringComparison.Ordinal)).Select(f => f.Id).ToList();
        var collections = new List<CollectionFact>
        {
            new() { Id = Guid.NewGuid(), Name = "Alien Collection", ItemIds = alienIds.Append(Guid.NewGuid()).ToList() },
            new() { Id = Guid.NewGuid(), Name = "Empty", ItemIds = new List<Guid> { Guid.NewGuid() } }
        };
        var a = LibraryAnalyzer.Analyze(facts, collections, DateTime.UtcNow);

        Assert.Equal(new[] { 1970, 1980, 1990, 2010 }, a.Decades.Select(d => d.Decade));
        Assert.Equal(1, a.Decades.Single(d => d.Decade == 1990).Movies);

        var r = a.Ratings.Single(x => x.Rating == "R");
        Assert.Equal(2, r.Movies);
        Assert.Equal(5.0, r.MovieHours);
        var unrated = a.Ratings.Single(x => x.Rating == "Unrated");
        Assert.Equal(1, unrated.Movies);
        Assert.Equal(1, unrated.Series);
        Assert.Equal("TV-Y", a.Ratings[0].Rating); // ladder order

        Assert.Equal(1, a.Tags.Single(t => t.Name == "holiday").Movies);
        Assert.Equal(1, a.Tags.Single(t => t.Name == "hbo").Series);

        var movies = a.Libraries.Single(l => l.Name == "Movies");
        Assert.Equal(4, movies.Movies);
        Assert.Equal(0, movies.Episodes);
        Assert.Equal(9.0, movies.Hours);

        var alien = Assert.Single(a.Collections);
        Assert.Equal("Alien Collection", alien.Name);
        Assert.Equal(2, alien.Items); // the unknown id is ignored, the empty collection dropped
        Assert.Equal(4.0, alien.Hours);
    }

    [Fact]
    public void Franchises_ComeFromCollectionNames()
    {
        var facts = Facts();
        var drNo = Movie("Dr. No", 1962, "PG", 110, "Action");
        drNo.CollectionName = "James Bond Collection";
        var goldfinger = Movie("Goldfinger", 1964, "PG", 110, "Action");
        goldfinger.CollectionName = "James Bond Collection";
        facts.Add(drNo);
        facts.Add(goldfinger);
        var a = LibraryAnalyzer.Analyze(facts, new List<CollectionFact>(), DateTime.UtcNow);

        var bond = Assert.Single(a.Franchises, f => f.Name == "James Bond");
        Assert.Equal(new[] { drNo.Id, goldfinger.Id }, bond.ItemIds);
        Assert.Equal(new[] { "Dr. No", "Goldfinger" }, bond.Titles);
    }

    [Fact]
    public void Franchises_ComeFromMovieTitles()
    {
        var facts = Facts();
        facts.Add(Movie("Alien 3", 1992, "R", 115, "Horror"));
        facts.Add(Movie("Alien: Resurrection", 1997, "R", 109, "Horror"));
        var a = LibraryAnalyzer.Analyze(facts, new List<CollectionFact>(), DateTime.UtcNow);

        var alien = Assert.Single(a.Franchises);
        Assert.Equal("Alien", alien.Name);
        Assert.Equal(new[] { "Alien", "Alien 3", "Alien: Resurrection" }, alien.Titles); // "Aliens" shares no prefix
        Assert.Equal(Math.Round((120 + 115 + 109) / 60.0, 1), alien.Hours);
    }
}
