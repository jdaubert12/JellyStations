namespace Jellyfin.Plugin.JellyStations.Models;

/// <summary>How the "Up next" card arrives and leaves.</summary>
public enum UpNextCardAnimation
{
    None,

    /// <summary>Fades in and out over a fraction of a second.</summary>
    Fade,

    /// <summary>Slides in from the nearest side edge and back out.</summary>
    Slide,

    /// <summary>Both at once: the usual broadcast look.</summary>
    FadeAndSlide
}

/// <summary>
/// An "Up next" card drawn over the picture shortly before a program ends: the next program's title, its artwork
/// and the time it starts and ends. Burned into the stream by the plugin's ffmpeg, so every viewer sees it and it
/// stays in step with the picture whatever the client's HLS latency. Off by default: like the logo overlay, it
/// costs encoder CPU for the whole program, not just while the card shows.
/// </summary>
public sealed class UpNextCardConfig
{
    public const int DefaultLeadSeconds = 30;
    public const int DefaultShowSeconds = 10;
    public const int DefaultHeightPercent = 14;
    public const int DefaultOpacity = 85;
    public const int MaxLeadSeconds = 6 * 60 * 60;
    public const int MaxLeadTimes = 8;

    public bool Enabled { get; set; }

    public LogoCorner Corner { get; set; } = LogoCorner.BottomRight;

    public UpNextCardAnimation Animation { get; set; } = UpNextCardAnimation.Fade;

    /// <summary>
    /// How long before the program ends the card appears, in seconds, once per entry: 3600, 1800 and 300 shows it
    /// an hour, half an hour and five minutes before the end. 5 s to 6 h each, up to eight, largest first.
    /// </summary>
    public List<int> LeadTimesSeconds { get; set; } = new() { DefaultLeadSeconds };

    /// <summary>How long the card stays each time, 3-60 s (never longer than the shortest lead time).</summary>
    public int ShowSeconds { get; set; } = DefaultShowSeconds;

    /// <summary>Card height as a percentage of the picture height, 8-30. Width follows.</summary>
    public int HeightPercent { get; set; } = DefaultHeightPercent;

    /// <summary>Background opacity in percent, 30-100.</summary>
    public int Opacity { get; set; } = DefaultOpacity;

    /// <summary>Show the next program's poster or episode thumbnail.</summary>
    public bool ShowThumbnail { get; set; } = true;

    /// <summary>Show when the next program starts and ends.</summary>
    public bool ShowTimes { get; set; } = true;

    /// <summary>Times as 20:00 rather than 8:00 PM.</summary>
    public bool TwentyFourHourClock { get; set; }

    /// <summary>The shortest lead time: the card's last showing must end with the program.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int ShortestLeadSeconds => LeadTimesSeconds.Count == 0 ? DefaultLeadSeconds : LeadTimesSeconds.Min();

    public void Normalize()
    {
        LeadTimesSeconds = (LeadTimesSeconds ?? new List<int>())
            .Select(s => Math.Clamp(s, 5, MaxLeadSeconds))
            .Distinct()
            .OrderByDescending(s => s)
            .Take(MaxLeadTimes)
            .ToList();
        if (LeadTimesSeconds.Count == 0)
        {
            LeadTimesSeconds.Add(DefaultLeadSeconds);
        }

        ShowSeconds = Math.Clamp(ShowSeconds, 3, Math.Min(60, ShortestLeadSeconds));
        HeightPercent = Math.Clamp(HeightPercent, 8, 30);
        Opacity = Math.Clamp(Opacity, 30, 100);
        if (!Enum.IsDefined(Corner))
        {
            Corner = LogoCorner.BottomRight;
        }

        if (!Enum.IsDefined(Animation))
        {
            Animation = UpNextCardAnimation.Fade;
        }
    }
}
