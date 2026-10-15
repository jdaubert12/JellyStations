using Jellyfin.Plugin.TvStation.Library;
using Jellyfin.Plugin.TvStation.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.TvStation.Tests.Library;

public class SourceFilterTests
{
    private static List<BaseItem> Episodes(params int[] seasons) => seasons.Select(s => (BaseItem)new Episode { ParentIndexNumber = s }).ToList();

    [Fact]
    public void FilterSeasons_NoSettings_ReturnsEverything()
    {
        var eps = Episodes(0, 1, 2, 3);
        Assert.Equal(eps, LibraryContentResolver.FilterSeasons(eps, new ContentSource { Kind = SourceKind.Series }));
    }

    [Fact]
    public void FilterSeasons_Range_KeepsOnlySeasonsInRange_AndDropsSpecials()
    {
        var eps = Episodes(0, 1, 2, 3, 4, 5);
        var kept = LibraryContentResolver.FilterSeasons(eps, new ContentSource { Kind = SourceKind.Series, SeasonFrom = 2, SeasonTo = 4 }).ToList();
        Assert.Equal(new int?[] { 2, 3, 4 }, kept.Select(e => e.ParentIndexNumber));

        var open = LibraryContentResolver.FilterSeasons(eps, new ContentSource { Kind = SourceKind.Series, SeasonFrom = 4 }).ToList();
        Assert.Equal(new int?[] { 4, 5 }, open.Select(e => e.ParentIndexNumber));
    }

    [Fact]
    public void FilterSeasons_ExcludeSpecials_DropsSeasonZeroOnly()
    {
        var eps = Episodes(0, 1, 0, 2);
        var kept = LibraryContentResolver.FilterSeasons(eps, new ContentSource { Kind = SourceKind.Series, ExcludeSpecials = true }).ToList();
        Assert.Equal(new int?[] { 1, 2 }, kept.Select(e => e.ParentIndexNumber));
    }

    private static Movie Rated(string? rating, int? year = null) => new() { Name = rating ?? "NR", OfficialRating = rating, ProductionYear = year };

    private static readonly Func<BaseItem, BaseItem?> NoSeries = _ => null;

    [Fact]
    public void FilterRatings_MaxRating_AdmitsAtOrBelow_AndDropsUnratedUnlessAllowed()
    {
        var items = new List<BaseItem> { Rated("G"), Rated("PG"), Rated("PG-13"), Rated("R"), Rated("TV-14"), Rated("TV-MA"), Rated(null), Rated("Not Rated") };
        var pg13 = new ContentSource { Kind = SourceKind.Genre, MaxRating = "PG-13" };
        Assert.Equal(new[] { "G", "PG", "PG-13", "TV-14" }, LibraryContentResolver.FilterRatings(items, pg13, NoSeries).Select(i => i.Name));

        pg13.AllowUnrated = true;
        Assert.Equal(new[] { "G", "PG", "PG-13", "TV-14", "NR", "Not Rated" }, LibraryContentResolver.FilterRatings(items, pg13, NoSeries).Select(i => i.Name));

        var none = new ContentSource { Kind = SourceKind.Genre };
        Assert.Equal(items.Count, LibraryContentResolver.FilterRatings(items, none, NoSeries).Count());
    }

    [Fact]
    public void FilterRatings_AllowList_IsExact_AndNrAdmitsUnrated()
    {
        var items = new List<BaseItem> { Rated("G"), Rated("PG"), Rated("R"), Rated(null) };
        var src = new ContentSource { Kind = SourceKind.Playlist, Ratings = { "pg", "R" } };
        Assert.Equal(new[] { "PG", "R" }, LibraryContentResolver.FilterRatings(items, src, NoSeries).Select(i => i.Name));

        src.Ratings.Add("NR");
        Assert.Equal(new[] { "PG", "R", "NR" }, LibraryContentResolver.FilterRatings(items, src, NoSeries).Select(i => i.Name));
    }

    [Fact]
    public void FilterRatings_EpisodeWithoutRating_UsesSeriesRating()
    {
        var series = new Series { OfficialRating = "TV-MA" };
        var ep = new Episode { Name = "ep" };
        var src = new ContentSource { Kind = SourceKind.Series, MaxRating = "PG-13" };
        Assert.Empty(LibraryContentResolver.FilterRatings(new BaseItem[] { ep }, src, _ => series));

        series.OfficialRating = "TV-PG";
        Assert.Single(LibraryContentResolver.FilterRatings(new BaseItem[] { ep }, src, _ => series));
    }

