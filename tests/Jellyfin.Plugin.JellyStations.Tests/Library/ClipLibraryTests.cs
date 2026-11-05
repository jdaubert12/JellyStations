using Jellyfin.Plugin.JellyStations.Library;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Library;

public class ClipLibraryTests
{
    private static readonly Guid MoviesLib = Guid.Parse("33333333-dddd-0000-0000-000000000003");
    private static readonly Guid ShowsLib = Guid.Parse("44444444-dddd-0000-0000-000000000004");
    private static readonly Guid BumpsLib = Guid.Parse("55555555-dddd-0000-0000-000000000005");
    private static readonly Guid HomeLib = Guid.Parse("66666666-dddd-0000-0000-000000000006");

    private static LibraryFact Item(Guid library, string libraryName, double minutes, bool movie = true, Guid? series = null) => new()
    {
        Id = Guid.NewGuid(),
        IsMovie = movie,
        Title = "Item",
        SeriesId = series,
        SeriesName = series.HasValue ? "Shorts" : null,
        Year = 2000,
        Rating = "G",
        RuntimeTicks = TimeSpan.FromMinutes(minutes).Ticks,
        Genres = { "Comedy" },
        LibraryId = library,
        LibraryName = libraryName
    };

    [Fact]
    public void A_library_of_short_clips_is_flagged_and_kept_out_of_the_figures()
    {
        var facts = new List<LibraryFact>
        {
            Item(MoviesLib, "Movies", 120),
            Item(MoviesLib, "Movies", 2), // one short among films does not make a clip library
            Item(BumpsLib, "Bumps", 0.25),
            Item(BumpsLib, "Bumps", 0.5),
            Item(BumpsLib, "Bumps", 1),
            Item(BumpsLib, "Bumps", 3),
            Item(BumpsLib, "Bumps", 4) // one longer piece in five still leaves 80 % clips
        };

        var a = LibraryAnalyzer.Analyze(facts, new List<CollectionFact>(), DateTime.UtcNow);

        var bumps = Assert.Single(a.Libraries, l => l.Id == BumpsLib);
        Assert.True(bumps.ClipLibrary);
        Assert.Equal((5, 4), (bumps.Movies, bumps.Clips));
        var movies = Assert.Single(a.Libraries, l => l.Id == MoviesLib);
        Assert.False(movies.ClipLibrary);
        Assert.Equal(1, movies.Clips);

        // Only the two real movies count anywhere else.
        Assert.Equal(2, a.Totals.Movies);
        Assert.Equal(2, Assert.Single(a.Genres).Movies);
        Assert.Equal(2, Assert.Single(a.Decades).Movies);
        Assert.Equal(2.0, a.KidsHours);
    }

    [Fact]
    public void Plain_videos_count_toward_the_library_but_are_not_programs()
    {
        var facts = new List<LibraryFact> { Item(MoviesLib, "Movies", 120) };
        var others = new List<LibraryFact>
        {
            Item(HomeLib, "Idents", 0.2, movie: false),
            Item(HomeLib, "Idents", 0.3, movie: false),
            Item(HomeLib, "Idents", 0.4, movie: false)
        };

        var a = LibraryAnalyzer.Analyze(facts, new List<CollectionFact>(), DateTime.UtcNow, others);

        var idents = Assert.Single(a.Libraries, l => l.Id == HomeLib);
        Assert.Equal((0, 0, 3, 3, true), (idents.Movies, idents.Episodes, idents.Videos, idents.Clips, idents.ClipLibrary));
        Assert.Equal("Idents", idents.Name);
        Assert.Equal(1, a.Totals.Movies);
        Assert.Equal(0, a.Totals.Episodes);
        Assert.Equal(new[] { HomeLib }, LibraryAnalyzer.ClipLibraryIds(a.Libraries));
    }

    [Fact]
    public void Short_episodes_and_small_or_mixed_libraries_are_not_clip_libraries()
    {
        var series = Guid.NewGuid();
        var facts = new List<LibraryFact>
        {
            // A show of two-minute shorts is still a show.
            Item(ShowsLib, "Shows", 2, movie: false, series: series),
            Item(ShowsLib, "Shows", 2, movie: false, series: series),
            Item(ShowsLib, "Shows", 2, movie: false, series: series),
            // Two clips are too few to call it.
            Item(BumpsLib, "Bumps", 0.5),
            Item(BumpsLib, "Bumps", 0.5),
            // Half clips, half films.
            Item(HomeLib, "Mixed", 1),
            Item(HomeLib, "Mixed", 1),
            Item(HomeLib, "Mixed", 90),
            Item(HomeLib, "Mixed", 95)
        };

        var a = LibraryAnalyzer.Analyze(facts, new List<CollectionFact>(), DateTime.UtcNow);

        Assert.All(a.Libraries, l => Assert.False(l.ClipLibrary));
        Assert.Equal(6, a.Totals.Movies);
        Assert.Equal(3, a.Totals.Episodes);
    }
}
