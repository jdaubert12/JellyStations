namespace Jellyfin.Plugin.TvStation.Models;

/// <summary>A Jellyfin-free snapshot of a library item, frozen at compile time.</summary>
public sealed class PlayableItem
{
    public Guid ItemId { get; set; }

    public string Path { get; set; } = string.Empty;

    public long RuntimeTicks { get; set; }

    /// <summary>Series name for episodes, title for movies.</summary>
    public string Title { get; set; } = string.Empty;

    public string? EpisodeTitle { get; set; }

    public Guid? SeriesId { get; set; }

    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }

    public bool IsMovie { get; set; }

    public bool IsSeries { get; set; }

    public List<string> Genres { get; set; } = new();

    /// <summary>Library tags; episodes without their own tags carry the series' tags.</summary>
    public List<string> Tags { get; set; } = new();

    public string? Overview { get; set; }

    public int? ProductionYear { get; set; }

    public string? OfficialRating { get; set; }

    public float? CommunityRating { get; set; }

    public string? PrimaryImagePath { get; set; }

    /// <summary>Zero-based index among the file's audio streams to map; null = first audio.</summary>
    public int? AudioStreamIndex { get; set; }

    /// <summary>False when the file is known to have no audio track (silence is generated instead).</summary>
    public bool HasAudio { get; set; } = true;

    public TimeSpan Runtime => TimeSpan.FromTicks(RuntimeTicks);

    public string DisplayTitle => IsSeries && !string.IsNullOrEmpty(EpisodeTitle) ? Title + " - " + EpisodeTitle : Title;
}
