using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.TvStation.Models;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvStation.Library;

/// <summary>Expands content sources into Jellyfin-free PlayableItem snapshots.</summary>
public sealed class LibraryContentResolver
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryContentResolver> _logger;

    public LibraryContentResolver(ILibraryManager libraryManager, ILogger<LibraryContentResolver> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Expands sources in order, de-duplicating by item id. Items carrying one of <paramref name="excludedTags"/> are
    /// dropped from every source except hand-picked Movie/Episode sources and a Tag source for that same tag; each is
    /// recorded once in <paramref name="excluded"/> (and snapshotted in <paramref name="tagExcluded"/> when given).
    /// An item hand-picked anywhere in <paramref name="sources"/> is never tag-dropped by another source, whatever the
    /// order, so adding a movie on purpose always wins over a tag that would otherwise hide it.
    /// </summary>
    public List<PlayableItem> Expand(
        IEnumerable<ContentSource> sources,
        string audioLanguage,
        List<ExcludedItem> excluded,
        IReadOnlyCollection<string>? excludedTags = null,
        List<PlayableItem>? tagExcluded = null,
        Action<ContentSource, int, int, int>? onSource = null)
    {
        var result = new List<PlayableItem>();
        var seen = new HashSet<Guid>();
        var sourceList = sources as IReadOnlyCollection<ContentSource> ?? sources.ToList();
        var pinned = HandPickedIds(sourceList);
        foreach (var source in sourceList)
        {
            var block = new List<PlayableItem>();
            var dropTags = EffectiveExcludedTags(source, excludedTags);
            var found = 0;
            var alreadyListed = 0;
            foreach (var item in ExpandSource(source))
            {
                found++;
                if (!seen.Add(item.Id))
                {
                    alreadyListed++;
                    continue;
                }

                var playable = ToPlayable(item, audioLanguage, excluded);
                if (playable is null)
                {
                    continue;
                }

                if (!pinned.Contains(playable.ItemId) && HasExcludedTag(playable, dropTags))
                {
                    if (tagExcluded is null || tagExcluded.All(t => t.ItemId != playable.ItemId))
                    {
                        tagExcluded?.Add(playable);
                        excluded.Add(new ExcludedItem { ItemId = playable.ItemId, Name = playable.DisplayTitle, Reason = ExclusionReason.TagExcluded });
                    }

                    continue;
                }

                block.Add(playable);
            }

            onSource?.Invoke(source, found, block.Count, alreadyListed);

            // Weight n repeats the whole source block n times so sequential order stays intact (E1..En, E1..En).
            var weight = Math.Clamp(source.Weight, 1, 10);
            for (var w = 0; w < weight; w++)
            {
                result.AddRange(block);
            }
        }

        return result;
    }

    /// <summary>
    /// The tags that drop items from <paramref name="source"/>: none for hand-picked Movie/Episode sources, and never
    /// the tag a Tag source was built from (asking for it wins over excluding it).
    /// </summary>
    public static HashSet<string> EffectiveExcludedTags(ContentSource source, IEnumerable<string>? excludedTags)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (excludedTags is null || source.Kind is SourceKind.Movie or SourceKind.Episode)
        {
            return set;
        }

        foreach (var tag in excludedTags)
        {
            if (!string.IsNullOrWhiteSpace(tag))
            {
                set.Add(tag.Trim());
            }
        }

        if (source.Kind == SourceKind.Tag && !string.IsNullOrWhiteSpace(source.Tag))
        {
            set.Remove(source.Tag.Trim());
        }

        return set;
    }

    /// <summary>
    /// For each of <paramref name="itemIds"/>, the first source in <paramref name="sources"/> that already plays it, applying
    /// the excluded tags the way <see cref="Expand"/> does. Hand-picked Movie/Episode sources are not consulted: the point
    /// is to tell the editor that a wider source (genre, tag, series, collection, playlist, library) already covers a hand-pick.
    /// </summary>
    public Dictionary<Guid, ContentSource> CoveredBy(IEnumerable<ContentSource> sources, IEnumerable<Guid> itemIds, IReadOnlyCollection<string>? excludedTags)
    {
        var result = new Dictionary<Guid, ContentSource>();
        var wanted = new HashSet<Guid>(itemIds.Where(g => g != Guid.Empty));
        if (wanted.Count == 0)
        {
            return result;
        }

        var scratch = new List<ExcludedItem>();
        foreach (var source in sources)
        {
            if (source.Kind is SourceKind.Movie or SourceKind.Episode)
            {
                continue;
            }

            var dropTags = EffectiveExcludedTags(source, excludedTags);
            foreach (var item in ExpandSource(source))
            {
                if (!wanted.Contains(item.Id) || result.ContainsKey(item.Id))
                {
                    continue;
                }

                var playable = ToPlayable(item, string.Empty, scratch);
                if (playable is null || HasExcludedTag(playable, dropTags))
                {
                    continue;
                }

                result[item.Id] = source;
                if (result.Count == wanted.Count)
                {
                    return result;
                }
            }
        }

        return result;
    }

    /// <summary>The item ids named by hand-picked Movie/Episode sources: these are never dropped by an excluded tag.</summary>
    public static HashSet<Guid> HandPickedIds(IEnumerable<ContentSource> sources)
    {
        var set = new HashSet<Guid>();
        foreach (var source in sources)
        {
            if (source.Kind is SourceKind.Movie or SourceKind.Episode && source.ItemId is { } id && id != Guid.Empty)
            {
                set.Add(id);
            }
        }

        return set;
    }

    /// <summary>True if the item carries any of the given tags (case-insensitive).</summary>
    public static bool HasExcludedTag(PlayableItem item, IReadOnlyCollection<string> excludedTags)
        => excludedTags.Count > 0 && item.Tags.Any(t => excludedTags.Contains(t, StringComparer.OrdinalIgnoreCase));

    /// <summary>Snapshot a single item by id (used for lineups; duplicates allowed).</summary>
    public PlayableItem? Single(Guid itemId, string audioLanguage, List<ExcludedItem> excluded)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            excluded.Add(new ExcludedItem { ItemId = itemId, Name = itemId.ToString(), Reason = ExclusionReason.NotFound });
            return null;
        }

        return ToPlayable(item, audioLanguage, excluded);
    }

    public IEnumerable<BaseItem> ExpandSource(ContentSource source)
    {
        try
        {
            switch (source.Kind)
            {
                case SourceKind.Movie:
                case SourceKind.Episode:
                    {
                        var item = source.ItemId.HasValue ? _libraryManager.GetItemById(source.ItemId.Value) : null;
                        return item is null ? Array.Empty<BaseItem>() : new[] { item };
                    }

                case SourceKind.Series:
                    return source.ItemId.HasValue ? Tuned(FilterSeasons(EpisodesOfSeries(source.ItemId.Value), source), source) : Array.Empty<BaseItem>();

                case SourceKind.Season:
                    return source.ItemId.HasValue ? EpisodesOfSeason(source.ItemId.Value) : Array.Empty<BaseItem>();

                case SourceKind.BoxSet:
                case SourceKind.Playlist:
                    {
                        var folder = source.ItemId.HasValue ? _libraryManager.GetItemById(source.ItemId.Value) as Folder : null;
                        return folder is null ? Array.Empty<BaseItem>() : Tuned(FilterKinds(FlattenChildren(folder), source), source);
                    }

                case SourceKind.Genre:
                    return Tuned(ByGenre(source), source);

                case SourceKind.Tag:
                    return Tuned(ByTag(source), source);

                case SourceKind.Library:
                    return source.ItemId.HasValue ? Tuned(FilterKinds(VideosUnder(source.ItemId.Value), source), source) : Array.Empty<BaseItem>();

                default:
                    return Array.Empty<BaseItem>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TvStation: failed to expand source {Kind} {Id}", source.Kind, source.ItemId);
            return Array.Empty<BaseItem>();
        }
    }

    /// <summary>Rating and year filters for every source kind except explicit Movie/Episode picks.</summary>
    private IEnumerable<BaseItem> Tuned(IEnumerable<BaseItem> items, ContentSource source)
        => FilterYears(FilterRatings(items, source, SeriesOf), source, SeriesOf);

    private static BaseItem? SeriesOf(BaseItem item)
    {
        try
        {
            return item is Episode e ? e.Series : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Applies MaxRating / Ratings / AllowUnrated. Episodes without their own rating use the series rating,
    /// resolved through <paramref name="seriesOf"/> so the rule stays testable without a library.
    /// </summary>
    public static IEnumerable<BaseItem> FilterRatings(IEnumerable<BaseItem> items, ContentSource source, Func<BaseItem, BaseItem?> seriesOf)
    {
        var hasMax = RatingLadder.Rank(source.MaxRating) is not null;
        var allow = source.Ratings.Select(RatingLadder.Normalize).Where(r => r is not null).Select(r => r!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!hasMax && allow.Count == 0)
        {
            return items;
        }

        var unratedOk = source.AllowUnrated || allow.Contains("NR");
        return items.Where(i =>
        {
            var rating = i.OfficialRating;
            if (string.IsNullOrWhiteSpace(rating) && i is Episode)
            {
                rating = seriesOf(i)?.OfficialRating;
            }

            if (RatingLadder.IsUnrated(rating))
            {
                var norm = RatingLadder.Normalize(rating);
                return unratedOk || (norm is not null && allow.Contains(norm));
            }

            if (hasMax && !RatingLadder.IsAtMost(rating, source.MaxRating))
            {
                return false;
            }

            return allow.Count == 0 || allow.Contains(RatingLadder.Normalize(rating)!);
        });
    }

    /// <summary>
    /// Applies YearFrom / YearTo. Episodes use the series' production year (the show's year) and fall back to
    /// their own air year; items with no year at all are dropped when a bound is set.
    /// </summary>
    public static IEnumerable<BaseItem> FilterYears(IEnumerable<BaseItem> items, ContentSource source, Func<BaseItem, BaseItem?> seriesOf)
    {
        if (source.YearFrom is null && source.YearTo is null)
        {
            return items;
        }

        return items.Where(i =>
        {
            var year = i is Episode ? (seriesOf(i)?.ProductionYear ?? i.ProductionYear) : i.ProductionYear;
            return year is not null
                && (source.YearFrom is null || year >= source.YearFrom)
                && (source.YearTo is null || year <= source.YearTo);
        });
    }

    /// <summary>Applies a Series source's season range and specials switch.</summary>
    public static IEnumerable<BaseItem> FilterSeasons(IEnumerable<BaseItem> episodes, ContentSource source)
    {
        if (source.SeasonFrom is null && source.SeasonTo is null && !source.ExcludeSpecials)
        {
            return episodes;
        }

        return episodes.Where(e =>
        {
            var season = e.ParentIndexNumber;
            if (source.ExcludeSpecials && season == 0)
            {
                return false;
            }

            if (source.SeasonFrom is null && source.SeasonTo is null)
            {
                return true;
            }

            // Specials (season 0) sit outside any numbered range; unknown seasons are dropped when a range is set.
            return season is > 0
                && (source.SeasonFrom is null || season >= source.SeasonFrom)
                && (source.SeasonTo is null || season <= source.SeasonTo);
        });
    }

    /// <summary>Applies a container source's movie/episode filter. Empty or both kinds = everything.</summary>
    public static IEnumerable<BaseItem> FilterKinds(IEnumerable<BaseItem> items, ContentSource source)
    {
        var movies = source.ItemKinds.Count == 0 || source.ItemKinds.Contains(SourceKind.Movie);
        var episodes = source.ItemKinds.Count == 0 || source.ItemKinds.Contains(SourceKind.Episode);
        if (movies && episodes)
        {
            return items;
        }

        return items.Where(i => i is Episode ? episodes : movies);
    }

    public IReadOnlyList<BaseItem> EpisodesOfSeries(Guid seriesId)
    {
        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            AncestorIds = new[] { seriesId },
            IsVirtualItem = false,
            Recursive = true,
            OrderBy = new[] { (ItemSortBy.ParentIndexNumber, SortOrder.Ascending), (ItemSortBy.IndexNumber, SortOrder.Ascending) },
            DtoOptions = new DtoOptions(true)
        });
    }

    public IReadOnlyList<BaseItem> EpisodesOfSeason(Guid seasonId)
    {
        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            ParentId = seasonId,
            IsVirtualItem = false,
            Recursive = true,
            OrderBy = new[] { (ItemSortBy.IndexNumber, SortOrder.Ascending) },
            DtoOptions = new DtoOptions(true)
        });
    }

    public IEnumerable<BaseItem> FlattenChildren(Folder folder)
    {
        IEnumerable<BaseItem> children = folder is BoxSet or Playlist
            ? folder.GetLinkedChildren()
            : folder.GetRecursiveChildren(i => i is Video);

        foreach (var child in children)
        {
            switch (child)
            {
                case Series s:
                    foreach (var e in EpisodesOfSeries(s.Id))
                    {
                        yield return e;
                    }

                    break;
                case Season season:
                    foreach (var e in EpisodesOfSeason(season.Id))
                    {
                        yield return e;
                    }

                    break;
                case Video v:
                    yield return v;
                    break;
                case Folder f when f is not BoxSet and not Playlist:
                    foreach (var e in FlattenChildren(f))
                    {
                        yield return e;
                    }

                    break;
            }
        }
    }

    private IReadOnlyList<BaseItem> ByGenre(ContentSource source)
        => ByGenreOrTag(source, source.Genre, q => q.Genres = new[] { source.Genre! });

    /// <summary>Movies and episodes tagged with source.Tag, plus every episode of a series carrying the tag.</summary>
    private IReadOnlyList<BaseItem> ByTag(ContentSource source)
        => ByGenreOrTag(source, source.Tag, q => q.Tags = new[] { source.Tag!.Trim() });

    private IReadOnlyList<BaseItem> ByGenreOrTag(ContentSource source, string? term, Action<InternalItemsQuery> apply)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return Array.Empty<BaseItem>();
        }

        var kinds = new List<BaseItemKind>();
        if (source.ItemKinds.Count == 0 || source.ItemKinds.Contains(SourceKind.Movie))
        {
            kinds.Add(BaseItemKind.Movie);
        }

        if (source.ItemKinds.Count == 0 || source.ItemKinds.Contains(SourceKind.Episode))
        {
            kinds.Add(BaseItemKind.Episode);
        }

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = kinds.ToArray(),
            IsVirtualItem = false,
            Recursive = true,
            OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending), (ItemSortBy.ParentIndexNumber, SortOrder.Ascending), (ItemSortBy.IndexNumber, SortOrder.Ascending) },
            DtoOptions = new DtoOptions(true)
        };
        apply(query);
        if (source.LibraryId.HasValue && source.LibraryId.Value != Guid.Empty)
        {
            query.ParentId = source.LibraryId.Value;
        }

        var items = _libraryManager.GetItemList(query);

        // Episodes whose own genre/tag list is empty inherit from the series; include those too.
        if (kinds.Contains(BaseItemKind.Episode))
        {
            var seriesQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series },
                Recursive = true,
                DtoOptions = new DtoOptions(true)
            };
            apply(seriesQuery);
            if (source.LibraryId.HasValue && source.LibraryId.Value != Guid.Empty)
            {
                seriesQuery.ParentId = source.LibraryId.Value;
            }

            var list = items.ToList();
            var seen = new HashSet<Guid>(list.Select(i => i.Id));
            foreach (var series in _libraryManager.GetItemList(seriesQuery))
            {
                foreach (var ep in EpisodesOfSeries(series.Id))
                {
                    if (seen.Add(ep.Id))
                    {
                        list.Add(ep);
                    }
                }
            }

            return list;
        }

        return items;
    }

    private IReadOnlyList<BaseItem> VideosUnder(Guid libraryId)
    {
        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video },
            ParentId = libraryId,
            IsVirtualItem = false,
            Recursive = true,
            OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending), (ItemSortBy.ParentIndexNumber, SortOrder.Ascending), (ItemSortBy.IndexNumber, SortOrder.Ascending) },
            DtoOptions = new DtoOptions(true)
        });
    }

    public PlayableItem? ToPlayable(BaseItem item, string audioLanguage, List<ExcludedItem> excluded)
    {
        if (item is not Video video)
        {
            excluded.Add(new ExcludedItem { ItemId = item.Id, Name = item.Name ?? item.Id.ToString(), Reason = ExclusionReason.NotVideo });
            return null;
        }

        if (string.IsNullOrEmpty(video.Path))
        {
            excluded.Add(new ExcludedItem { ItemId = item.Id, Name = DisplayName(video), Reason = ExclusionReason.NoPath });
            return null;
        }

        var parts = PartsOf(video);
        if (parts.Any(v => v.RunTimeTicks is null || v.RunTimeTicks <= 0))
        {
            excluded.Add(new ExcludedItem { ItemId = item.Id, Name = DisplayName(video), Reason = ExclusionReason.NoRuntime });
            return null;
        }

        if (parts.Any(v => string.IsNullOrEmpty(v.Path)))
        {
            excluded.Add(new ExcludedItem { ItemId = item.Id, Name = DisplayName(video), Reason = ExclusionReason.NoPath });
            return null;
        }

        var playable = new PlayableItem
        {
            ItemId = video.Id,
            Path = video.Path,
            RuntimeTicks = parts.Sum(v => v.RunTimeTicks!.Value),
            Genres = video.Genres?.ToList() ?? new List<string>(),
            Tags = video.Tags?.ToList() ?? new List<string>(),
            Overview = video.Overview,
            ProductionYear = video.ProductionYear,
            OfficialRating = video.OfficialRating,
            CommunityRating = video.CommunityRating,
            PrimaryImagePath = video.PrimaryImagePath,
            AudioStreamIndex = PickAudioStream(video, audioLanguage),
            HasAudio = HasAudioTrack(video),
            HdrTransfer = HdrTransferOf(video)
        };

        if (parts.Count > 1)
        {
            playable.Parts = parts
                .Select(v => new PlayablePart { Path = v.Path, RuntimeTicks = v.RunTimeTicks!.Value, AudioStreamIndex = PickAudioStream(v, audioLanguage), HasAudio = HasAudioTrack(v), HdrTransfer = HdrTransferOf(v) })
                .ToList();
        }

        if (video is Episode episode)
        {
            playable.IsSeries = true;
            playable.Title = episode.SeriesName ?? episode.FindSeriesName() ?? "Unknown Series";
            playable.EpisodeTitle = episode.Name;
            playable.SeriesId = episode.SeriesId == Guid.Empty ? null : episode.SeriesId;
            playable.SeasonNumber = episode.ParentIndexNumber;
            playable.EpisodeNumber = episode.IndexNumber;
            if (string.IsNullOrEmpty(playable.PrimaryImagePath))
            {
                var series = episode.Series;
                playable.PrimaryImagePath = series?.PrimaryImagePath;
            }

            if (playable.Genres.Count == 0 && episode.Series?.Genres is { Length: > 0 } g)
            {
                playable.Genres = g.ToList();
            }

            if (playable.Tags.Count == 0 && episode.Series?.Tags is { Length: > 0 } t)
            {
                playable.Tags = t.ToList();
            }
        }
        else
        {
            playable.IsMovie = true;
            playable.Title = video.Name ?? "Untitled";
        }

        return playable;
    }

    /// <summary>The video and, for a stacked (multi-part) video, its additional parts in play order.</summary>
    public static IReadOnlyList<Video> PartsOf(Video video)
    {
        try
        {
            if (video.IsStacked)
            {
                var list = new List<Video> { video };
                list.AddRange(video.GetAdditionalParts());
                return list;
            }
        }
        catch (Exception)
        {
            // Parts could not be resolved; treat as a single file.
        }

        return new[] { video };
    }

    /// <summary>Runtime across every part of a stacked video, or null when any part lacks one.</summary>
    public static long? TotalRuntimeTicks(BaseItem item)
    {
        if (item is not Video video)
        {
            return item.RunTimeTicks;
        }

        var parts = PartsOf(video);
        if (parts.Any(v => v.RunTimeTicks is null || v.RunTimeTicks <= 0))
        {
            return null;
        }

        return parts.Sum(v => v.RunTimeTicks!.Value);
    }

    private static string DisplayName(Video v) => v is Episode e ? $"{e.SeriesName} - {e.Name}" : v.Name ?? v.Id.ToString();

    private static string? HdrTransferOf(Video video)
    {
        try
        {
            var stream = video.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Video);
            return stream is null ? null : HdrTransferOf(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The transfer characteristic the restreamer must tone map from: "smpte2084" (PQ: HDR10, HDR10+, Dolby Vision on
    /// an HDR10 base) or "arib-std-b67" (HLG); null for SDR. Jellyfin's range classification decides, the raw transfer
    /// tag names the curve (a stream Jellyfin calls HDR without a recognizable transfer is treated as PQ).
    /// </summary>
    public static string? HdrTransferOf(MediaStream stream)
    {
        var transfer = stream.ColorTransfer?.Trim().ToLowerInvariant();
        var tagged = transfer is "smpte2084" or "arib-std-b67";
        if (stream.VideoRange == VideoRange.SDR)
        {
            return null;
        }

        if (tagged)
        {
            return transfer;
        }

        return stream.VideoRange == VideoRange.HDR ? "smpte2084" : null;
    }

    private static bool HasAudioTrack(Video video)
    {
        try
        {
            var streams = video.GetMediaStreams();
            return streams.Count == 0 || streams.Any(s => s.Type == MediaStreamType.Audio);
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static int? PickAudioStream(Video video, string language)
    {
        try
        {
            var audio = video.GetMediaStreams().Where(s => s.Type == MediaStreamType.Audio).OrderBy(s => s.Index).ToList();
            if (audio.Count <= 1)
            {
                return null;
            }

            var idx = audio.FindIndex(s => !string.IsNullOrEmpty(language) && string.Equals(s.Language, language, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                idx = audio.FindIndex(s => s.IsDefault);
            }

            return idx < 0 ? null : idx;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
