using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.JellyStations.Api.Dto;
using Jellyfin.Plugin.JellyStations.Library;
using Jellyfin.Plugin.JellyStations.Models;
using Xunit;

namespace Jellyfin.Plugin.JellyStations.Tests.Library;

public class WeekLayoutsTests
{
    private static readonly Guid MoviesLib = Guid.Parse("33333333-cccc-0000-0000-000000000003");
    private static readonly Guid ShowsLib = Guid.Parse("44444444-cccc-0000-0000-000000000004");
    private static readonly Guid BumpsLib = Guid.Parse("55555555-cccc-0000-0000-000000000005");

    /// <summary>A library with enough of everything: kids shows, family movies, grown-up films, half-hour comedies.</summary>
    private static LibraryAnalysisDto FullLibrary()
    {
        var a = new LibraryAnalysisDto
        {
            KidsHours = 20,
            FamilyHours = 60,
            Totals = new AnalysisTotalsDto { Movies = 50, MovieHours = 100, Episodes = 300, EpisodeHours = 150, Series = 10 },
            Libraries =
            {
                new LibraryStatDto { Id = MoviesLib, Name = "Movies", Movies = 50, Hours = 100 },
                new LibraryStatDto { Id = ShowsLib, Name = "Shows", Episodes = 300, Hours = 150 },
                new LibraryStatDto { Id = BumpsLib, Name = "Bumps", Videos = 40, Clips = 40, ClipLibrary = true, Hours = 1 }
            },
            Ratings =
            {
                new RatingStatDto { Rating = "G", Movies = 5, MovieHours = 8 },
                new RatingStatDto { Rating = "PG", Movies = 10, MovieHours = 18 },
                new RatingStatDto { Rating = "PG-13", Movies = 15, MovieHours = 30 },
                new RatingStatDto { Rating = "R", Movies = 20, MovieHours = 44 },
                new RatingStatDto { Rating = "TV-MA", Series = 3, EpisodeHours = 40 }
            }
        };
        a.Series.Add(Show("Cheers", 60, 25, "TV-PG", "Comedy"));
        a.Series.Add(Show("Taxi", 40, 16, "TV-PG", "Comedy"));
        a.Series.Add(Show("Frasier", 50, 20, "TV-PG", "Comedy"));
        a.Series.Add(Show("Barry", 16, 9, "TV-MA", "Drama"));
        return a;
    }

    private static SeriesStatDto Show(string name, int episodes, double hours, string? rating, params string[] genres)
        => new() { Id = Guid.NewGuid(), Name = name, Episodes = episodes, Hours = hours, Rating = rating, Genres = genres.ToList() };

    private static WeekLayout Classic => WeekLayouts.BuiltIn.Single(l => l.Name == "Classic broadcast week");

    [Fact]
    public void BuiltIns_HaveFixedDistinctIds_AndBlocksTheEditorAccepts()
    {
        var layouts = WeekLayouts.BuiltIn;

        Assert.Equal(new[] { "Classic broadcast week", "Movies all day", "Kids by day, grown-ups by night" }, layouts.Select(l => l.Name));
        Assert.Equal(layouts.Count, layouts.Select(l => l.Id).Distinct().Count());
        Assert.All(layouts, l =>
        {
            Assert.StartsWith("7c1b3a30-", l.Id.ToString("D"), StringComparison.Ordinal);
            Assert.True(l.BuiltIn);
            Assert.False(string.IsNullOrWhiteSpace(l.StationName));
            Assert.True(l.Blocks.Count >= WeekLayouts.MinBlocks);
            Assert.NotEqual(LayoutFill.None, l.Filler);
            Assert.All(l.Blocks, b =>
            {
                Assert.NotEqual(LayoutFill.None, b.Fill);
                Assert.NotEmpty(b.Block.Days);
                Assert.InRange(b.Block.DurationMinutes, 5, 1440);
                Assert.True(TimeSpan.TryParseExact(b.Block.StartLocal, @"hh\:mm", CultureInfo.InvariantCulture, out _));
                Assert.Empty(b.Block.Sources);
                Assert.Equal(BlockRecurrence.Weekly, b.Block.Recurrence);
            });
        });
    }

