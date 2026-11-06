using Jellyfin.Plugin.JellyStations.Api.Dto;
using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Library;

public class SuggestionCoverageTests
{
    private static readonly Guid MoviesLib = Guid.Parse("33333333-cccc-0000-0000-000000000003");
    private static readonly Guid ShowsLib = Guid.Parse("44444444-cccc-0000-0000-000000000004");
    private static readonly Guid BumpsLib = Guid.Parse("55555555-cccc-0000-0000-000000000005");
    private static readonly Guid Cheers = Guid.Parse("66666666-cccc-0000-0000-000000000006");
    private static readonly Guid Bluey = Guid.Parse("77777777-cccc-0000-0000-000000000007");
    private static readonly Guid BondSet = Guid.Parse("88888888-cccc-0000-0000-000000000008");

    private static LibraryFact Movie(string title, int minutes, int? year = 2000, string? rating = "PG-13", string[]? genres = null, string[]? tags = null, Guid? library = null) => new()
    {
        Id = Guid.NewGuid(),
        IsMovie = true,
        Title = title,
        Year = year,
        Rating = rating,
        RuntimeTicks = TimeSpan.FromMinutes(minutes).Ticks,
        Genres = (genres ?? Array.Empty<string>()).ToList(),
        Tags = (tags ?? Array.Empty<string>()).ToList(),
        LibraryId = library ?? MoviesLib,
        LibraryName = "Movies"
    };

    private static LibraryFact Episode(Guid series, string name, int season, int minutes = 30, string? rating = "TV-PG", params string[] genres) => new()
    {
        Id = Guid.NewGuid(),
        Title = "Ep",
        SeriesId = series,
        SeriesName = name,
        SeasonNumber = season,
        Year = 1990,
        Rating = rating,
        RuntimeTicks = TimeSpan.FromMinutes(minutes).Ticks,
        Genres = genres.ToList(),
        LibraryId = ShowsLib,
        LibraryName = "Shows"
    };

    private sealed class World
    {
        public LibraryFact Goldfinger { get; } = Movie("Goldfinger", 120, 1964, "PG", new[] { "Action" });

        public LibraryFact Heat { get; } = Movie("Heat", 180, 1995, "R", new[] { "Crime", "action" });

        public LibraryFact Elf { get; } = Movie("Elf", 90, 2003, "PG", new[] { "Comedy" }, new[] { "Christmas" });

        public LibraryFact Unrated { get; } = Movie("Home Movie", 60, null, null, new[] { "Comedy" });

        public List<LibraryFact> CheersEpisodes { get; } = new() { Episode(Cheers, "Cheers", 0), Episode(Cheers, "Cheers", 1, 30, "TV-PG", "Comedy"), Episode(Cheers, "Cheers", 2, 30, "TV-PG", "Comedy") };

        public List<LibraryFact> BlueyEpisodes { get; } = new() { Episode(Bluey, "Bluey", 1, 6, "TV-Y", "Comedy"), Episode(Bluey, "Bluey", 1, 6, "TV-Y", "Comedy") };

        public List<LibraryFact> Facts => new[] { Goldfinger, Heat, Elf, Unrated }.Concat(CheersEpisodes).Concat(BlueyEpisodes).ToList();

        public List<CollectionFact> Collections => new() { new CollectionFact { Id = BondSet, Name = "Bond", ItemIds = { Goldfinger.Id, Guid.NewGuid() } } };

        public FactIndex Index => new(Facts, Collections);
    }

    private static Station Pool(params ContentSource[] sources)
    {
        var station = new Station { Mode = ScheduleMode.Pool };
        station.Pool.Sources.AddRange(sources);
        return station;
    }

    private static ContentSource Source(SourceKind kind, Guid? id = null, string? genre = null, string? tag = null) => new() { Kind = kind, ItemId = id, Genre = genre, Tag = tag };

