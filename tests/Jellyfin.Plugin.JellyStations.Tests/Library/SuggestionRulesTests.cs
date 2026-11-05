using Jellyfin.Plugin.JellyStations.Api.Dto;
using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Services;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Library;

/// <summary>The rules added with the genre split, themes, comfort reruns, TV decades, near misses and clip libraries.</summary>
public class SuggestionRulesTests
{
    private static readonly Guid MoviesLib = Guid.Parse("33333333-bbbb-0000-0000-000000000003");
    private static readonly Guid ShowsLib = Guid.Parse("44444444-bbbb-0000-0000-000000000004");
    private static readonly Guid BumpsLib = Guid.Parse("55555555-bbbb-0000-0000-000000000005");

    private static LibraryAnalysisDto Library() => new()
    {
        Libraries =
        {
            new LibraryStatDto { Id = MoviesLib, Name = "Movies", Movies = 50, Hours = 100 },
            new LibraryStatDto { Id = ShowsLib, Name = "Shows", Episodes = 300, Hours = 150 }
        }
    };

    private static SeriesStatDto Show(string name, int episodes, double hours, int? year = null, string? rating = "TV-14", params string[] genres)
        => new() { Id = Guid.NewGuid(), Name = name, Episodes = episodes, Hours = hours, Year = year, Rating = rating, Genres = genres.ToList() };

    private static StationSuggestionDto One(List<StationSuggestionDto> list, string key) => Assert.Single(list, s => s.Key == key);

    [Fact]
    public void A_genre_with_enough_movies_and_enough_episodes_becomes_two_channels()
    {
        var a = Library();
        a.Genres.Add(new GenreStatDto { Name = "Sci-Fi", Movies = 6, MovieHours = 12, Episodes = 40, EpisodeHours = 30, Series = 3 });
        a.Genres.Add(new GenreStatDto { Name = "Drama", Movies = 2, MovieHours = 4, Episodes = 40, EpisodeHours = 30, Series = 2 });

        var list = StationSuggester.Suggest(a, Array.Empty<string>());

        Assert.DoesNotContain(list, s => s.Key == "genre:Sci-Fi");
        var movies = One(list, "genre-movies:Sci-Fi");
        Assert.Equal("Sci-Fi Movies", movies.Name);
        Assert.Equal(12, movies.Hours);
        Assert.Equal(PlayOrder.Shuffle, movies.Station.Pool.Order);
        Assert.True(movies.Station.PaddingByLength);
        var movieSource = Assert.Single(movies.Station.Pool.Sources);
        Assert.Equal((SourceKind.Genre, "Sci-Fi"), (movieSource.Kind, movieSource.Genre));
        Assert.Equal(new[] { SourceKind.Movie }, movieSource.ItemKinds);

        var tv = One(list, "genre-tv:Sci-Fi");
        Assert.Equal("Sci-Fi TV", tv.Name);
        Assert.Equal(30, tv.Hours);
        Assert.Equal(PlayOrder.ShuffleEpisodesInOrder, tv.Station.Pool.Order);
        Assert.Equal(new[] { SourceKind.Episode }, Assert.Single(tv.Station.Pool.Sources).ItemKinds);
        Assert.Contains("40 episodes from 3 series", tv.Description, StringComparison.Ordinal);

        // Four hours of movies is not a channel on its own: Drama stays one mixed station.
        Assert.Empty(One(list, "genre:Drama").Station.Pool.Sources.Single().ItemKinds);
        Assert.DoesNotContain(list, s => s.Key.EndsWith(":Drama", StringComparison.Ordinal) && s.Key != "genre:Drama");
    }

