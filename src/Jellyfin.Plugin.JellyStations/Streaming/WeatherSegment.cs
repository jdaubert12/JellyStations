namespace Jellyfin.Plugin.JellyStations.Streaming;

/// <summary>One rendered screen on the air for <see cref="Seconds"/>, with the clock it shows.</summary>
/// <param name="Path">The PNG.</param>
/// <param name="Seconds">How long it stays, including the cross-fade into the next one.</param>
/// <param name="ClockOffsetSeconds">Unix seconds the clock shows at the start of the segment (segment start plus the place's UTC offset).</param>
/// <summary>
/// One screen on air for <paramref name="Seconds"/>: a PNG, or with <paramref name="IsSequence"/> a concat-demuxer
/// list of PNG frames with durations (the radar loop), which must cover the screen's seconds plus the cross-fade.
/// </summary>
public sealed record WeatherSlide(string Path, double Seconds, long ClockOffsetSeconds, bool IsSequence = false);

/// <summary>
/// A stretch of one audio file: seek to <see cref="InPoint"/> and play <see cref="Seconds"/>, <see cref="GainDb"/>
/// louder (its loudness correction), faded in at its start and out at its end when the track really starts or
/// ends there rather than at a segment cut.
/// </summary>
public sealed record WeatherAudio(string Path, double InPoint, double Seconds, double GainDb = 0, bool FadeIn = false, bool FadeOut = false);

/// <summary>A line of text shown over the band from <see cref="From"/> to <see cref="To"/> seconds into the segment.</summary>
public sealed record WeatherCredit(string Text, double From, double To);

/// <summary>
/// One ffmpeg run of a weather channel: a few minutes of screens cross-faded on the clock, music under them,
/// a live clock and the music credit drawn by ffmpeg. Built by the weather service, played by the restreamer.
/// </summary>
public sealed class WeatherSegment
{
    public const double FadeSeconds = 0.5;

    public required DateTime StartUtc { get; init; }

    public required DateTime EndUtc { get; init; }

    public required IReadOnlyList<WeatherSlide> Slides { get; init; }

    public IReadOnlyList<WeatherAudio> Audio { get; init; } = Array.Empty<WeatherAudio>();

    public IReadOnlyList<WeatherCredit> Credits { get; init; } = Array.Empty<WeatherCredit>();

    /// <summary>The font file drawtext uses for the clock and the credit.</summary>
    public required string FontPath { get; init; }

    public bool TwentyFourHourClock { get; init; }

    /// <summary>Music level, 100 = as recorded.</summary>
    public int VolumePercent { get; init; } = 100;

    /// <summary>What the Status page shows while this plays.</summary>
    public string Title { get; init; } = "Local weather";

    public double TotalSeconds => (EndUtc - StartUtc).TotalSeconds;
}
