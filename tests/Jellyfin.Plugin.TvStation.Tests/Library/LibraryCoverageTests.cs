using Jellyfin.Plugin.TvStation.Library;
using Xunit;

namespace Jellyfin.Plugin.TvStation.Tests.Library;

public class LibraryCoverageTests
{
    private static readonly Guid Bluey = Guid.Parse("11111111-bbbb-0000-0000-000000000001");
    private static readonly Guid Frasier = Guid.Parse("22222222-bbbb-0000-0000-000000000002");
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private static LibraryFact Movie(string title, int minutes, int? year = null, string? rating = null) => new()
    {
        Id = Guid.NewGuid(),
        IsMovie = true,
        Title = title,
        Year = year,
        Rating = rating,
        RuntimeTicks = TimeSpan.FromMinutes(minutes).Ticks,
        Genres = { "Drama" },
        LibraryName = "Movies"
    };

    private static LibraryFact Episode(Guid series, string name, int minutes, string? rating = "TV-G") => new()
    {
        Id = Guid.NewGuid(),
        IsMovie = false,
        Title = "Ep",
        SeriesId = series,
        SeriesName = name,
        Rating = rating,
        RuntimeTicks = TimeSpan.FromMinutes(minutes).Ticks,
        LibraryName = "Shows"
    };

    [Fact]
    public void NothingOnChannels_ListsEverything()
    {
        var facts = new[] { Movie("Heat", 120, 1995, "R"), Episode(Bluey, "Bluey", 7), Episode(Bluey, "Bluey", 7) };

        var c = LibraryCoverage.Build(facts, new HashSet<Guid>(), 0, Now);

        Assert.Equal(0, c.Channels);
        Assert.Single(c.Movies);
        Assert.Equal("Heat", c.Movies[0].Title);
        Assert.Equal(1995, c.Movies[0].Year);
        Assert.Equal("R", c.Movies[0].Rating);
        Assert.Equal(120, c.Movies[0].Minutes);
        var s = Assert.Single(c.Series);
        Assert.Equal("Bluey", s.Name);
        Assert.Equal(2, s.Episodes);
        Assert.Equal(0, s.EpisodesOnChannels);
        Assert.Equal(0.2, s.Hours);
        Assert.Equal(1, c.Totals.Movies);
        Assert.Equal(0, c.Totals.MoviesOnChannels);
        Assert.Equal(1, c.Totals.Series);
        Assert.Equal(0, c.Totals.SeriesOnChannels);
        Assert.Equal(2.2, c.Totals.HoursNotOnChannels);
        Assert.Equal(2.2, c.Totals.HoursTotal);
    }

    [Fact]
    public void CoveredItems_DropOut()
    {
        var heat = Movie("Heat", 120);
        var jaws = Movie("Jaws", 124);
        var b1 = Episode(Bluey, "Bluey", 7);
        var b2 = Episode(Bluey, "Bluey", 7);
        var facts = new[] { heat, jaws, b1, b2 };

        var c = LibraryCoverage.Build(facts, new HashSet<Guid> { heat.Id, b1.Id, b2.Id }, 2, Now);

        Assert.Equal(2, c.Channels);
        Assert.Equal("Jaws", Assert.Single(c.Movies).Title);
        Assert.Empty(c.Series);
        Assert.Equal(1, c.Totals.MoviesOnChannels);
        Assert.Equal(1, c.Totals.SeriesOnChannels);
        Assert.Equal(0, c.Totals.SeriesPartly);
        Assert.Equal(2.1, c.Totals.HoursNotOnChannels);
    }

    [Fact]
    public void PartlyCoveredSeries_ReportsMissingEpisodesOnly()
    {
        var f1 = Episode(Frasier, "Frasier", 22);
        var f2 = Episode(Frasier, "Frasier", 22);
        var f3 = Episode(Frasier, "Frasier", 22);
        var facts = new[] { f1, f2, f3 };

        var c = LibraryCoverage.Build(facts, new HashSet<Guid> { f1.Id }, 1, Now);

        var s = Assert.Single(c.Series);
        Assert.Equal(3, s.Episodes);
        Assert.Equal(1, s.EpisodesOnChannels);
        Assert.Equal(0.7, s.Hours);
        Assert.Equal(1, c.Totals.SeriesPartly);
        Assert.Equal(0, c.Totals.SeriesOnChannels);
        Assert.Equal(0.7, c.Totals.HoursNotOnChannels);
    }

    [Fact]
    public void Lists_AreAlphabetical()
    {
        var facts = new[] { Movie("Zulu", 100), Movie("alien", 100), Movie("Heat", 100), Episode(Frasier, "Frasier", 22), Episode(Bluey, "Bluey", 7) };

        var c = LibraryCoverage.Build(facts, new HashSet<Guid>(), 0, Now);

        Assert.Equal(new[] { "alien", "Heat", "Zulu" }, c.Movies.Select(m => m.Title));
        Assert.Equal(new[] { "Bluey", "Frasier" }, c.Series.Select(s => s.Name));
    }

    [Fact]
    public void EpisodesWithoutSeries_AreIgnored()
    {
        var orphan = Episode(Bluey, "Bluey", 7);
        orphan.SeriesId = null;

        var c = LibraryCoverage.Build(new[] { orphan }, new HashSet<Guid>(), 0, Now);

        Assert.Empty(c.Series);
        Assert.Equal(0, c.Totals.Series);
    }
}
