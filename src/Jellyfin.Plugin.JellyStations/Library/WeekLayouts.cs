using System.Globalization;
using Jellyfin.Plugin.JellyStations.Api.Dto;
using Jellyfin.Plugin.JellyStations.Models;
using Jellyfin.Plugin.JellyStations.Storage;

namespace Jellyfin.Plugin.JellyStations.Library;

/// <summary>A week layout filled from the library: the station it makes and what had to be left out.</summary>
public sealed class LayoutSuggestion
{
    public Station Station { get; set; } = new();

    /// <summary>Hours the station can play between its blocks (the filler's content).</summary>
    public double Hours { get; set; }

    public string Description { get; set; } = string.Empty;

    /// <summary>Blocks left out because the library has nothing for them, by name.</summary>
    public List<string> Dropped { get; } = new();
}

/// <summary>
/// Week layouts: the built-in ones, how a station's blocks become a saved one, how a layout's blocks are added to
/// a station, and how the suggester fills one from the library analysis. Pure; the controller, the import path and
/// <see cref="StationSuggester"/> call it.
/// </summary>
public static class WeekLayouts
{
    /// <summary>A layout is offered as a station when at least this many of its blocks could be filled.</summary>
    public const int MinBlocks = 2;

    private static readonly DayOfWeek[] EveryDay =
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    };

    private static readonly DayOfWeek[] Weekdays = { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };

    /// <summary>Ratings from PG-13 / TV-14 up, for the grown-up fill (an allow-list, since sources have no minimum rating).</summary>
    private static readonly string[] GrownupRatings = { "PG-13", "TV-14", "R", "TV-MA", "NC-17" };

    /// <summary>The layouts that ship with the plugin. Ids are fixed so links and exports hold.</summary>
    public static IReadOnlyList<WeekLayout> BuiltIn { get; } = new List<WeekLayout>
    {
        Layout(
            "7c1b3a30-0001-4b5e-9a6d-6e1f0c2d3e01",
            "Classic broadcast week",
            "Cartoons on weekday mornings and all Saturday morning, reruns through the day, a Sunday matinee, a movie every night at eight and grown-up shows late; everything else in between, nothing above PG-13 before eight in the evening.",
            "Network One",
            LayoutFill.Everything,
            new[] { new DaypartRule { StartLocal = "06:00", EndLocal = "20:00", MaxRating = "PG-13", AllowUnrated = true } },
            Block("Morning cartoons", Weekdays, "06:30", 120, PlayOrder.ShuffleEpisodesInOrder, LayoutFill.Kids),
            Block("Saturday morning cartoons", new[] { DayOfWeek.Saturday }, "07:00", 240, PlayOrder.ShuffleEpisodesInOrder, LayoutFill.Kids),
            Block("Daytime reruns", Weekdays, "09:00", 420, PlayOrder.ShuffleEpisodesInOrder, LayoutFill.Reruns),
            Block("Sunday matinee", new[] { DayOfWeek.Sunday }, "13:00", 180, PlayOrder.Shuffle, LayoutFill.FamilyMovies),
            Block("Prime-time movie", EveryDay, "20:00", 180, PlayOrder.Shuffle, LayoutFill.Movies),
            Block("Late night", EveryDay, "23:00", 180, PlayOrder.ShuffleEpisodesInOrder, LayoutFill.Grownup)),
        Layout(
            "7c1b3a30-0002-4b5e-9a6d-6e1f0c2d3e02",
            "Movies all day",
            "Movies around the clock: a family matinee every afternoon, the main feature at eight and a late movie after it, shuffled films in between.",
            "Movies All Day",
            LayoutFill.Movies,
            Array.Empty<DaypartRule>(),
            Block("Afternoon matinee", EveryDay, "13:00", 180, PlayOrder.Shuffle, LayoutFill.FamilyMovies),
            Block("Prime-time movie", EveryDay, "20:00", 180, PlayOrder.Shuffle, LayoutFill.Movies),
            Block("Late movie", EveryDay, "23:00", 150, PlayOrder.Shuffle, LayoutFill.Movies)),
        Layout(
            "7c1b3a30-0003-4b5e-9a6d-6e1f0c2d3e03",
            "Kids by day, grown-ups by night",
            "Cartoons after school and all Saturday morning, a family movie on Friday and Saturday nights, grown-up shows from ten; everything rated PG or below in between.",
            "Family One",
            LayoutFill.Family,
            Array.Empty<DaypartRule>(),
            Block("After school", Weekdays, "15:00", 180, PlayOrder.ShuffleEpisodesInOrder, LayoutFill.Kids),
            Block("Saturday morning cartoons", new[] { DayOfWeek.Saturday }, "07:00", 240, PlayOrder.ShuffleEpisodesInOrder, LayoutFill.Kids),
            Block("Family movie night", new[] { DayOfWeek.Friday, DayOfWeek.Saturday }, "19:00", 180, PlayOrder.Shuffle, LayoutFill.FamilyMovies),
            Block("Late night", EveryDay, "22:00", 240, PlayOrder.ShuffleEpisodesInOrder, LayoutFill.Grownup))
    };

    /// <summary>Saved layouts first (by name), then the built-in ones whose names no saved layout has taken.</summary>
    public static List<WeekLayout> Merge(IEnumerable<WeekLayout>? saved)
    {
        var list = (saved ?? Enumerable.Empty<WeekLayout>())
            .Where(l => l is not null)
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var l in list)
        {
            l.BuiltIn = false;
        }

        var taken = new HashSet<string>(list.Select(l => l.Name.Trim()), StringComparer.OrdinalIgnoreCase);
        list.AddRange(BuiltIn.Where(b => !taken.Contains(b.Name)));
        return list;
    }

    /// <summary>The layout with this id among the saved and built-in ones, or null.</summary>
    public static WeekLayout? Find(IEnumerable<WeekLayout>? saved, Guid id) => Merge(saved).FirstOrDefault(l => l.Id == id);

    /// <summary>
    /// A layout from a station's blocks: each block copied without its id, with or without its sources, and the
    /// pool's family hours. The blocks' fill is <see cref="LayoutFill.None"/>, so the suggester plays the sources
    /// kept and leaves out blocks saved without any; between the blocks it plays everything.
    /// </summary>
    public static WeekLayout FromStation(Station station, string name, bool keepSources, IReadOnlyList<LayoutFill?>? fills = null, LayoutFill? filler = null)
    {
        var layout = new WeekLayout
        {
            Id = Guid.NewGuid(),
            Name = (name ?? string.Empty).Trim(),
            Filler = filler ?? LayoutFill.Everything,
            CreatedUtc = DateTime.UtcNow
        };
        var blocks = station.Slots?.Blocks ?? new List<SlotBlock>();
        for (var i = 0; i < blocks.Count; i++)
        {
            var copy = JsonFileStore.Clone(blocks[i]);
            copy.Id = Guid.Empty;
            if (!keepSources)
            {
                copy.Sources = new List<ContentSource>();
            }

            var fill = copy.Kind == BlockKind.Weather ? LayoutFill.None : (fills is not null && i < fills.Count ? fills[i] ?? LayoutFill.None : LayoutFill.None);
            layout.Blocks.Add(new LayoutBlock { Block = copy, Fill = fill });
        }

        if (station.Mode == ScheduleMode.Pool && station.Pool?.DaypartRules is { Count: > 0 } rules)
        {
            layout.DaypartRules = JsonFileStore.Clone(rules);
        }

        return layout;
    }

    /// <summary>
    /// Cleans a layout for storage: trimmed name, lists in place of nulls, block ids cleared (they are given on
    /// apply), an id of its own when it has none or has a built-in one.
    /// </summary>
    public static WeekLayout Clean(WeekLayout layout)
    {
        layout.Name = (layout.Name ?? string.Empty).Trim();
        layout.Description = string.IsNullOrWhiteSpace(layout.Description) ? null : layout.Description.Trim();
        layout.StationName = string.IsNullOrWhiteSpace(layout.StationName) ? null : layout.StationName.Trim();
        layout.Blocks = (layout.Blocks ?? new List<LayoutBlock>()).Where(b => b?.Block is not null).ToList();
        foreach (var b in layout.Blocks)
        {
            b.Block.Id = Guid.Empty;
            b.Block.Days ??= new List<DayOfWeek>();
            b.Block.Sources ??= new List<ContentSource>();
        }

        layout.DaypartRules ??= new List<DaypartRule>();
        layout.BuiltIn = false;
        if (layout.Id == Guid.Empty || BuiltIn.Any(b => b.Id == layout.Id))
        {
            layout.Id = Guid.NewGuid();
        }

        if (layout.CreatedUtc == default)
        {
            layout.CreatedUtc = DateTime.UtcNow;
        }

        return layout;
    }

    /// <summary>The layout's blocks as new blocks for a station: deep copies, each with an id of its own.</summary>
    public static List<SlotBlock> Blocks(WeekLayout layout)
    {
        var blocks = new List<SlotBlock>();
        foreach (var b in layout.Blocks ?? new List<LayoutBlock>())
        {
            var copy = JsonFileStore.Clone(b.Block);
            copy.Id = Guid.NewGuid();
            copy.Days ??= new List<DayOfWeek>();
            copy.Sources ??= new List<ContentSource>();
            blocks.Add(copy);
        }

        return blocks;
    }

    /// <summary>
    /// The station a layout makes from this library: a pool of the filler's content with the layout's blocks on
    /// top, each filled from the library by its fill. A block whose fill has less than its rule needs (the same
    /// minimums the plain suggestions use) is left out and named in <see cref="LayoutSuggestion.Dropped"/>; a
    /// weather block needs nothing. Null when fewer than <see cref="MinBlocks"/> blocks could be filled or the
    /// filler has nothing to play.
    /// </summary>
    public static LayoutSuggestion? Suggest(WeekLayout layout, LibraryAnalysisDto a, SuggestionThresholds? thresholds = null)
    {
        var t = thresholds ?? new SuggestionThresholds();
        var filler = Content(layout.Filler, a, t);
        if (filler is null)
        {
            return null;
        }

        var station = new Station { Mode = ScheduleMode.Pool };
        station.Pool.Order = filler.Order;
        station.Pool.Sources.AddRange(filler.Sources);
        station.Pool.DaypartRules = JsonFileStore.Clone(layout.DaypartRules ?? new List<DaypartRule>());
        var result = new LayoutSuggestion { Station = station, Hours = filler.Hours };
        var movies = filler.Movies;
        var kept = new List<SlotBlock>();
        foreach (var layoutBlock in layout.Blocks ?? new List<LayoutBlock>())
        {
            var block = JsonFileStore.Clone(layoutBlock.Block);
            block.Id = Guid.NewGuid();
            block.Days ??= new List<DayOfWeek>();
            block.Sources ??= new List<ContentSource>();
            var name = string.IsNullOrWhiteSpace(block.Name) ? "Block" : block.Name;
            if (block.Kind == BlockKind.Weather)
            {
                kept.Add(block);
                continue;
            }

            if (layoutBlock.Fill == LayoutFill.None)
            {
                if (block.Sources.Count == 0)
                {
                    result.Dropped.Add(name);
                    continue;
                }

                movies |= block.Sources.Any(s => s.IncludesMovies && s.Kind != SourceKind.Series && s.Kind != SourceKind.Episode);
                kept.Add(block);
                continue;
            }

            var content = Content(layoutBlock.Fill, a, t);
            if (content is null)
            {
                result.Dropped.Add(name);
                continue;
            }

            block.Sources = content.Sources;
            movies |= content.Movies;
            kept.Add(block);
        }

        if (kept.Count < MinBlocks)
        {
            return null;
        }

        station.Slots.Blocks = kept;
        station.PaddingByLength = movies;
        result.Description = Describe(layout, kept, result.Dropped);
        return result;
    }

    /// <summary>What a fill takes from this library, or null when the library has too little of it.</summary>
    private static FillContent? Content(LayoutFill fill, LibraryAnalysisDto a, SuggestionThresholds t)
    {
        var libraries = a.Libraries.Where(l => !l.ClipLibrary && l.Movies + l.Episodes > 0).ToList();
        var movieLibraries = libraries.Where(l => l.Movies > 0).ToList();
        var tvLibraries = libraries.Where(l => l.Episodes > 0).ToList();
        if (libraries.Count == 0)
        {
            return null;
        }

        switch (fill)
        {
            case LayoutFill.Everything:
            {
                var hours = libraries.Sum(l => l.Hours);
                return hours > 0 ? new FillContent(libraries.Select(l => LibrarySource(l, null, null)), PlayOrder.ShuffleEpisodesInOrder, hours, movieLibraries.Count > 0) : null;
            }

            case LayoutFill.Kids:
                return a.KidsHours >= t.KidsMinHours
                    ? new FillContent(libraries.Select(l => LibrarySource(l, null, "G")), PlayOrder.ShuffleEpisodesInOrder, a.KidsHours, movieLibraries.Count > 0)
                    : null;
            case LayoutFill.Family:
                return a.FamilyHours >= t.FamilyMoviesMinHours
                    ? new FillContent(libraries.Select(l => LibrarySource(l, null, "PG")), PlayOrder.ShuffleEpisodesInOrder, a.FamilyHours, movieLibraries.Count > 0)
                    : null;
            case LayoutFill.FamilyMovies:
            {
                var hours = a.Ratings.Where(r => (RatingLadder.Rank(r.Rating) ?? int.MaxValue) <= 3).Sum(r => r.MovieHours);
                return movieLibraries.Count > 0 && hours >= t.FamilyMoviesMinHours
                    ? new FillContent(movieLibraries.Select(l => LibrarySource(l, SourceKind.Movie, "PG")), PlayOrder.Shuffle, hours, true)
                    : null;
            }

            case LayoutFill.Movies:
                return movieLibraries.Count > 0 && a.Totals.MovieHours >= t.GenreMinHours
                    ? new FillContent(movieLibraries.Select(l => LibrarySource(l, SourceKind.Movie, null)), PlayOrder.Shuffle, a.Totals.MovieHours, true)
                    : null;
            case LayoutFill.Reruns:
            {
                var comfort = a.Series.Where(StationSuggester.IsComfortRerun).ToList();
                var comfortHours = comfort.Sum(s => s.Hours);
                if (comfort.Count >= t.ComfortMinSeries && comfortHours >= t.ComfortMinHours)
                {
                    return new FillContent(comfort.Select(s => new ContentSource { Kind = SourceKind.Series, ItemId = s.Id, DisplayName = s.Name }), PlayOrder.ShuffleEpisodesInOrder, comfortHours, false);
                }

                return tvLibraries.Count > 0 && a.Totals.EpisodeHours >= t.GenreMinHours
                    ? new FillContent(tvLibraries.Select(l => LibrarySource(l, SourceKind.Episode, null)), PlayOrder.ShuffleEpisodesInOrder, a.Totals.EpisodeHours, false)
                    : null;
            }

            case LayoutFill.Grownup:
            {
                var hours = a.Ratings.Where(r => (RatingLadder.Rank(r.Rating) ?? -1) >= 4).Sum(r => r.MovieHours + r.EpisodeHours);
                if (hours < t.GenreMinHours)
                {
                    return null;
                }

                var sources = libraries.Select(l =>
                {
                    var s = LibrarySource(l, null, null);
                    s.Ratings = GrownupRatings.ToList();
                    return s;
                });
                return new FillContent(sources, PlayOrder.ShuffleEpisodesInOrder, hours, movieLibraries.Count > 0);
            }

            default:
                return null;
        }
    }

    /// <summary>"Morning cartoons (weekdays 06:30), ... ; everything else in between. No kids content, so no Saturday morning cartoons."</summary>
    private static string Describe(WeekLayout layout, IReadOnlyList<SlotBlock> blocks, IReadOnlyList<string> dropped)
    {
        var parts = blocks.Select(b => (string.IsNullOrWhiteSpace(b.Name) ? "Block" : b.Name) + " (" + DaysText(b.Days) + " " + b.StartLocal + ")");
        var text = string.Join(", ", parts) + "; " + FillerText(layout.Filler) + " in between.";
        if (dropped.Count > 0)
        {
            text += " Left out for want of content: " + string.Join(", ", dropped) + ".";
        }

        return text;
    }

    private static string FillerText(LayoutFill fill) => fill switch
    {
        LayoutFill.Movies => "shuffled movies",
        LayoutFill.Family => "everything rated PG or below",
        LayoutFill.Kids => "everything rated G or below",
        LayoutFill.FamilyMovies => "family movies",
        LayoutFill.Reruns => "reruns",
        LayoutFill.Grownup => "grown-up shows and movies",
        _ => "everything else"
    };

    /// <summary>"every day", "weekdays", "weekends", or the short day names.</summary>
    public static string DaysText(IReadOnlyCollection<DayOfWeek> days)
    {
        var set = new HashSet<DayOfWeek>(days);
        if (set.Count == 7)
        {
            return "every day";
        }

        if (set.Count == 5 && !set.Contains(DayOfWeek.Saturday) && !set.Contains(DayOfWeek.Sunday))
        {
            return "weekdays";
        }

        if (set.Count == 2 && set.Contains(DayOfWeek.Saturday) && set.Contains(DayOfWeek.Sunday))
        {
            return "weekends";
        }

        return string.Join(", ", EveryDay.Where(set.Contains).Select(d => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(d)));
    }

    private static ContentSource LibrarySource(LibraryStatDto library, SourceKind? kind, string? maxRating)
    {
        var source = new ContentSource { Kind = SourceKind.Library, ItemId = library.Id, DisplayName = library.Name, MaxRating = maxRating };
        if (kind.HasValue)
        {
            source.ItemKinds.Add(kind.Value);
        }

        return source;
    }

    private static WeekLayout Layout(string id, string name, string description, string stationName, LayoutFill filler, DaypartRule[] dayparts, params LayoutBlock[] blocks) => new()
    {
        Id = Guid.Parse(id),
        Name = name,
        Description = description,
        StationName = stationName,
        BuiltIn = true,
        Filler = filler,
        DaypartRules = dayparts.ToList(),
        Blocks = blocks.ToList()
    };

    private static LayoutBlock Block(string name, DayOfWeek[] days, string start, int minutes, PlayOrder order, LayoutFill fill) => new()
    {
        Fill = fill,
        Block = new SlotBlock
        {
            Id = Guid.Empty,
            Name = name,
            Days = days.ToList(),
            StartLocal = start,
            DurationMinutes = minutes,
            Order = order,
            Overflow = SlotOverflow.FillerRemainder,
            RemainderMayOverrun = true,
            MaxStartDelayMinutes = 30
        }
    };

    private sealed class FillContent
    {
        public FillContent(IEnumerable<ContentSource> sources, PlayOrder order, double hours, bool movies)
        {
            Sources = sources.ToList();
            Order = order;
            Hours = hours;
            Movies = movies;
        }

        public List<ContentSource> Sources { get; }

        public PlayOrder Order { get; }

        public double Hours { get; }

        /// <summary>Whether the content has movies in it, so the station pads by length.</summary>
        public bool Movies { get; }
    }
}
