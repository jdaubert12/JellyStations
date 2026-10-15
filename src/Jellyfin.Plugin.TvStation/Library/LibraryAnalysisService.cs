using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TvStation.Api.Dto;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvStation.Library;

/// <summary>Collects <see cref="LibraryFact"/>s from Jellyfin, runs <see cref="LibraryAnalyzer"/>, and caches the result.</summary>
public sealed class LibraryAnalysisService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(15);

    private readonly ILibraryManager _libraryManager;
    private readonly LibraryContentResolver _resolver;
    private readonly ILogger<LibraryAnalysisService> _logger;
    private readonly object _lock = new();
    private LibraryAnalysisDto? _cached;

    public LibraryAnalysisService(ILibraryManager libraryManager, LibraryContentResolver resolver, ILogger<LibraryAnalysisService> logger)
    {
        _libraryManager = libraryManager;
        _resolver = resolver;
        _logger = logger;
    }

    public LibraryAnalysisDto Get(bool refresh = false)
    {
        lock (_lock)
        {
            if (!refresh && _cached is not null && DateTime.UtcNow - _cached.ComputedUtc < CacheFor)
            {
                return _cached;
            }

            var started = DateTime.UtcNow;
            var facts = CollectFacts();
            var collections = CollectCollections();
            _cached = LibraryAnalyzer.Analyze(facts, collections, DateTime.UtcNow);
            _logger.LogInformation(
                "TvStation: analyzed library: {Movies} movie(s), {Episodes} episode(s) in {Series} series, {Collections} collection(s) in {Ms} ms",
                _cached.Totals.Movies,
                _cached.Totals.Episodes,
                _cached.Totals.Series,
                collections.Count,
                (int)(DateTime.UtcNow - started).TotalMilliseconds);
            return _cached;
        }
    }

    public void Invalidate()
    {
        lock (_lock)
        {
            _cached = null;
        }
    }

    /// <summary>Movies and episodes per library, with episode genre/tag/rating inherited from the series when missing.</summary>
    private List<LibraryFact> CollectFacts()
    {
        var facts = new List<LibraryFact>();
        var seen = new HashSet<Guid>();
        var seriesCache = new Dictionary<Guid, Series?>();

        Series? SeriesOf(Episode e)
        {
            if (e.SeriesId == Guid.Empty)
            {
                return null;
            }

            if (!seriesCache.TryGetValue(e.SeriesId, out var s))
            {
                s = _libraryManager.GetItemById(e.SeriesId) as Series;
                seriesCache[e.SeriesId] = s;
            }

            return s;
        }

        foreach (var folder in _libraryManager.GetVirtualFolders().Where(v => Guid.TryParse(v.ItemId, out _)))
        {
            var libraryId = Guid.Parse(folder.ItemId);
            IReadOnlyList<BaseItem> items;
            try
            {
                items = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
                    ParentId = libraryId,
                    IsVirtualItem = false,
                    Recursive = true,
                    DtoOptions = new DtoOptions(false)
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TvStation: could not read library {Name} for analysis", folder.Name);
                continue;
            }

            foreach (var item in items)
            {
                if (item is not Video video || video.RunTimeTicks is null || video.RunTimeTicks <= 0 || !seen.Add(video.Id))
                {
                    continue;
                }

                var fact = new LibraryFact
                {
                    Id = video.Id,
                    IsMovie = video is Movie,
                    Title = video.Name ?? string.Empty,
                    Rating = video.OfficialRating,
                    Year = video.ProductionYear,
                    RuntimeTicks = video.RunTimeTicks.Value,
                    Genres = video.Genres?.ToList() ?? new List<string>(),
                    Tags = video.Tags?.ToList() ?? new List<string>(),
                    LibraryId = libraryId,
                    LibraryName = folder.Name
                };

                if (video is Episode episode)
                {
                    var series = SeriesOf(episode);
                    fact.SeriesId = episode.SeriesId == Guid.Empty ? null : episode.SeriesId;
                    fact.SeriesName = episode.SeriesName ?? series?.Name;
                    fact.SeasonNumber = episode.ParentIndexNumber;
                    if (string.IsNullOrWhiteSpace(fact.Rating))
                    {
                        fact.Rating = series?.OfficialRating;
                    }

                    if (fact.Genres.Count == 0 && series?.Genres is { Length: > 0 } g)
                    {
                        fact.Genres = g.ToList();
                    }

                    if (fact.Tags.Count == 0 && series?.Tags is { Length: > 0 } t)
                    {
                        fact.Tags = t.ToList();
                    }

                    if (!fact.Year.HasValue)
                    {
                        fact.Year = series?.ProductionYear;
                    }
                }

                facts.Add(fact);
            }
        }

        return facts;
    }

    private List<CollectionFact> CollectCollections()
    {
        var result = new List<CollectionFact>();
        try
        {
            var boxSets = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                Recursive = true,
                DtoOptions = new DtoOptions(false)
            });

            foreach (var box in boxSets.OfType<Folder>())
            {
                result.Add(new CollectionFact
                {
                    Id = box.Id,
                    Name = box.Name ?? string.Empty,
                    ItemIds = _resolver.FlattenChildren(box).Select(i => i.Id).Distinct().ToList()
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TvStation: could not read collections for analysis");
        }

        return result;
    }
}
