namespace Jellyfin.Plugin.JellyStations.Music;

/// <summary>
/// A curated set of tracks the plugin can fetch for a weather channel. The plugin ships no audio: an automatic pack is a list
/// of URLs on the artist's own site with checksums, downloaded into the plugin's music folder on request; a manual pack is a
/// set of links to albums the person downloads themselves (sites that need a login), with the folder name to drop them into.
/// </summary>
public sealed class MusicPack
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>Folder name under the plugin's music folder.</summary>
    public string Folder { get; set; } = string.Empty;

    public string Artist { get; set; } = string.Empty;

    public string Licence { get; set; } = string.Empty;

    public string LicenceUrl { get; set; } = string.Empty;

    /// <summary>Credit the licence asks for, as it should appear wherever the music is credited.</summary>
    public string Attribution { get; set; } = string.Empty;

    /// <summary>True when the files cannot be fetched by the plugin (the site needs a login) and the person downloads them.</summary>
    public bool Manual { get; set; }

    public List<MusicPackLink> Links { get; set; } = new();

    public List<MusicPackTrack> Tracks { get; set; } = new();

    public long TotalBytes => Tracks.Sum(t => t.Bytes);

    public int TotalSeconds => Tracks.Sum(t => t.Seconds);
}

public sealed class MusicPackLink
{
    public string Label { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;
}

public sealed class MusicPackTrack
{
    public string Title { get; set; } = string.Empty;

    public string Artist { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    /// <summary>Where the plugin fetches the file from; empty for a manual pack.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Lower-case hex SHA-256 of the file as published; the download is rejected when it differs.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public long Bytes { get; set; }

    public int Seconds { get; set; }

    public string SourcePage { get; set; } = string.Empty;
}

/// <summary>A pack as the editor sees it: the catalogue entry plus what is on disk and whether a download is running.</summary>
public sealed class MusicPackStatusDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Folder { get; set; } = string.Empty;

    /// <summary>The folder's full path on the server, for the manual packs' instructions.</summary>
    public string FolderPath { get; set; } = string.Empty;

    public string Artist { get; set; } = string.Empty;

    public string Licence { get; set; } = string.Empty;

    public string LicenceUrl { get; set; } = string.Empty;

    public string Attribution { get; set; } = string.Empty;

    public bool Manual { get; set; }

    public List<MusicPackLink> Links { get; set; } = new();

    public int TrackCount { get; set; }

    public long TotalBytes { get; set; }

    public int TotalSeconds { get; set; }

    /// <summary>Tracks present and complete in the folder (any audio files, for a manual pack).</summary>
    public int Present { get; set; }

    public bool Installed { get; set; }

    public bool Running { get; set; }

    /// <summary>Tracks finished in the current or last run.</summary>
    public int Downloaded { get; set; }

    public long DownloadedBytes { get; set; }

    public string? Error { get; set; }

    public DateTime? FinishedUtc { get; set; }
}
