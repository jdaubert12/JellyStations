using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyStations.Api.Dto;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.JellyStations.Library;

/// <summary>Read-only library lookups for the admin content picker.</summary>
public sealed class LibrarySearchService
{
    private readonly ILibraryManager _libraryManager;
    private readonly LibraryContentResolver _resolver;

    public LibrarySearchService(ILibraryManager libraryManager, LibraryContentResolver resolver)
    {
        _libraryManager = libraryManager;
        _resolver = resolver;
    }

    public IReadOnlyList<LibraryItemDto> Search(string? term, IReadOnlyList<string>? kinds, int limit)
    {
        var types = (kinds is null || kinds.Count == 0 ? new[] { "Movie", "Series", "BoxSet", "Playlist" } : kinds)
            .Select(ToKind)
            .Where(k => k.HasValue)
            .Select(k => k!.Value)
            .ToArray();

        if (types.Length == 0)
        {
            return Array.Empty<LibraryItemDto>();
        }

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = types,
            Recursive = true,
            IsVirtualItem = false,
            Limit = Math.Clamp(limit, 1, 200),
            OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) },
            DtoOptions = new DtoOptions(false)
        };

        if (!string.IsNullOrWhiteSpace(term))
        {
            query.SearchTerm = term.Trim();
        }

        return _libraryManager.GetItemList(query).Select(ToDto).ToList();
    }

    public IReadOnlyList<LibraryItemDto> Children(Guid id)
    {
        var item = _libraryManager.GetItemById(id);
        return item switch
        {
            Series s => _resolver.EpisodesOfSeries(s.Id).Select(ToDto).ToList(),
            Season s => _resolver.EpisodesOfSeason(s.Id).Select(ToDto).ToList(),
            Folder f => _resolver.FlattenChildren(f).Select(ToDto).ToList(),
            _ => Array.Empty<LibraryItemDto>()
        };
    }

    public IReadOnlyList<GenreDto> Genres()
    {
        var result = new Dictionary<string, GenreDto>(StringComparer.OrdinalIgnoreCase);
        var genres = _libraryManager.GetGenres(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            // Jellyfin 12+ only computes per-genre counts when this field is requested; older servers always did.
            DtoOptions = new DtoOptions(false) { Fields = new[] { ItemFields.ItemCounts } }
        });

        foreach (var (item, counts) in genres.Items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Name))
            {
                continue;
            }

            if (!result.TryGetValue(item.Name, out var dto))
            {
                dto = new GenreDto { Name = item.Name };
                result[item.Name] = dto;
            }

            if (counts is not null)
            {
                // Jellyfin 12 rolls episodes of matching series into EpisodeCount, so SeriesCount is not added.
                dto.MovieCount += counts.MovieCount;
                dto.EpisodeCount += counts.EpisodeCount;
            }
        }

        return result.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Every tag on a movie or series, with counts, sorted by name.</summary>
    public IReadOnlyList<TagDto> Tags()
    {
        var result = new Dictionary<string, TagDto>(StringComparer.OrdinalIgnoreCase);
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            DtoOptions = new DtoOptions(false)
        });

        foreach (var item in items)
        {
            foreach (var tag in item.Tags ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(tag))
                {
                    continue;
                }

                if (!result.TryGetValue(tag, out var dto))
                {
                    dto = new TagDto { Name = tag };
                    result[tag] = dto;
                }

                if (item is Series)
                {
                    dto.SeriesCount++;
                }
                else
                {
                    dto.MovieCount++;
                }
            }
        }

        return result.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<LibraryItemDto> OfKind(BaseItemKind kind)
    {
        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            Recursive = true,
            OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) },
            DtoOptions = new DtoOptions(false)
        }).Select(ToDto).ToList();
    }

    public IReadOnlyList<LibraryFolderDto> Libraries()
    {
        return _libraryManager.GetVirtualFolders()
            .Where(v => Guid.TryParse(v.ItemId, out _))
            .Select(v => new LibraryFolderDto
            {
                Id = Guid.Parse(v.ItemId),
                Name = v.Name,
                CollectionType = v.CollectionType?.ToString()
            })
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static LibraryItemDto ToDto(BaseItem item)
    {
        var dto = new LibraryItemDto
        {
            Id = item.Id,
            Name = item.Name ?? string.Empty,
            Kind = KindName(item),
            Year = item.ProductionYear,
            RuntimeTicks = LibraryContentResolver.TotalRuntimeTicks(item),
            HasImage = item.HasImage(ImageType.Primary)
        };

        if (item is Episode e)
        {
            dto.SeriesName = e.SeriesName;
            dto.SeasonNumber = e.ParentIndexNumber;
            dto.EpisodeNumber = e.IndexNumber;
        }
        else if (item is Season s)
        {
            dto.SeriesName = s.SeriesName;
            dto.SeasonNumber = s.IndexNumber;
        }
        else if (item is MediaBrowser.Controller.Entities.Audio.Audio audio)
        {
            // The picker shows this under the name, as it shows the series of an episode.
            dto.SeriesName = string.Join(", ", (audio.Artists ?? Array.Empty<string>()).Take(2));
            dto.EpisodeNumber = audio.IndexNumber;
        }
        else if (item is MusicAlbum album)
        {
            dto.SeriesName = string.Join(", ", (album.AlbumArtists ?? Array.Empty<string>()).Take(2));
        }

        return dto;
    }

    private static string KindName(BaseItem item) => item switch
    {
        Movie => "Movie",
        Series => "Series",
        Season => "Season",
        Episode => "Episode",
        BoxSet => "BoxSet",
        Playlist => "Playlist",
        Video => "Video",
        MusicAlbum => "Album",
        MusicArtist => "Artist",
        MusicGenre => "MusicGenre",
        MediaBrowser.Controller.Entities.Audio.Audio => "Audio",
        _ => item.GetType().Name
    };

    private static BaseItemKind? ToKind(string s) => s.ToLowerInvariant() switch
    {
        "movie" => BaseItemKind.Movie,
        "series" => BaseItemKind.Series,
        "season" => BaseItemKind.Season,
        "episode" => BaseItemKind.Episode,
        "boxset" or "collection" => BaseItemKind.BoxSet,
        "playlist" => BaseItemKind.Playlist,
        "video" => BaseItemKind.Video,
        "album" or "musicalbum" => BaseItemKind.MusicAlbum,
        "artist" or "musicartist" => BaseItemKind.MusicArtist,
        "musicgenre" => BaseItemKind.MusicGenre,
        "audio" or "track" => BaseItemKind.Audio,
        _ => null
    };
}
