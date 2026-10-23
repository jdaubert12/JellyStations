using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.TvStation.Models;
using Jellyfin.Plugin.TvStation.Storage;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvStation.Library;

/// <summary>
/// Expands a weather channel's music sources (Jellyfin playlists, albums, artists, music genres, single tracks)
/// and its music folder into frozen <see cref="MusicTrack"/> snapshots, the way <see cref="LibraryContentResolver"/>
/// does for video. Folder files are probed for their length once and remembered.
/// </summary>
public sealed class MusicResolver
{
    private readonly ILibraryManager _libraryManager;
    private readonly MusicFolderScanner _folders;
    private readonly TvStationPaths _paths;
    private readonly ILogger<MusicResolver> _logger;

    public MusicResolver(ILibraryManager libraryManager, MusicFolderScanner folders, TvStationPaths paths, ILogger<MusicResolver> logger)
    {
        _libraryManager = libraryManager;
        _folders = folders;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>The kinds the music picker searches.</summary>
    public static readonly SourceKind[] MusicKinds = { SourceKind.Playlist, SourceKind.Album, SourceKind.Artist, SourceKind.MusicGenre, SourceKind.Audio };

    public List<MusicTrack> Expand(WeatherConfig config, List<SourceExpansion> expansions, List<ExcludedItem> excluded)
    {
        var result = new List<MusicTrack>();
        var seen = new HashSet<Guid>();
        foreach (var source in config.MusicSources)
        {
            var found = 0;
            var kept = 0;
            var alreadyListed = 0;
            foreach (var audio in ExpandSource(source))
            {
                found++;
                if (!seen.Add(audio.Id))
                {
                    alreadyListed++;
                    continue;
                }

                var track = ToTrack(audio, source.Kind.ToString(), excluded);
                if (track is not null)
                {
                    result.Add(track);
                    kept++;
                }
            }

            expansions.Add(new SourceExpansion
            {
                Section = "Music",
                Label = SourceExpansion.Describe(source),
                Kind = source.Kind,
                Found = found,
                Kept = kept,
                AlreadyListed = alreadyListed
            });
        }

        foreach (var name in Folders(config.MusicFolder))
        {
            var folder = _paths.ResolveMusicFolder(name);
            var tracks = _folders.Scan(folder);
            var kept = 0;
            foreach (var track in tracks)
            {
                if (seen.Add(track.Id))
                {
                    result.Add(track);
                    kept++;
                }
            }

            expansions.Add(new SourceExpansion
            {
                Section = "Music",
                Label = "Folder \"" + name + "\"",
                Kind = SourceKind.Folder,
                Found = tracks.Count,
                Kept = kept,
                AlreadyListed = tracks.Count - kept
            });
        }

        return result;
    }

    /// <summary>The folder names in a station's music folder setting: one, or several separated by ";" or line breaks.</summary>
    public static IReadOnlyList<string> Folders(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting))
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var part in setting.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (seen.Add(part))
            {
                list.Add(part);
            }
        }

        return list;
    }

    /// <summary>The audio items a music source names, in a sensible play order (album tracks in track order).</summary>
    public IEnumerable<Audio> ExpandSource(ContentSource source)
    {
        try
        {
            switch (source.Kind)
            {
                case SourceKind.Audio:
                    return source.ItemId.HasValue && _libraryManager.GetItemById(source.ItemId.Value) is Audio a ? new[] { a } : Array.Empty<Audio>();

                case SourceKind.Album:
                    return source.ItemId.HasValue ? TracksUnder(source.ItemId.Value) : Array.Empty<Audio>();

                case SourceKind.Artist:
                    return source.ItemId.HasValue
                        ? Query(q => q.ArtistIds = new[] { source.ItemId.Value })
                        : Array.Empty<Audio>();

                case SourceKind.MusicGenre:
                    {
                        if (source.ItemId.HasValue && _libraryManager.GetItemById(source.ItemId.Value) is { } genre)
                        {
                            return Query(q => q.Genres = new[] { genre.Name });
                        }

                        return string.IsNullOrWhiteSpace(source.Genre) ? Array.Empty<Audio>() : Query(q => q.Genres = new[] { source.Genre! });
                    }

                case SourceKind.Playlist:
                    {
                        var playlist = source.ItemId.HasValue ? _libraryManager.GetItemById(source.ItemId.Value) as Playlist : null;
                        return playlist is null ? Array.Empty<Audio>() : FlattenAudio(playlist.GetLinkedChildren());
                    }

                default:
                    return Array.Empty<Audio>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TvStation: failed to expand music source {Kind} {Id}", source.Kind, source.ItemId);
            return Array.Empty<Audio>();
        }
    }

    private IEnumerable<Audio> FlattenAudio(IEnumerable<BaseItem> items)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case Audio a:
                    yield return a;
                    break;
                case MusicAlbum album:
                    foreach (var t in TracksUnder(album.Id))
                    {
                        yield return t;
                    }

                    break;
                case MusicArtist artist:
                    foreach (var t in Query(q => q.ArtistIds = new[] { artist.Id }))
                    {
                        yield return t;
                    }

                    break;
                case Folder f when f is not Playlist:
                    foreach (var t in TracksUnder(f.Id))
                    {
                        yield return t;
                    }

                    break;
            }
        }
    }

    private IReadOnlyList<Audio> TracksUnder(Guid parentId) => Query(q => q.ParentId = parentId);

    private IReadOnlyList<Audio> Query(Action<InternalItemsQuery> configure)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            Recursive = true,
            IsVirtualItem = false,
            OrderBy = new[] { (ItemSortBy.Album, SortOrder.Ascending), (ItemSortBy.ParentIndexNumber, SortOrder.Ascending), (ItemSortBy.IndexNumber, SortOrder.Ascending), (ItemSortBy.SortName, SortOrder.Ascending) },
            DtoOptions = new DtoOptions(false)
        };
        configure(query);
        return _libraryManager.GetItemList(query).OfType<Audio>().ToList();
    }

    public static MusicTrack? ToTrack(Audio audio, string source, List<ExcludedItem> excluded)
    {
        var name = string.IsNullOrWhiteSpace(audio.Name) ? System.IO.Path.GetFileNameWithoutExtension(audio.Path ?? string.Empty) : audio.Name;
        if (string.IsNullOrEmpty(audio.Path))
        {
            excluded.Add(new ExcludedItem { ItemId = audio.Id, Name = name, Reason = ExclusionReason.NoPath });
            return null;
        }

        if (audio.RunTimeTicks is null or <= 0)
        {
            excluded.Add(new ExcludedItem { ItemId = audio.Id, Name = name, Reason = ExclusionReason.NoRuntime });
            return null;
        }

        return new MusicTrack
        {
            Id = audio.Id,
            Path = audio.Path,
            RuntimeTicks = audio.RunTimeTicks.Value,
            Title = name,
            Artist = audio.Artists?.FirstOrDefault() ?? audio.AlbumArtists?.FirstOrDefault(),
            Source = source
        };
    }
}
