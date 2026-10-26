namespace Jellyfin.Plugin.JellyStations.Api.Dto;

/// <summary>A library item as shown in the content picker.</summary>
public sealed class LibraryItemDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Movie, Series, Season, Episode, BoxSet, Playlist, Video.</summary>
    public string Kind { get; set; } = string.Empty;

    public int? Year { get; set; }

    public string? SeriesName { get; set; }

    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }

    public long? RuntimeTicks { get; set; }

    public bool HasImage { get; set; }

    public string? LibraryName { get; set; }
}

public sealed class GenreDto
{
    public string Name { get; set; } = string.Empty;

    public int MovieCount { get; set; }

    public int EpisodeCount { get; set; }
}

public sealed class TagDto
{
    public string Name { get; set; } = string.Empty;

    public int MovieCount { get; set; }

    public int SeriesCount { get; set; }
}

public sealed class LibraryFolderDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? CollectionType { get; set; }
}

/// <summary>The audio and subtitle languages found on an item's files, for the editor's per-source track choices.</summary>
public sealed class TrackLanguagesDto
{
    /// <summary>How many video files were looked at.</summary>
    public int Files { get; set; }

    public List<TrackLanguageDto> Audio { get; set; } = new();

    public List<TrackLanguageDto> Subtitles { get; set; } = new();
}

public sealed class TrackLanguageDto
{
    /// <summary>ISO 639-2 code as the plugin canonicalizes it ("jpn", "eng", "deu").</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>How many of the files carry a track in this language.</summary>
    public int Files { get; set; }
}