    [Fact]
    public void A_library_source_honours_kind_rating_and_year()
    {
        var w = new World();
        var index = w.Index;

        Assert.Equal(4, index.ItemsOf(Pool(Source(SourceKind.Library, MoviesLib))).Count);

        var pg = Source(SourceKind.Library, MoviesLib);
        pg.MaxRating = "PG";
        Assert.Equal(new[] { w.Goldfinger.Id, w.Elf.Id }.ToHashSet(), index.ItemsOf(Pool(pg))); // the unrated one does not pass a cap
        pg.AllowUnrated = true;
        Assert.Contains(w.Unrated.Id, index.ItemsOf(Pool(pg)));

        var sixties = Source(SourceKind.Library, MoviesLib);
        sixties.YearFrom = 1960;
        sixties.YearTo = 1969;
        Assert.Equal(w.Goldfinger.Id, Assert.Single(index.ItemsOf(Pool(sixties)))); // no year, no decade

        var episodesOnly = Source(SourceKind.Library, MoviesLib);
        episodesOnly.ItemKinds.Add(SourceKind.Episode);
        Assert.Empty(index.ItemsOf(Pool(episodesOnly)));
    }

    [Fact]
    public void A_source_limited_to_4K_plays_only_the_4K_files()
    {
        var w = new World();
        w.Heat.Width = 3840;
        w.Heat.Height = 1600;
        w.Elf.Width = 1920;
        w.Elf.Height = 1080;
        var index = w.Index;

        var uhd = Source(SourceKind.Library, MoviesLib);
        uhd.MinResolution = "4k";
        Assert.Equal(w.Heat.Id, Assert.Single(index.ItemsOf(Pool(uhd)))); // files of unknown size do not pass

        var fhd = Source(SourceKind.Library, MoviesLib);
        fhd.MinResolution = "1080p";
        Assert.Equal(new[] { w.Heat.Id, w.Elf.Id }.ToHashSet(), index.ItemsOf(Pool(fhd)));
    }

    [Fact]
    public void Genre_tag_series_collection_and_lineup_match_the_facts()
    {
        var w = new World();
        var index = w.Index;

        Assert.Equal(new[] { w.Goldfinger.Id, w.Heat.Id }.ToHashSet(), index.ItemsOf(Pool(Source(SourceKind.Genre, genre: "Action")))); // any case

        var comedyTv = Source(SourceKind.Genre, genre: "Comedy");
        comedyTv.ItemKinds.Add(SourceKind.Episode);
        Assert.Equal(4, index.ItemsOf(Pool(comedyTv)).Count);
        comedyTv.LibraryId = MoviesLib;
        Assert.Empty(index.ItemsOf(Pool(comedyTv)));

        Assert.Equal(w.Elf.Id, Assert.Single(index.ItemsOf(Pool(Source(SourceKind.Tag, tag: "christmas")))));

        Assert.Equal(3, index.ItemsOf(Pool(Source(SourceKind.Series, Cheers))).Count);
        var noSpecials = Source(SourceKind.Series, Cheers);
        noSpecials.ExcludeSpecials = true;
        noSpecials.SeasonTo = 1;
        Assert.Equal(w.CheersEpisodes[1].Id, Assert.Single(index.ItemsOf(Pool(noSpecials))));

        Assert.Equal(w.Goldfinger.Id, Assert.Single(index.ItemsOf(Pool(Source(SourceKind.BoxSet, BondSet))))); // the member the library does not hold is skipped
        Assert.Empty(index.ItemsOf(Pool(Source(SourceKind.Playlist, Guid.NewGuid()))));

        var lineup = new Station { Mode = ScheduleMode.Lineup };
        lineup.Lineup.Items.Add(new LineupItem { ItemId = w.Heat.Id });
        lineup.Lineup.Items.Add(new LineupItem { ItemId = Guid.NewGuid() });
        Assert.Equal(w.Heat.Id, Assert.Single(index.ItemsOf(lineup)));

        var slots = new Station { Mode = ScheduleMode.Slots };
        slots.Slots.Blocks.Add(new SlotBlock { Sources = { Source(SourceKind.Movie, w.Heat.Id) } });
        slots.Slots.Filler.Sources.Add(Source(SourceKind.Series, Bluey));
        Assert.Equal(3, index.ItemsOf(slots).Count);
    }

