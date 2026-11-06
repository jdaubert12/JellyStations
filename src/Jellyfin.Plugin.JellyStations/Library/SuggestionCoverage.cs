using System.Globalization;
using Jellyfin.Plugin.JellyStations.Api.Dto;
using Jellyfin.Plugin.JellyStations.Models;
using static Jellyfin.Plugin.JellyStations.Library.LibraryFacts;

namespace Jellyfin.Plugin.JellyStations.Library;

/// <summary>
/// Which movies and episodes a station definition would play, worked out from <see cref="LibraryFact"/>s alone so
/// it needs no library and no compile. It follows <see cref="LibraryContentResolver"/> for the source kinds the
/// suggester writes (library, genre, tag, series, collection, hand-picked titles, with their rating, year, kind,
/// season and picture-size filters and the excluded tags); a playlist or a single season is not known to the facts and matches
/// nothing. Good enough to say how much of a suggestion the channels already play.
/// </summary>
public sealed class FactIndex
{
    private readonly IReadOnlyList<LibraryFact> _facts;
    private readonly Dictionary<Guid, LibraryFact> _byId;
    private readonly ILookup<Guid, LibraryFact> _bySeries;
    private readonly Dictionary<Guid, List<Guid>> _collections;

    public FactIndex(IReadOnlyList<LibraryFact> facts, IReadOnlyList<CollectionFact>? collections = null)
    {
        _facts = facts;
        _byId = new Dictionary<Guid, LibraryFact>();
        foreach (var f in facts)
        {
            _byId[f.Id] = f;
        }

        _bySeries = facts.Where(f => f.SeriesId.HasValue).ToLookup(f => f.SeriesId!.Value);
        _collections = new Dictionary<Guid, List<Guid>>();
        foreach (var c in collections ?? Array.Empty<CollectionFact>())
        {
            _collections[c.Id] = c.ItemIds;
        }
    }

    /// <summary>The ids of everything the station would play: its pool, its blocks and filler, or its lineup.</summary>
    public HashSet<Guid> ItemsOf(Station station, IEnumerable<string>? globalExcludedTags = null)
    {
        var tags = StationCompiler.EffectiveExcludedTags(station, globalExcludedTags);
        var ids = new HashSet<Guid>();
        var blocks = (station.Slots?.Blocks ?? new List<SlotBlock>()).SelectMany(b => b.Sources ?? new List<ContentSource>());
        IEnumerable<ContentSource> sources = station.Mode switch
        {
            ScheduleMode.Lineup => Array.Empty<ContentSource>(),
            ScheduleMode.Slots => blocks.Concat(station.Slots?.Filler?.Sources ?? new List<ContentSource>()),
            _ => (station.Pool?.Sources ?? new List<ContentSource>()).Concat(blocks)
        };

        foreach (var source in sources)
        {
            foreach (var fact in Match(source, tags))
            {
                ids.Add(fact.Id);
            }
        }

        if (station.Mode == ScheduleMode.Lineup)
        {
            foreach (var item in station.Lineup?.Items ?? new List<LineupItem>())
            {
                if (_byId.ContainsKey(item.ItemId))
                {
                    ids.Add(item.ItemId);
                }
            }
        }

        ids.ExceptWith(station.ExcludedItemIds ?? new List<Guid>());
        return ids;
    }

    /// <summary>Total running time of the given items, in hours (unknown ids count for nothing).</summary>
    public double Hours(IEnumerable<Guid> ids)
        => ids.Sum(id => _byId.TryGetValue(id, out var f) ? HoursOf(f.RuntimeTicks) : 0);

    private IEnumerable<LibraryFact> Match(ContentSource source, IReadOnlyCollection<string> excludedTags)
    {
        IEnumerable<LibraryFact> items;
        switch (source.Kind)
        {
            case SourceKind.Movie:
            case SourceKind.Episode:
                // A hand-picked title plays whatever its rating, year or tags say.
                return source.ItemId is { } id && _byId.TryGetValue(id, out var one) ? new[] { one } : Array.Empty<LibraryFact>();
            case SourceKind.Series:
                items = source.ItemId is { } series ? Seasons(_bySeries[series], source) : Array.Empty<LibraryFact>();
                break;
            case SourceKind.BoxSet:
                items = source.ItemId is { } box && _collections.TryGetValue(box, out var members)
                    ? Kinds(members.Where(_byId.ContainsKey).Select(m => _byId[m]), source)
                    : Array.Empty<LibraryFact>();
                break;
            case SourceKind.Genre:
                items = string.IsNullOrWhiteSpace(source.Genre) ? Array.Empty<LibraryFact>() : Kinds(_facts.Where(f => Has(f.Genres, source.Genre) && InLibrary(f, source.LibraryId)), source);
                break;
            case SourceKind.Tag:
                items = string.IsNullOrWhiteSpace(source.Tag) ? Array.Empty<LibraryFact>() : Kinds(_facts.Where(f => Has(f.Tags, source.Tag) && InLibrary(f, source.LibraryId)), source);
                break;
            case SourceKind.Library:
                items = source.ItemId is { } library ? Kinds(_facts.Where(f => f.LibraryId == library), source) : Array.Empty<LibraryFact>();
                break;
            default:
                return Array.Empty<LibraryFact>();
        }

        items = Years(Ratings(items, source), source);
        if (PictureSize.Normalize(source.MinResolution) is { } tier)
        {
            items = items.Where(f => PictureSize.Meets(f.Width, f.Height, tier));
        }

        var drop = LibraryContentResolver.EffectiveExcludedTags(source, excludedTags);
        return drop.Count == 0 ? items : items.Where(f => !f.Tags.Any(drop.Contains));
    }

