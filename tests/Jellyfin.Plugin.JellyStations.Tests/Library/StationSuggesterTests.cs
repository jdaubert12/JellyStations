using Jellyfin.Plugin.JellyStations.Api.Dto;
using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Library;

public class StationSuggesterTests
{
    private static readonly Guid MoviesLib = Guid.Parse("33333333-aaaa-0000-0000-000000000003");
    private static readonly Guid ShowsLib = Guid.Parse("44444444-aaaa-0000-0000-000000000004");
    private static readonly Guid Bluey = Guid.Parse("11111111-aaaa-0000-0000-000000000001");
    private static readonly Guid Bond = Guid.Parse("55555555-aaaa-0000-0000-000000000005");

    private static LibraryAnalysisDto Analysis()
    {
        return new LibraryAnalysisDto
        {
            KidsHours = 5,
            Genres =
            {
                new GenreStatDto { Name = "Comedy", Movies = 4, MovieHours = 6, Episodes = 40, EpisodeHours = 10, Series = 2 },
                new GenreStatDto { Name = "Drama", Movies = 1, MovieHours = 2 }
            },
            Series =
            {
                new SeriesStatDto { Id = Bluey, Name = "Bluey", Episodes = 30, Seasons = 2, Hours = 3.5, Rating = "TV-Y" },
                new SeriesStatDto { Id = Guid.NewGuid(), Name = "Short Run", Episodes = 5, Hours = 2 }
            },
            Ratings =
            {
                new RatingStatDto { Rating = "G", Movies = 2, MovieHours = 3 },
                new RatingStatDto { Rating = "PG", Movies = 3, MovieHours = 5 },
                new RatingStatDto { Rating = "R", Movies = 10, MovieHours = 20 }
            },
            Decades =
            {
                new DecadeStatDto { Decade = 1980, Movies = 6, Hours = 9 },
                new DecadeStatDto { Decade = 1990, Movies = 1, Hours = 2 }
            },
            Franchises =
            {
                new FranchiseStatDto { Name = "Harry Potter", ItemIds = { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() }, Titles = { "HP 1", "HP 2", "HP 3" }, Hours = 7.5 },
                new FranchiseStatDto { Name = "Rocky", ItemIds = { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() }, Titles = { "Rocky", "Rocky II", "Rocky III" }, Hours = 6 },
                new FranchiseStatDto { Name = "Dune", ItemIds = { Guid.NewGuid(), Guid.NewGuid() }, Titles = { "Dune", "Dune: Part Two" }, Hours = 5.2 }
            },
            Collections = { new CollectionStatDto { Id = Bond, Name = "James Bond", Items = 4, Hours = 8 } },
            Tags =
            {
                new TagStatDto { Name = "christmas", Movies = 3, Series = 1, Hours = 6 },
                new TagStatDto { Name = "xmas", Movies = 1, Hours = 1.5 },
                new TagStatDto { Name = "halloween", Movies = 1, Hours = 0.5 },
                new TagStatDto { Name = "hbo", Movies = 0, Series = 1, Hours = 3 }
            },
            Libraries =
            {
                new LibraryStatDto { Id = MoviesLib, Name = "Movies", Movies = 20, Hours = 30 },
                new LibraryStatDto { Id = ShowsLib, Name = "Shows", Episodes = 75, Hours = 20 }
            }
        };
    }

    private static StationSuggestionDto One(List<StationSuggestionDto> list, string key) => Assert.Single(list, s => s.Key == key);

    [Fact]
    public void Rules_ProduceTheExpectedSet()
    {
        var list = StationSuggester.Suggest(Analysis(), new[] { "comedy" });
        var keys = list.Select(s => s.Key).ToList();

        Assert.Equal(
            new[] { "kids", "family-movies", "genre:Comedy", "series:" + Bluey.ToString("N"), "franchise:Harry Potter", "franchise:Rocky", "franchise-nights", "collection:" + Bond.ToString("N"), "decade:1980", "seasonal:Holiday" },
            keys);

        Assert.True(One(list, "genre:Comedy").Exists); // matched case-insensitively
        Assert.All(list.Where(s => s.Key != "genre:Comedy"), s => Assert.False(s.Exists));
        Assert.DoesNotContain(keys, k => k.StartsWith("genre:Drama", StringComparison.Ordinal)); // 2 h is below the threshold
        Assert.DoesNotContain(keys, k => k == "decade:1990");
        Assert.DoesNotContain(keys, k => k == "franchise:Dune"); // two titles: nights only
        Assert.DoesNotContain(keys, k => k == "seasonal:Halloween"); // half an hour
    }

    [Fact]
    public void Kids_UsesEveryLibrary_WithARatingCap()
    {
        var kids = One(StationSuggester.Suggest(Analysis(), Array.Empty<string>()), "kids");
        Assert.Equal("Kids TV", kids.Name);
        Assert.Equal(5, kids.Hours);
        Assert.Equal(ScheduleMode.Pool, kids.Station.Mode);
        Assert.Equal(PlayOrder.ShuffleEpisodesInOrder, kids.Station.Pool.Order);
        Assert.Equal(2, kids.Station.Pool.Sources.Count);
        Assert.All(kids.Station.Pool.Sources, s =>
        {
            Assert.Equal(SourceKind.Library, s.Kind);
            Assert.Equal("G", s.MaxRating);
            Assert.False(s.AllowUnrated);
        });

        var family = One(StationSuggester.Suggest(Analysis(), Array.Empty<string>()), "family-movies");
        Assert.Equal(8, family.Hours); // G + PG movie hours
        var source = Assert.Single(family.Station.Pool.Sources);
        Assert.Equal(MoviesLib, source.ItemId);
        Assert.Equal(new[] { SourceKind.Movie }, source.ItemKinds);
        Assert.Equal("PG", source.MaxRating);
    }