    [Fact]
    public void Excluded_tags_drop_items_except_hand_picks_and_the_tag_asked_for()
    {
        var w = new World();
        var index = w.Index;
        var global = new[] { "christmas" };

        Assert.DoesNotContain(w.Elf.Id, index.ItemsOf(Pool(Source(SourceKind.Genre, genre: "Comedy")), global));
        Assert.Contains(w.Elf.Id, index.ItemsOf(Pool(Source(SourceKind.Movie, w.Elf.Id)), global));
        Assert.Contains(w.Elf.Id, index.ItemsOf(Pool(Source(SourceKind.Tag, tag: "Christmas")), global));

        var optedOut = Pool(Source(SourceKind.Genre, genre: "Comedy"));
        optedOut.UseGlobalExcludedTags = false;
        Assert.Contains(w.Elf.Id, index.ItemsOf(optedOut, global));

        var ownList = Pool(Source(SourceKind.Library, MoviesLib));
        ownList.ExcludedItemIds = new List<Guid> { w.Heat.Id };
        Assert.DoesNotContain(w.Heat.Id, index.ItemsOf(ownList));
    }

    private static StationSuggestionDto Suggestion(string key, string category, double hours, Station station)
        => new() { Key = key, Category = category, Name = key, Hours = hours, NewHours = hours, Station = station };

    [Fact]
    public void New_hours_count_what_no_channel_plays_and_order_each_category()
    {
        var w = new World();
        var action = Suggestion("genre:Action", "Genre", 99, Pool(Source(SourceKind.Genre, genre: "Action")));   // 5 h, 3 h of it new
        var comedy = Suggestion("genre:Comedy", "Genre", 99, Pool(Source(SourceKind.Genre, genre: "Comedy")));   // 3.7 h, all new
        var crime = Suggestion("genre:Crime", "Genre", 99, Pool(Source(SourceKind.Genre, genre: "Crime")));      // Heat only: new
        var bond = Suggestion("collection:bond", "Collection", 2, Pool(Source(SourceKind.BoxSet, BondSet)));     // Goldfinger only: covered
        var mystery = Suggestion("playlist", "Collection", 7, Pool(Source(SourceKind.Playlist, Guid.NewGuid()))); // unknown to the facts
        var list = new List<StationSuggestionDto> { action, comedy, crime, bond, mystery };

        SuggestionCoverage.Apply(list, w.Index, new HashSet<Guid> { w.Goldfinger.Id });

        Assert.Equal((5d, 3d, false), (action.Hours, action.NewHours, action.Covered));
        Assert.Equal((3.7, 3.7, false), (comedy.Hours, comedy.NewHours, comedy.Covered));
        Assert.Equal((2d, 0d, true), (bond.Hours, bond.NewHours, bond.Covered));
        Assert.Equal((7d, 7d, false), (mystery.Hours, mystery.NewHours, mystery.Covered)); // left as the rule put it
        // Genres by new hours, most first (a tie keeps the rules' order); the collection category stays second.
        Assert.Equal(new[] { "genre:Comedy", "genre:Action", "genre:Crime", "playlist", "collection:bond" }, list.Select(s => s.Key));
    }

    [Fact]
    public void With_no_channels_nothing_is_reordered_or_covered()
    {
        var w = new World();
        var crime = Suggestion("genre:Crime", "Genre", 99, Pool(Source(SourceKind.Genre, genre: "Crime")));
        var comedy = Suggestion("genre:Comedy", "Genre", 99, Pool(Source(SourceKind.Genre, genre: "Comedy")));
        var list = new List<StationSuggestionDto> { crime, comedy };

        SuggestionCoverage.Apply(list, w.Index, new HashSet<Guid>());

        Assert.Equal(new[] { "genre:Crime", "genre:Comedy" }, list.Select(s => s.Key));
        Assert.All(list, s => Assert.False(s.Covered));
        Assert.Equal(3d, crime.NewHours);
    }

