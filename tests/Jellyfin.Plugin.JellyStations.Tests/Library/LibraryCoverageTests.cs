using Jellyfin.Plugin.JellyStations.Library;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Library;

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

    [Fact]
    public void A_partly_played_show_says_what_sets_the_left_out_episodes_apart()
    {
        static LibraryFact Ep(string rating, int? season, bool fromSeries, params string[] genres)
        {
            var e = Episode(Frasier, "Frasier", 22, rating);
            e.SeasonNumber = season;
            e.GenresFromSeries = fromSeries;
            e.Genres = genres.ToList();
            return e;
        }

        // No genres of their own: they go by the show's.
        var on = new[] { Ep("TV-PG", 1, false, "Adventure", "Comedy"), Ep("TV-PG", 1, false, "Adventure", "Sport") };
        var bare = new[] { Ep("TV-PG", 2, true, "Reality", "Talk"), Ep("TV-PG", 2, true, "Reality", "Talk") };
        var facts = on.Concat(bare).ToList();
        var show = Assert.Single(LibraryCoverage.Build(facts, on.Select(e => e.Id).ToHashSet(), 1, Now).Series);
        Assert.Equal("The 2 episodes left have no genres of their own and go by the show's (Reality, Talk). Identify them in Jellyfin, or add the show itself to a channel.", show.Why);
        Assert.StartsWith("The episode left has no genres of its own and goes by the show's", LibraryCoverage.WhyLeftOut(on, bare.Take(1).ToList()), StringComparison.Ordinal);

        // A genre every episode on a channel has, and these do not.
        Assert.Equal("The episode left is not under \"Adventure\" like the episodes on a channel.", LibraryCoverage.WhyLeftOut(on, new[] { Ep("TV-PG", 1, false, "Comedy") }));

        // No rating; specials; nothing to tell apart; a show on no channel at all.
        var same = new[] { "Comedy" };
        var rated = new[] { Ep("TV-PG", 1, false, same), Ep("TV-14", 2, false, same) };
        Assert.StartsWith("The 2 episodes left have no rating", LibraryCoverage.WhyLeftOut(rated, new[] { Ep(null!, 1, false, same), Ep("NR", 1, false, same) }), StringComparison.Ordinal);
        Assert.StartsWith("The episode left is a special", LibraryCoverage.WhyLeftOut(rated, new[] { Ep("TV-PG", 0, false, same) }), StringComparison.Ordinal);
        Assert.Null(LibraryCoverage.WhyLeftOut(rated, new[] { Ep("TV-PG", 3, false, same) }));
        Assert.Null(Assert.Single(LibraryCoverage.Build(facts, new HashSet<Guid>(), 1, Now).Series).Why);
    }

    [Fact]
    public void A_partly_played_show_names_the_episodes_left_out()
    {
        LibraryFact Ep(int season, int number, string title)
        {
            var e = Episode(Frasier, "Frasier", 22);
            e.SeasonNumber = season;
            e.EpisodeNumber = number;
            e.Title = title;
            e.FileName = title + ".mkv";
            return e;
        }

        var on = Ep(1, 1, "Pilot");
        var late = Ep(2, 3, "Late");
        var early = Ep(1, 2, "Early");
        var facts = new List<LibraryFact> { on, late, early };

        var show = Assert.Single(LibraryCoverage.Build(facts, new HashSet<Guid> { on.Id }, 1, Now).Series);
        Assert.Equal(new[] { (1, 2, "Early", "Early.mkv"), (2, 3, "Late", "Late.mkv") }, show.LeftOut.Select(e => (e.Season!.Value, e.Episode!.Value, e.Title, e.FileName!)));
        Assert.Equal(early.Id, show.LeftOut[0].Id);

        // A show on no channel at all is listed whole, with no episodes named; a long list stops at the cap.
        Assert.Empty(Assert.Single(LibraryCoverage.Build(facts, new HashSet<Guid>(), 1, Now).Series).LeftOut);
        facts.AddRange(Enumerable.Range(1, 30).Select(i => Ep(3, i, "More " + i)));
        var many = Assert.Single(LibraryCoverage.Build(facts, new HashSet<Guid> { on.Id }, 1, Now).Series);
        Assert.Equal(LibraryCoverage.MaxLeftOutListed, many.LeftOut.Count);
        Assert.Equal(32, many.Episodes - many.EpisodesOnChannels);
    }
}