    [Fact]
    public void BuiltIns_DoNotOverlapWithinADay()
    {
        // Two blocks on the same day must not run into each other, or the earlier one in the list would hide the other.
        foreach (var layout in WeekLayouts.BuiltIn)
        {
            foreach (var day in Enum.GetValues<DayOfWeek>())
            {
                var spans = layout.Blocks
                    .Where(b => b.Block.Days.Contains(day))
                    .Select(b => (Start: TimeSpan.Parse(b.Block.StartLocal, CultureInfo.InvariantCulture).TotalMinutes, b.Block.Name, b.Block.DurationMinutes))
                    .OrderBy(s => s.Start)
                    .ToList();
                for (var i = 1; i < spans.Count; i++)
                {
                    Assert.True(spans[i - 1].Start + spans[i - 1].DurationMinutes <= spans[i].Start, layout.Name + ": " + spans[i - 1].Name + " runs into " + spans[i].Name + " on " + day);
                }
            }
        }
    }

    [Fact]
    public void TheClassicWeek_IsFilledFromTheLibrary()
    {
        var filled = WeekLayouts.Suggest(Classic, FullLibrary());

        Assert.NotNull(filled);
        var station = filled!.Station;
        Assert.Equal(ScheduleMode.Pool, station.Mode);
        Assert.True(station.PaddingByLength);
        Assert.Empty(filled.Dropped);
        Assert.Equal(250, filled.Hours); // everything, clip library left out

        // The pool is everything, with the layout's family hours.
        Assert.Equal(new[] { MoviesLib, ShowsLib }, station.Pool.Sources.Select(s => s.ItemId!.Value));
        Assert.All(station.Pool.Sources, s => Assert.Null(s.MaxRating));
        var rule = Assert.Single(station.Pool.DaypartRules);
        Assert.Equal(("06:00", "20:00", "PG-13"), (rule.StartLocal, rule.EndLocal, rule.MaxRating));

        var blocks = station.Slots.Blocks;
        Assert.Equal(6, blocks.Count);
        Assert.Equal(blocks.Count, blocks.Select(b => b.Id).Distinct().Count());
        Assert.DoesNotContain(blocks, b => b.Id == Guid.Empty);

        var cartoons = blocks.Single(b => b.Name == "Morning cartoons");
        Assert.All(cartoons.Sources, s => Assert.Equal((SourceKind.Library, "G"), (s.Kind, s.MaxRating)));
        Assert.Equal(2, cartoons.Sources.Count);

        var reruns = blocks.Single(b => b.Name == "Daytime reruns");
        Assert.Equal(new[] { "Cheers", "Taxi", "Frasier" }, reruns.Sources.Select(s => s.DisplayName)); // the comfort comedies, not Barry
        Assert.All(reruns.Sources, s => Assert.Equal(SourceKind.Series, s.Kind));

        var matinee = blocks.Single(b => b.Name == "Sunday matinee");
        var matineeSource = Assert.Single(matinee.Sources);
        Assert.Equal((MoviesLib, "PG", SourceKind.Movie), (matineeSource.ItemId, matineeSource.MaxRating, Assert.Single(matineeSource.ItemKinds)));

        var movie = blocks.Single(b => b.Name == "Prime-time movie");
        Assert.Equal(PlayOrder.Shuffle, movie.Order);
        Assert.Null(Assert.Single(movie.Sources).MaxRating);

        var late = blocks.Single(b => b.Name == "Late night");
        Assert.Equal(2, late.Sources.Count);
        Assert.All(late.Sources, s => Assert.Equal(new[] { "PG-13", "TV-14", "R", "TV-MA", "NC-17" }, s.Ratings));

        Assert.Contains("Morning cartoons (weekdays 06:30)", filled.Description, StringComparison.Ordinal);
        Assert.Contains("Prime-time movie (every day 20:00)", filled.Description, StringComparison.Ordinal);
        Assert.EndsWith("everything else in between.", filled.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlockWhoseFillTheLibraryLacks_IsLeftOutAndNamed()
    {
        var a = FullLibrary();
        a.KidsHours = 1; // below the 4 h a kids channel needs

        var filled = WeekLayouts.Suggest(Classic, a);

        Assert.NotNull(filled);
        Assert.Equal(new[] { "Morning cartoons", "Saturday morning cartoons" }, filled!.Dropped);
        Assert.Equal(4, filled.Station.Slots.Blocks.Count);
        Assert.Contains("Left out for want of content: Morning cartoons, Saturday morning cartoons.", filled.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Reruns_FallBackToEveryShow_WhenTheComediesAreTooFew()
    {
        var a = FullLibrary();
        a.Series.RemoveAll(s => s.Name != "Cheers");

        var filled = WeekLayouts.Suggest(Classic, a);

        var reruns = filled!.Station.Slots.Blocks.Single(b => b.Name == "Daytime reruns");
        var source = Assert.Single(reruns.Sources);
        Assert.Equal((SourceKind.Library, ShowsLib, SourceKind.Episode), (source.Kind, source.ItemId, Assert.Single(source.ItemKinds)));
    }

    [Fact]
    public void TooFewFilledBlocks_OrNothingToFillWith_MeansNoStation()
    {
        var a = FullLibrary();
        a.KidsHours = 0;
        a.FamilyHours = 0;
        a.Ratings.Clear(); // no family movies, no grown-up content
        a.Series.Clear();
        a.Totals.EpisodeHours = 2;
        a.Totals.MovieHours = 2;
        // Only the movie block (needs 8 h of movies) and the reruns block (needs 8 h of episodes) could remain, and neither has it.
        Assert.Null(WeekLayouts.Suggest(Classic, a));

        var empty = new LibraryAnalysisDto();
        Assert.Null(WeekLayouts.Suggest(Classic, empty));
    }

    [Fact]
    public void MoviesAllDay_IsMoviesBetweenTheBlocks()
    {
        var movies = WeekLayouts.BuiltIn.Single(l => l.Name == "Movies all day");

        var filled = WeekLayouts.Suggest(movies, FullLibrary());

        Assert.NotNull(filled);
        Assert.Equal(PlayOrder.Shuffle, filled!.Station.Pool.Order);
        var pool = Assert.Single(filled.Station.Pool.Sources);
        Assert.Equal((MoviesLib, SourceKind.Movie), (pool.ItemId, Assert.Single(pool.ItemKinds)));
        Assert.Equal(100, filled.Hours);
        Assert.Empty(filled.Station.Pool.DaypartRules);
        Assert.EndsWith("shuffled movies in between.", filled.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void FromStation_KeepsOrDropsTheSources_AndCarriesTheFamilyHours()
    {
        var station = new Station
        {
            Mode = ScheduleMode.Pool,
            Pool = new PoolConfig { DaypartRules = { new DaypartRule { StartLocal = "07:00", EndLocal = "21:00", MaxRating = "PG" } } },
            Slots = new SlotsConfig
            {
                Blocks =
                {
                    new SlotBlock { Id = Guid.NewGuid(), Name = "Toons", Days = { DayOfWeek.Saturday }, StartLocal = "07:00", DurationMinutes = 120, Sources = { new ContentSource { Kind = SourceKind.Series, ItemId = Guid.NewGuid(), DisplayName = "Bluey" } } },
                    new SlotBlock { Id = Guid.NewGuid(), Name = "Weather", Kind = BlockKind.Weather, Days = { DayOfWeek.Monday }, StartLocal = "06:00", DurationMinutes = 5 }
                }
            }
        };

        var kept = WeekLayouts.FromStation(station, " My week ", keepSources: true);
        Assert.Equal("My week", kept.Name);
        Assert.NotEqual(Guid.Empty, kept.Id);
        Assert.Equal(LayoutFill.Everything, kept.Filler);
        Assert.All(kept.Blocks, b => Assert.Equal((Guid.Empty, LayoutFill.None), (b.Block.Id, b.Fill)));
        Assert.Equal("Bluey", Assert.Single(kept.Blocks[0].Block.Sources).DisplayName);
        Assert.Equal("PG", Assert.Single(kept.DaypartRules).MaxRating);

        var shape = WeekLayouts.FromStation(station, "Shape", keepSources: false);
        Assert.All(shape.Blocks, b => Assert.Empty(b.Block.Sources));

        // The source station is untouched.
        Assert.Single(station.Slots.Blocks[0].Sources);
        Assert.NotEqual(Guid.Empty, station.Slots.Blocks[0].Id);

        // Slots mode has no family hours to carry.
        station.Mode = ScheduleMode.Slots;
        Assert.Empty(WeekLayouts.FromStation(station, "Slots", true).DaypartRules);
    }

    [Fact]
    public void ASavedLayoutWithSources_IsSuggestedWithThem_AndBlocksWithoutAreLeftOut()
    {
        var show = Guid.NewGuid();
        var layout = new WeekLayout
        {
            Id = Guid.NewGuid(),
            Name = "Mine",
            Filler = LayoutFill.Everything,
            Blocks =
            {
                new LayoutBlock { Block = new SlotBlock { Name = "Toons", Days = { DayOfWeek.Saturday }, StartLocal = "07:00", DurationMinutes = 120, Sources = { new ContentSource { Kind = SourceKind.Series, ItemId = show } } } },
                new LayoutBlock { Block = new SlotBlock { Name = "Empty", Days = { DayOfWeek.Sunday }, StartLocal = "07:00", DurationMinutes = 120 } },
                new LayoutBlock { Block = new SlotBlock { Name = "Weather", Kind = BlockKind.Weather, Days = { DayOfWeek.Monday }, StartLocal = "06:00", DurationMinutes = 5 } }
            }
        };

        var filled = WeekLayouts.Suggest(layout, FullLibrary());

        Assert.NotNull(filled);
        Assert.Equal(new[] { "Toons", "Weather" }, filled!.Station.Slots.Blocks.Select(b => b.Name));
        Assert.Equal(show, Assert.Single(filled.Station.Slots.Blocks[0].Sources).ItemId);
        Assert.Equal(new[] { "Empty" }, filled.Dropped);
        Assert.True(filled.Station.PaddingByLength); // the pool of everything has movies
    }

    [Fact]
    public void Blocks_AreFreshCopies()
    {
        var blocks = WeekLayouts.Blocks(Classic);

        Assert.Equal(6, blocks.Count);
        Assert.All(blocks, b => Assert.NotEqual(Guid.Empty, b.Id));
        Assert.Equal(blocks.Count, blocks.Select(b => b.Id).Distinct().Count());
        blocks[0].Days.Clear();
        Assert.NotEmpty(Classic.Blocks[0].Block.Days);
    }

    [Fact]
    public void Clean_GivesAnIdAndTrims_AndNeverKeepsABuiltInId()
    {
        var layout = WeekLayouts.Clean(new WeekLayout { Id = Classic.Id, Name = "  Copy  ", Description = " ", StationName = " Chan ", Blocks = null!, DaypartRules = null! });

        Assert.NotEqual(Classic.Id, layout.Id);
        Assert.NotEqual(Guid.Empty, layout.Id);
        Assert.Equal("Copy", layout.Name);
        Assert.Null(layout.Description);
        Assert.Equal("Chan", layout.StationName);
        Assert.Empty(layout.Blocks);
        Assert.Empty(layout.DaypartRules);
        Assert.False(layout.BuiltIn);
        Assert.NotEqual(default, layout.CreatedUtc);
    }

    [Fact]
    public void Merge_PutsSavedFirst_AndASavedNameShadowsABuiltIn()
    {
        var saved = new List<WeekLayout> { new() { Id = Guid.NewGuid(), Name = "Zed", BuiltIn = true }, new() { Id = Guid.NewGuid(), Name = " movies ALL day " } };

        var merged = WeekLayouts.Merge(saved);

        Assert.Equal(new[] { " movies ALL day ", "Zed", "Classic broadcast week", "Kids by day, grown-ups by night" }, merged.Select(l => l.Name));
        Assert.False(merged[1].BuiltIn);
        Assert.NotNull(WeekLayouts.Find(saved, Classic.Id));
        Assert.Null(WeekLayouts.Find(saved, WeekLayouts.BuiltIn.Single(l => l.Name == "Movies all day").Id));
    }

    [Fact]
    public void TheSuggester_OffersTheLayoutsFirst_UnderBroadcastWeek()
    {
        var a = FullLibrary();
        a.Genres.Add(new GenreStatDto { Name = "Comedy", Movies = 10, MovieHours = 20 });

        var list = StationSuggester.Suggest(a, Array.Empty<string>());

        Assert.Equal("Broadcast week", list[0].Category);
        var classic = Assert.Single(list, s => s.Key == "layout:" + Classic.Id.ToString("N"));
        Assert.Equal("Network One", classic.Name);
        Assert.Equal("Network One", classic.Station.Name);
        Assert.Equal(6, classic.Station.Slots.Blocks.Count);
        Assert.Equal(3, list.Count(s => s.Category == "Broadcast week"));
        Assert.Contains(list, s => s.Key == "genre:Comedy");

        // A saved layout passed in takes the built-ins' place, and is named after itself when it names no station.
        var mine = new WeekLayout { Id = Guid.NewGuid(), Name = "Mine", Filler = LayoutFill.Movies, Blocks = { Classic.Blocks[4], Classic.Blocks[5] } };
        var own = StationSuggester.Suggest(a, Array.Empty<string>(), null, new[] { mine });
        var suggestion = Assert.Single(own, s => s.Category == "Broadcast week");
        Assert.Equal(("layout:" + mine.Id.ToString("N"), "Mine"), (suggestion.Key, suggestion.Name));
    }

    [Fact]
    public void ExportDocument_CarriesWeekLayouts_AndOlderDocumentsStillParse()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        var doc = new StationExportDocument { WeekLayouts = { Classic } };
        var json = JsonSerializer.SerializeToElement(doc, options);

        var parsed = StationExportDocument.Parse(json, options);

        Assert.NotNull(parsed);
        Assert.Equal(3, parsed!.Version);
        var layout = Assert.Single(parsed.WeekLayouts);
        Assert.Equal(Classic.Id, layout.Id);
        Assert.Equal(6, layout.Blocks.Count);
        Assert.Equal(LayoutFill.Kids, layout.Blocks[0].Fill);

        var old = StationExportDocument.Parse(JsonSerializer.SerializeToElement(new { Version = 2, Stations = Array.Empty<Station>(), BlockTemplates = Array.Empty<SlotBlock>() }, options), options);
        Assert.NotNull(old);
        Assert.Empty(old!.WeekLayouts);

        // A document holding only layouts is an export document, not a single station.
        var onlyLayouts = StationExportDocument.Parse(JsonSerializer.SerializeToElement(new { WeekLayouts = new[] { Classic } }, options), options);
        Assert.NotNull(onlyLayouts);
        Assert.Single(onlyLayouts!.WeekLayouts);
        Assert.Empty(onlyLayouts.Stations);
    }
}