    private static bool Has(List<string> values, string wanted)
        => values.Any(v => string.Equals(v.Trim(), wanted.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool InLibrary(LibraryFact fact, Guid? libraryId)
        => libraryId is not { } id || id == Guid.Empty || fact.LibraryId == id;

    private static IEnumerable<LibraryFact> Kinds(IEnumerable<LibraryFact> items, ContentSource source)
    {
        var movies = source.IncludesMovies;
        var episodes = source.IncludesEpisodes;
        return movies && episodes ? items : items.Where(f => f.IsMovie ? movies : episodes);
    }

    private static IEnumerable<LibraryFact> Seasons(IEnumerable<LibraryFact> episodes, ContentSource source)
    {
        if (source.SeasonFrom is null && source.SeasonTo is null && !source.ExcludeSpecials)
        {
            return episodes;
        }

        return episodes.Where(e =>
        {
            var season = e.SeasonNumber;
            if (source.ExcludeSpecials && season == 0)
            {
                return false;
            }

            return (source.SeasonFrom is null && source.SeasonTo is null)
                || (season is > 0 && (source.SeasonFrom is null || season >= source.SeasonFrom) && (source.SeasonTo is null || season <= source.SeasonTo));
        });
    }

    /// <summary>MaxRating / Ratings / AllowUnrated, as <see cref="LibraryContentResolver.FilterRatings"/> applies them.</summary>
    private static IEnumerable<LibraryFact> Ratings(IEnumerable<LibraryFact> items, ContentSource source)
    {
        var hasMax = RatingLadder.Rank(source.MaxRating) is not null;
        var allow = (source.Ratings ?? new List<string>()).Select(RatingLadder.Normalize).Where(r => r is not null).Select(r => r!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!hasMax && allow.Count == 0)
        {
            return items;
        }

        var unratedOk = source.AllowUnrated || allow.Contains("NR");
        return items.Where(f =>
        {
            if (RatingLadder.IsUnrated(f.Rating))
            {
                var norm = RatingLadder.Normalize(f.Rating);
                return unratedOk || (norm is not null && allow.Contains(norm));
            }

            if (hasMax && !RatingLadder.IsAtMost(f.Rating, source.MaxRating))
            {
                return false;
            }

            return allow.Count == 0 || allow.Contains(RatingLadder.Normalize(f.Rating)!);
        });
    }

    private static IEnumerable<LibraryFact> Years(IEnumerable<LibraryFact> items, ContentSource source)
    {
        if (source.YearFrom is null && source.YearTo is null)
        {
            return items;
        }

        return items.Where(f => f.Year is { } year
            && (source.YearFrom is null || year >= source.YearFrom)
            && (source.YearTo is null || year <= source.YearTo));
    }
}

/// <summary>
/// Sets the suggestions against what the channels already play: how much of each is new, which are fully covered,
/// the order within a category, and the station that collects whatever no channel plays. Pure.
/// </summary>
public static class SuggestionCoverage
{
    public const string LeftoversKey = "leftovers";

    public const string LeftoversCategory = "Not on a channel";

    public const string LeftoversName = "Everything Else";

    /// <summary>
    /// Fills in <see cref="StationSuggestionDto.NewHours"/> and <see cref="StationSuggestionDto.Covered"/> (and the
    /// exact content hours) for every suggestion whose content the facts can name. When any station is counted,
    /// each category is then ordered by new hours, most first; categories keep their order.
    /// </summary>
    public static void Apply(List<StationSuggestionDto> suggestions, FactIndex index, IReadOnlySet<Guid> onChannels, IEnumerable<string>? globalExcludedTags = null)
    {
        var tags = (globalExcludedTags ?? Array.Empty<string>()).ToList();
        foreach (var s in suggestions)
        {
            var items = index.ItemsOf(s.Station, tags);
            if (items.Count == 0)
            {
                // Content the facts cannot name (or none at all): nothing to compare, so it stays as the rule put it.
                s.NewHours = s.Hours;
                s.Covered = false;
                continue;
            }

            var fresh = items.Where(id => !onChannels.Contains(id)).ToList();
            s.Hours = Math.Round(index.Hours(items), 1);
            s.Covered = fresh.Count == 0;
            // Anything new at all shows as at least 0.1 h, so "nothing new" always means covered.
            s.NewHours = s.Covered ? 0 : Math.Min(s.Hours, Math.Max(0.1, Math.Round(index.Hours(fresh), 1)));
        }

        if (onChannels.Count == 0)
        {
            return;
        }

        var categories = suggestions.Select(s => s.Category).Distinct().ToList();
        var ordered = suggestions
            .Select((s, i) => (Suggestion: s, Index: i))
            .OrderBy(x => categories.IndexOf(x.Suggestion.Category))
            .ThenByDescending(x => x.Suggestion.NewHours)
            .ThenBy(x => x.Index)
            .Select(x => x.Suggestion)
            .ToList();
        suggestions.Clear();
        suggestions.AddRange(ordered);
    }

    /// <summary>
    /// The station that plays whatever no counted channel does: every such movie hand-picked, and every series with
    /// an episode left out (the whole show, so it runs in order). Null when no station is counted yet, or less than
    /// <paramref name="minHours"/> is left. Clip libraries and items carrying a globally excluded tag are not
    /// leftovers: the first are bumpers, the second are kept off the channels on purpose.
    /// </summary>
    public static StationSuggestionDto? Leftovers(IReadOnlyList<LibraryFact> facts, IReadOnlySet<Guid> clipLibraries, IReadOnlySet<Guid> onChannels, int channels, double minHours, IEnumerable<string> existingNames, IEnumerable<string>? globalExcludedTags = null)
    {
        if (channels <= 0)
        {
            return null;
        }

        var drop = new HashSet<string>((globalExcludedTags ?? Array.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()), StringComparer.OrdinalIgnoreCase);
        var programs = facts.Where(f => !(f.LibraryId is { } lib && clipLibraries.Contains(lib))).ToList();
        var left = programs.Where(f => !onChannels.Contains(f.Id) && !f.Tags.Any(t => drop.Contains(t.Trim()))).ToList();
        var newHours = left.Sum(f => HoursOf(f.RuntimeTicks));
        if (left.Count == 0 || newHours < minHours)
        {
            return null;
        }

        var movies = left.Where(f => f.IsMovie).OrderBy(f => f.Title, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Year).ToList();
        var seriesIds = left.Where(f => !f.IsMovie && f.SeriesId.HasValue).Select(f => f.SeriesId!.Value).ToHashSet();
        var series = programs
            .Where(f => !f.IsMovie && f.SeriesId.HasValue && seriesIds.Contains(f.SeriesId.Value))
            .GroupBy(f => f.SeriesId!.Value)
            .Select(g => (Id: g.Key, Name: g.Select(e => e.SeriesName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? string.Empty, Hours: g.Sum(e => HoursOf(e.RuntimeTicks)), Partly: g.Any(e => onChannels.Contains(e.Id))))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var station = new Station { Mode = ScheduleMode.Pool, Name = LeftoversName, PaddingByLength = movies.Count > 0 };
        station.Pool.Order = series.Count > 0 ? PlayOrder.ShuffleEpisodesInOrder : PlayOrder.Shuffle;
        station.Pool.Sources.AddRange(movies.Select(m => new ContentSource { Kind = SourceKind.Movie, ItemId = m.Id, DisplayName = m.Title }));
        station.Pool.Sources.AddRange(series.Select(s => new ContentSource { Kind = SourceKind.Series, ItemId = s.Id, DisplayName = s.Name }));

        var parts = new List<string>();
        if (movies.Count > 0)
        {
            parts.Add(movies.Count.ToString(CultureInfo.InvariantCulture) + (movies.Count == 1 ? " movie" : " movies"));
        }

        if (series.Count > 0)
        {
            parts.Add(series.Count.ToString(CultureInfo.InvariantCulture) + " series");
        }

        var partly = series.Count(s => s.Partly);
        var description = string.Join(" and ", parts) + " no channel plays yet, shuffled"
            + (series.Count > 0 ? ", each show's episodes in order" : string.Empty) + "."
            + (partly > 0 ? " " + partly.ToString(CultureInfo.InvariantCulture) + (partly == 1 ? " of the series is" : " of the series are") + " partly on a channel already; the whole show is included." : string.Empty);
        var hours = movies.Sum(m => HoursOf(m.RuntimeTicks)) + series.Sum(s => s.Hours);
        return new StationSuggestionDto
        {
            Key = LeftoversKey,
            Category = LeftoversCategory,
            Name = LeftoversName,
            Description = description,
            Hours = Math.Round(hours, 1),
            NewHours = Math.Min(Math.Round(hours, 1), Math.Max(0.1, Math.Round(newHours, 1))),
            Exists = existingNames.Any(n => string.Equals(n?.Trim(), LeftoversName, StringComparison.OrdinalIgnoreCase)),
            Station = station
        };
    }
}