    [Fact]
    public void Anime_comes_from_the_tag_when_no_genre_says_it()
    {
        var a = Library();
        a.Tags.Add(new TagStatDto { Name = "anime", Movies = 4, Series = 6, Hours = 60 });

        var anime = One(StationSuggester.Suggest(a, Array.Empty<string>()), "theme:anime");

        Assert.Equal(("Anime", "Genre", 60d), (anime.Name, anime.Category, anime.Hours));
        var source = Assert.Single(anime.Station.Pool.Sources);
        Assert.Equal((SourceKind.Tag, "anime"), (source.Kind, source.Tag));
        Assert.Equal(PlayOrder.ShuffleEpisodesInOrder, anime.Station.Pool.Order);
        Assert.True(anime.Station.PaddingByLength);
        Assert.Contains("the tag \"anime\"", anime.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_theme_takes_its_genres_so_they_are_not_offered_twice()
    {
        var a = Library();
        a.Genres.Add(new GenreStatDto { Name = "Documentary", Movies = 5, MovieHours = 9 });
        a.Tags.Add(new TagStatDto { Name = "Docuseries", Series = 2, Hours = 12 });

        var list = StationSuggester.Suggest(a, Array.Empty<string>());

        var docs = One(list, "theme:documentary");
        Assert.Equal("Documentaries", docs.Name);
        Assert.Equal(12, docs.Hours); // the larger of the two, since they may overlap
        Assert.Equal(new[] { SourceKind.Genre, SourceKind.Tag }, docs.Station.Pool.Sources.Select(s => s.Kind));
        Assert.DoesNotContain(list, s => s.Key == "genre:Documentary");
    }

    [Fact]
    public void A_theme_that_is_one_genre_is_left_to_the_genre_rule()
    {
        var a = Library();
        a.Genres.Add(new GenreStatDto { Name = "Documentary", Movies = 5, MovieHours = 9 });

        var list = StationSuggester.Suggest(a, Array.Empty<string>());

        Assert.Single(list, s => s.Key == "genre:Documentary");
        Assert.DoesNotContain(list, s => s.Key.StartsWith("theme:", StringComparison.Ordinal));
    }

    [Fact]
    public void Comfort_reruns_gathers_the_half_hour_comedies()
    {
        var a = Library();
        var cheers = Show("Cheers", 30, 11, 1982, "TV-PG", "Comedy");
        var taxi = Show("Taxi", 12, 4.4, 1978, "TV-PG", "Comedy", "Drama");
        var office = Show("The Office", 10, 3.7, 2005, null, "comedy");
        a.Series.AddRange(new[]
        {
            cheers, taxi, office,
            Show("Bluey", 50, 6, 2018, "TV-Y", "Comedy", "Animation"), // a kids show
            Show("Barry", 16, 9, 2018, "TV-MA", "Drama"),              // not a comedy
            Show("Hour Long", 10, 9, 2010, "TV-14", "Comedy"),         // 54 minutes an episode
            Show("Pilot Only", 2, 0.8, 2011, "TV-14", "Comedy")        // too few episodes
        });

        var comfort = One(StationSuggester.Suggest(a, Array.Empty<string>()), "comfort-reruns");

        Assert.Equal(("Comfort Reruns", "Series"), (comfort.Name, comfort.Category));
        Assert.Equal(19.1, comfort.Hours);
        Assert.Equal(PlayOrder.ShuffleEpisodesInOrder, comfort.Station.Pool.Order);
        Assert.Equal(new[] { cheers.Id, taxi.Id, office.Id }, comfort.Station.Pool.Sources.Select(s => s.ItemId!.Value));
        Assert.All(comfort.Station.Pool.Sources, s => Assert.Equal(SourceKind.Series, s.Kind));
        Assert.Contains("3 half-hour comedies (Cheers, Taxi, The Office)", comfort.Description, StringComparison.Ordinal);

        // Two shows are not a channel, and neither is too little of them.
        a.Series.Remove(office);
        var two = StationSuggester.Build(a, Array.Empty<string>());
        Assert.DoesNotContain(two.Suggestions, s => s.Key == "comfort-reruns");
        var miss = Assert.Single(two.NearMisses, n => n.Name == "Comfort Reruns");
        Assert.Equal((2d, 3d, "half-hour comedies"), (miss.Have, miss.Need, miss.Unit));
    }

    [Fact]
    public void A_decade_of_television_needs_two_shows_that_began_in_it()
    {
        var a = Library();
        var cheers = Show("Cheers", 30, 11, 1982);
        var alf = Show("ALF", 20, 7.5, 1986);
        a.Series.AddRange(new[] { cheers, alf, Show("Lost", 100, 70, 2004), Show("No Year", 40, 30) });

        var list = StationSuggester.Suggest(a, Array.Empty<string>());

        var eighties = One(list, "decade-tv:1980");
        Assert.Equal(("1980s TV", "Decade", 18.5), (eighties.Name, eighties.Category, eighties.Hours));
        Assert.Equal(new[] { cheers.Id, alf.Id }, eighties.Station.Pool.Sources.Select(s => s.ItemId!.Value));
        Assert.Equal(PlayOrder.ShuffleEpisodesInOrder, eighties.Station.Pool.Order);
        Assert.Contains("2 shows that began between 1980 and 1989", eighties.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(list, s => s.Key == "decade-tv:2000"); // one show is that show's own channel
    }

    [Fact]
    public void Near_misses_are_the_rules_at_least_half_way_there_closest_first()
    {
        var a = Library();
        a.KidsHours = 3;
        a.Genres.Add(new GenreStatDto { Name = "Horror", Movies = 4, MovieHours = 6.5 });
        a.Genres.Add(new GenreStatDto { Name = "Western", Movies = 1, MovieHours = 2 });
        a.Genres.Add(new GenreStatDto { Name = "Comedy", Movies = 9, MovieHours = 18 });
        a.Series.Add(Show("Almost", 15, 6));
        a.Series.Add(Show("Barely Started", 4, 2));
        a.Franchises.Add(new FranchiseStatDto { Name = "Dune", ItemIds = { Guid.NewGuid(), Guid.NewGuid() }, Titles = { "Dune", "Dune: Part Two" }, Hours = 5.2 });
        a.Decades.Add(new DecadeStatDto { Decade = 1990, Movies = 3, Hours = 5 });
        a.Tags.Add(new TagStatDto { Name = "halloween", Movies = 1, Hours = 0.6 });

        var set = StationSuggester.Build(a, new[] { "1990s Movies" });

        Assert.Equal(
            new[] { "Horror: 6.5 of 8 h", "Almost: 15 of 20 episodes", "Kids TV: 3 of 4 h", "Dune Marathon: 2 of 3 titles", "Halloween: 0.6 of 1 h" },
            set.NearMisses.Select(n => n.Name + ": " + n.Have + " of " + n.Need + " " + n.Unit));
        Assert.Equal("Genre", set.NearMisses[0].Category);
        // Western (a quarter of the way) and the four-episode show are too far off; a station by that name exists for the 1990s.
        Assert.DoesNotContain(set.Suggestions, s => s.Key is "genre:Horror" or "kids" or "franchise:Dune");
        Assert.Single(set.Suggestions, s => s.Key == "genre:Comedy");
    }

    [Fact]
    public void Near_misses_stop_at_the_cap()
    {
        var a = Library();
        for (var i = 0; i < StationSuggester.MaxNearMisses + 5; i++)
        {
            a.Series.Add(Show("Show " + i.ToString("00"), 10 + (i % 9), 4));
        }

        var near = StationSuggester.Build(a, Array.Empty<string>()).NearMisses;

        Assert.Equal(StationSuggester.MaxNearMisses, near.Count);
        Assert.Equal(18, near[0].Have);
    }

    [Fact]
    public void A_clip_library_is_never_a_source()
    {
        var a = Library();
        a.KidsHours = 10;
        a.Ratings.Add(new RatingStatDto { Rating = "PG", Movies = 5, MovieHours = 10 });
        a.Decades.Add(new DecadeStatDto { Decade = 1990, Movies = 6, Hours = 12 });
        a.Libraries.Add(new LibraryStatDto { Id = BumpsLib, Name = "Bumps", Movies = 40, Clips = 40, ClipLibrary = true, Hours = 0.3 });
        a.Libraries.Add(new LibraryStatDto { Id = Guid.NewGuid(), Name = "Home videos", Videos = 12, Hours = 6 });

        var list = StationSuggester.Suggest(a, Array.Empty<string>());

        Assert.Equal(new[] { MoviesLib, ShowsLib }, One(list, "kids").Station.Pool.Sources.Select(s => s.ItemId!.Value));
        Assert.Equal(MoviesLib, Assert.Single(One(list, "family-movies").Station.Pool.Sources).ItemId);
        Assert.Equal(MoviesLib, Assert.Single(One(list, "decade:1990").Station.Pool.Sources).ItemId);
    }

    [Fact]
    public void Thresholds_come_from_settings_and_a_caller_can_still_lower_them()
    {
        var saved = new SuggestionThresholds { GenreMinHours = 3, SeriesMinEpisodes = 12, ClassicsBeforeYear = 1960 };

        var t = StationSuggestionService.Thresholds(saved, null, null);
        Assert.Equal((3d, 12, 1960, 4d), (t.GenreMinHours, t.SeriesMinEpisodes, t.ClassicsBeforeYear, t.KidsMinHours));
        Assert.NotSame(saved, t);

        var lowered = StationSuggestionService.Thresholds(saved, 1, 5);
        Assert.Equal((1d, 1d, 1d, 1d, 5), (lowered.GenreMinHours, lowered.KidsMinHours, lowered.ComfortMinHours, lowered.LeftoversMinHours, lowered.SeriesMinEpisodes));
        Assert.Equal(3, saved.GenreMinHours);

        Assert.Equal(8, StationSuggestionService.Thresholds(null, null, null).GenreMinHours);
    }

    [Fact]
    public void Thresholds_are_brought_into_range()
    {
        var t = new SuggestionThresholds { GenreMinHours = -4, KidsMinHours = double.NaN, SeriesMinEpisodes = 0, FranchiseMinTitles = 1, ComfortMinSeries = 0, ClassicsBeforeYear = 3000, DecadeMinHours = 2.26 };
        t.Normalize();

        Assert.Equal((0.1, 4d, 1, 2, 2, 2100, 2.3), (t.GenreMinHours, t.KidsMinHours, t.SeriesMinEpisodes, t.FranchiseMinTitles, t.ComfortMinSeries, t.ClassicsBeforeYear, t.DecadeMinHours));
    }

    [Fact]
    public void Bumper_clips_switch_the_breaks_on_and_make_room_when_there_is_none()
    {
        var clips = new ContentSource { Kind = SourceKind.Library, ItemId = BumpsLib, DisplayName = "Bumps" };

        var gapless = new Station();
        StationSuggestionService.UseBumpers(gapless, clips);
        Assert.True(gapless.Bumpers.Enabled);
        Assert.Equal(BumpsLib, Assert.Single(gapless.Bumpers.Sources).ItemId);
        Assert.Equal(2, gapless.Bumpers.BreakMinutes);

        // Padding already leaves gaps, and a template's break length and rolls are its own.
        var padded = new Station { PaddingByLength = true };
        padded.Bumpers.Sources.Add(new ContentSource { Kind = SourceKind.Movie, ItemId = Guid.NewGuid() });
        padded.Bumpers.PreRoll.Enabled = true;
        StationSuggestionService.UseBumpers(padded, clips);
        Assert.Equal(0, padded.Bumpers.BreakMinutes);
        Assert.Equal(SourceKind.Library, Assert.Single(padded.Bumpers.Sources).Kind);
        Assert.True(padded.Bumpers.PreRoll.Enabled);

        var withBreak = new Station();
        withBreak.Bumpers.BreakMinutes = 5;
        StationSuggestionService.UseBumpers(withBreak, clips);
        Assert.Equal(5, withBreak.Bumpers.BreakMinutes);
    }
}