    [Fact]
    public void FilterYears_InclusiveRange_DropsUnknownYears_AndUsesSeriesYearForEpisodes()
    {
        var items = new List<BaseItem> { Rated("a", 1979), Rated("b", 1980), Rated("c", 1999), Rated("d", 2000), Rated("e", null) };
        var src = new ContentSource { Kind = SourceKind.Library, YearFrom = 1980, YearTo = 1999 };
        Assert.Equal(new[] { "b", "c" }, LibraryContentResolver.FilterYears(items, src, NoSeries).Select(i => i.Name));

        var open = new ContentSource { Kind = SourceKind.Library, YearTo = 1989 };
        Assert.Equal(new[] { "a", "b" }, LibraryContentResolver.FilterYears(items, open, NoSeries).Select(i => i.Name));

        var series = new Series { ProductionYear = 1995 };
        var ep = new Episode { Name = "ep", ProductionYear = 2003 };
        Assert.Single(LibraryContentResolver.FilterYears(new BaseItem[] { ep }, src, _ => series));
        Assert.Empty(LibraryContentResolver.FilterYears(new BaseItem[] { ep }, src, _ => new Series { ProductionYear = 2003 }));
    }

    [Fact]
    public void FilterKinds_LimitsContainersToMoviesOrEpisodes()
    {
        var items = new List<BaseItem> { new Movie(), new Episode(), new Movie(), new Episode() };

        Assert.Equal(4, LibraryContentResolver.FilterKinds(items, new ContentSource { Kind = SourceKind.Playlist }).Count());
        Assert.Equal(4, LibraryContentResolver.FilterKinds(items, new ContentSource { Kind = SourceKind.Playlist, ItemKinds = { SourceKind.Movie, SourceKind.Episode } }).Count());
        Assert.All(LibraryContentResolver.FilterKinds(items, new ContentSource { Kind = SourceKind.Playlist, ItemKinds = { SourceKind.Movie } }), i => Assert.IsType<Movie>(i));
        Assert.All(LibraryContentResolver.FilterKinds(items, new ContentSource { Kind = SourceKind.BoxSet, ItemKinds = { SourceKind.Episode } }), i => Assert.IsType<Episode>(i));
    }

    private static PlayableItem Tagged(string title, params string[] tags) => new() { ItemId = Guid.NewGuid(), Title = title, IsMovie = true, Tags = tags.ToList() };

    [Fact]
    public void EffectiveExcludedTags_SkipsHandPicks_TrimsAndDropsOwnTag()
    {
        var tags = new[] { " christmas ", "", "halloween" };
        Assert.Empty(LibraryContentResolver.EffectiveExcludedTags(new ContentSource { Kind = SourceKind.Movie }, tags));
        Assert.Empty(LibraryContentResolver.EffectiveExcludedTags(new ContentSource { Kind = SourceKind.Episode }, tags));
        Assert.Empty(LibraryContentResolver.EffectiveExcludedTags(new ContentSource { Kind = SourceKind.Genre }, null));

        var genre = LibraryContentResolver.EffectiveExcludedTags(new ContentSource { Kind = SourceKind.Genre, Genre = "Family" }, tags);
        Assert.Equal(new[] { "christmas", "halloween" }, genre.OrderBy(t => t));

        var tagSource = LibraryContentResolver.EffectiveExcludedTags(new ContentSource { Kind = SourceKind.Tag, Tag = "Christmas" }, tags);
        Assert.Equal(new[] { "halloween" }, tagSource);
    }

    [Fact]
    public void HasExcludedTag_IsCaseInsensitive_AndFalseWhenNothingExcluded()
    {
        var item = Tagged("A", "Christmas", "snow");
        Assert.True(LibraryContentResolver.HasExcludedTag(item, new[] { "christmas" }));
        Assert.False(LibraryContentResolver.HasExcludedTag(item, new[] { "halloween" }));
        Assert.False(LibraryContentResolver.HasExcludedTag(item, Array.Empty<string>()));
        Assert.False(LibraryContentResolver.HasExcludedTag(Tagged("B"), new[] { "christmas" }));
    }

    [Fact]
    public void StationExcludedTags_MergeWithGlobal_UnlessOptedOut()
    {
        var station = new Station { ExcludedTags = { "halloween", " Christmas" } };

        // Station tags first, then global; duplicates collapse case-insensitively keeping the first spelling.
        Assert.Equal(new[] { "halloween", "Christmas", "gore" }, StationCompiler.EffectiveExcludedTags(station, new[] { "christmas", "gore" }));

        station.UseGlobalExcludedTags = false;
        Assert.Equal(new[] { "halloween", "Christmas" }, StationCompiler.EffectiveExcludedTags(station, new[] { "christmas", "gore" }));
    }
}