    [Fact]
    public void Genre_Series_Collection_Decade_Shapes()
    {
        var list = StationSuggester.Suggest(Analysis(), Array.Empty<string>());

        var comedy = One(list, "genre:Comedy");
        Assert.Equal(16, comedy.Hours);
        var genreSource = Assert.Single(comedy.Station.Pool.Sources);
        Assert.Equal(SourceKind.Genre, genreSource.Kind);
        Assert.Equal("Comedy", genreSource.Genre);
        Assert.Equal(PlayOrder.ShuffleEpisodesInOrder, comedy.Station.Pool.Order);
        Assert.Contains("4 movies and 40 episodes from 2 series", comedy.Description, StringComparison.Ordinal);

        var bluey = One(list, "series:" + Bluey.ToString("N"));
        Assert.Equal("Bluey", bluey.Name);
        Assert.Equal(PlayOrder.Sequential, bluey.Station.Pool.Order);
        Assert.Equal(Bluey, Assert.Single(bluey.Station.Pool.Sources).ItemId);
        Assert.Contains("rated TV-Y", bluey.Description, StringComparison.Ordinal);

        var bond = One(list, "collection:" + Bond.ToString("N"));
        Assert.Equal(SourceKind.BoxSet, Assert.Single(bond.Station.Pool.Sources).Kind);
        Assert.Equal(PlayOrder.Sequential, bond.Station.Pool.Order);

        var eighties = One(list, "decade:1980");
        Assert.Equal("1980s Movies", eighties.Name);
        var decadeSource = Assert.Single(eighties.Station.Pool.Sources);
        Assert.Equal(1980, decadeSource.YearFrom);
        Assert.Equal(1989, decadeSource.YearTo);
        Assert.Equal(new[] { SourceKind.Movie }, decadeSource.ItemKinds);
    }

    [Fact]
    public void Franchises_MarathonLineups_AndNightlySlots()
    {
        var list = StationSuggester.Suggest(Analysis(), Array.Empty<string>());

        var potter = One(list, "franchise:Harry Potter");
        Assert.Equal("Harry Potter Marathon", potter.Name);
        Assert.Equal(ScheduleMode.Lineup, potter.Station.Mode);
        Assert.Equal(new[] { "HP 1", "HP 2", "HP 3" }, potter.Station.Lineup.Items.Select(i => i.DisplayName));

        var nights = One(list, "franchise-nights");
        Assert.Equal(ScheduleMode.Slots, nights.Station.Mode);
        var blocks = nights.Station.Slots.Blocks;
        Assert.Equal(3, blocks.Count);
        Assert.Equal(new[] { "Harry Potter", "Rocky", "Dune" }, blocks.Select(b => b.Name)); // longest first
        Assert.Equal(new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday }, blocks.Select(b => Assert.Single(b.Days)));
        Assert.All(blocks, b => Assert.Equal("19:00", b.StartLocal));
        Assert.Equal(360, blocks[0].DurationMinutes); // 7.5 h capped at six hours
        Assert.Equal(330, blocks[2].DurationMinutes); // 5.2 h rounded up to the half hour
        Assert.Equal(3, blocks[0].Sources.Count);
        Assert.All(blocks[0].Sources, s => Assert.Equal(SourceKind.Movie, s.Kind));
        var filler = Assert.Single(nights.Station.Slots.Filler.Sources);
        Assert.Equal(MoviesLib, filler.ItemId);
        Assert.Contains("Mon Harry Potter, Tue Rocky, Wed Dune", nights.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Seasonal_GroupsHolidayTags_AndSetsTheWindow()
    {
        var holiday = One(StationSuggester.Suggest(Analysis(), Array.Empty<string>()), "seasonal:Holiday");
        Assert.Equal("Holiday", holiday.Name);
        Assert.Equal(7.5, holiday.Hours);
        Assert.Equal(new[] { "christmas", "xmas" }, holiday.Station.Pool.Sources.Select(s => s.Tag));
        Assert.All(holiday.Station.Pool.Sources, s => Assert.Equal(SourceKind.Tag, s.Kind));
        Assert.False(holiday.Station.UseGlobalExcludedTags);
        var window = Assert.Single(holiday.Station.ActiveWindows);
        Assert.Equal((12, 1, 1, 6), (window.StartMonth, window.StartDay, window.EndMonth, window.EndDay));
        Assert.Contains("Dec 1 to Jan 6", holiday.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Thresholds_AreHonoured_AndEmptyLibraryYieldsNothing()
    {
        var strict = new SuggestionThresholds { GenreMinHours = 100, SeriesMinEpisodes = 100, KidsMinHours = 100, FamilyMoviesMinHours = 100, DecadeMinHours = 100, FranchiseMinTitles = 10, CollectionMinItems = 10, SeasonalMinHours = 100 };
        var list = StationSuggester.Suggest(Analysis(), Array.Empty<string>(), strict);
        Assert.Equal(new[] { "franchise-nights" }, list.Select(s => s.Key)); // nights need only two franchises

        Assert.Empty(StationSuggester.Suggest(new LibraryAnalysisDto(), Array.Empty<string>()));
    }
}