    [Fact]
    public void A_sliver_of_new_content_still_shows_as_new()
    {
        var short1 = Movie("Short", 2, genres: new[] { "Shorts" });
        var long1 = Movie("Long", 120, genres: new[] { "Shorts" });
        var s = Suggestion("genre:Shorts", "Genre", 2, Pool(Source(SourceKind.Genre, genre: "Shorts")));

        SuggestionCoverage.Apply(new List<StationSuggestionDto> { s }, new FactIndex(new[] { short1, long1 }), new HashSet<Guid> { long1.Id });

        Assert.Equal((0.1, false), (s.NewHours, s.Covered));
    }

    [Fact]
    public void Leftovers_collect_what_no_channel_plays()
    {
        var w = new World();
        var bump = Movie("Ident", 1, library: BumpsLib);
        var facts = w.Facts.Append(bump).ToList();
        var onChannels = new HashSet<Guid> { w.Goldfinger.Id, w.CheersEpisodes[0].Id, w.BlueyEpisodes[0].Id, w.BlueyEpisodes[1].Id };
        var clipLibraries = new HashSet<Guid> { BumpsLib };

        var left = SuggestionCoverage.Leftovers(facts, clipLibraries, onChannels, 2, 4, new[] { "everything else " }, new[] { "christmas" });

        Assert.NotNull(left);
        Assert.Equal((SuggestionCoverage.LeftoversKey, "Not on a channel", "Everything Else"), (left!.Key, left.Category, left.Name));
        Assert.True(left.Exists);
        // Heat and the unrated movie hand-picked, Cheers as a whole show; Elf is kept off on purpose, the ident is a bumper.
        Assert.Equal(
            new[] { (SourceKind.Movie, (Guid?)w.Heat.Id), (SourceKind.Movie, (Guid?)w.Unrated.Id), (SourceKind.Series, (Guid?)Cheers) },
            left.Station.Pool.Sources.Select(s => (s.Kind, s.ItemId)));
        Assert.Equal(PlayOrder.ShuffleEpisodesInOrder, left.Station.Pool.Order);
        Assert.True(left.Station.PaddingByLength);
        Assert.Equal(5.5, left.Hours);    // 3 + 1 + all 1.5 h of Cheers
        Assert.Equal(5d, left.NewHours);  // one Cheers episode already airs
        Assert.Contains("2 movies and 1 series no channel plays yet", left.Description, StringComparison.Ordinal);
        Assert.Contains("1 of the series is partly on a channel already", left.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Leftovers_need_a_channel_to_compare_with_and_enough_hours()
    {
        var w = new World();
        var none = new HashSet<Guid>();

        Assert.Null(SuggestionCoverage.Leftovers(w.Facts, none, none, 0, 4, Array.Empty<string>()));       // no station yet: that is the whole library
        Assert.Null(SuggestionCoverage.Leftovers(w.Facts, none, none, 1, 100, Array.Empty<string>()));     // not enough left
        Assert.Null(SuggestionCoverage.Leftovers(w.Facts, none, w.Facts.Select(f => f.Id).ToHashSet(), 3, 0.1, Array.Empty<string>()));

        var moviesOnly = SuggestionCoverage.Leftovers(new[] { w.Heat }, none, none, 1, 1, Array.Empty<string>());
        Assert.Equal(PlayOrder.Shuffle, moviesOnly!.Station.Pool.Order);
        Assert.False(moviesOnly.Exists);
        Assert.Equal("1 movie no channel plays yet, shuffled.", moviesOnly.Description);
    }
}
